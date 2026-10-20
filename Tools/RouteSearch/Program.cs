// Hybrid A* prototype over a live-dumped scene (Tools/MemoryProbe --viz TSV).
//
// The plugin's Planner is an analytic maneuver solver: its candidate universe is "direct
// maneuvers between the start pose and the spot pose" (straight / Reeds-Shepp / two-leg), so a
// detour that leaves the straight corridor is topologically impossible - no candidate contains
// the intermediate waypoints a detour needs. Hybrid A* is the standard answer from the literature
// (Dolgov & Thrun, DARPA Urban Challenge 2008-2010; state-lattice planners; today's automated
// parking stacks): search over discretized (x, z, heading) with short motion primitives, guided
// by a 2D obstacle-aware heuristic, and - in production systems - refined by an analytic
// Reeds-Shepp connection to the goal, which is exactly the solver the plugin already has.
//
// The vehicle envelope and the grace rules mirror Planner.CountCorridorConflicts so the result
// is comparable with the in-game 冲突 numbers:
//   envelope length = VehicleLengthM + 0.1 + 2*(EnvelopePadM + ObstacleMarginM) = 8.93 m on the
//                     measured 6x2 (body 8.43 m)
//   envelope width  = VehicleWidthM  + 2*(EnvelopePadM + ObstacleMarginM) = 2.97 m (body 2.47 m)
//   first 2.0 m and last 1.0 m of the route are exempt (StartGraceM / EndGraceM).
//   items inside a detected bay are dropped, as AutoParkingPlugin.AddMemoryObstacles does.
//
// Usage: dotnet run --project Tools/RouteSearch -c Release -- <scene.tsv> <route-out.txt>
//        [--spot X Z HeadingDeg] [--from X Z HeadingDeg] [--env-w W] [--env-l L]
//        [--turn-radius R] [--radius R] [--max-expand N] [--no-reverse] [--no-gear-switch]

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using AutoParking;

string tsvPath = args.Length > 0 ? args[0] : "scene.tsv";
string outPath = args.Length > 1 ? args[1] : "route_ha.txt";

double spotX = 1953.29, spotZ = 2142.61, spotHeadingDeg = 90.0;
double? fromX = null, fromZ = null, fromHeadingDeg = null;
double searchRadius = 150.0;
int maxExpand = 1_200_000;
bool allowReverse = true;   // --no-reverse: forward primitives only, i.e. "can a smooth Dubins-style curve do it?"
bool gearLocked = false;    // --no-gear-switch: stay in whatever gear the start node carries
double envLength = 8.93, envWidth = 2.97;   // measured 6x2 body 8.43x2.47 plus 0.25 m/side of pad
double turnRadiusM = 10.29;                 // wheelbase 4.95 / tan 33deg, times PlanRadiusMargin 1.35

for (int i = 2; i < args.Length; i++)
{
    if (args[i] == "--spot" && i + 3 < args.Length)
    {
        spotX = double.Parse(args[++i], CultureInfo.InvariantCulture);
        spotZ = double.Parse(args[++i], CultureInfo.InvariantCulture);
        spotHeadingDeg = double.Parse(args[++i], CultureInfo.InvariantCulture);
    }
    else if (args[i] == "--radius" && i + 1 < args.Length) searchRadius = double.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--env-w" && i + 1 < args.Length) envWidth = double.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--env-l" && i + 1 < args.Length) envLength = double.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--turn-radius" && i + 1 < args.Length) turnRadiusM = double.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--from" && i + 3 < args.Length)
    {
        fromX = double.Parse(args[++i], CultureInfo.InvariantCulture);
        fromZ = double.Parse(args[++i], CultureInfo.InvariantCulture);
        fromHeadingDeg = double.Parse(args[++i], CultureInfo.InvariantCulture);
    }
    else if (args[i] == "--max-expand" && i + 1 < args.Length) maxExpand = int.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--no-reverse") allowReverse = false;
    else if (args[i] == "--no-gear-switch") gearLocked = true;
}

// ---- parse the scene ---------------------------------------------------------------------------
(double x, double z, double heading) truck = default;
var boxes = new List<(double cx, double cz, double dx, double dz, string kind)>();

foreach (string line in File.ReadAllLines(tsvPath))
{
    string[] p = line.Split('\t');
    if (p[0] == "TRUCK")
    {
        double qw = double.Parse(p[4], CultureInfo.InvariantCulture), qx = double.Parse(p[5], CultureInfo.InvariantCulture);
        double qy = double.Parse(p[6], CultureInfo.InvariantCulture), qz = double.Parse(p[7], CultureInfo.InvariantCulture);
        // The nose is -(R·(0,0,+1)): SCS models face local -Z, so the usual truck_forward expression
        // below is the TAIL. Taking it for the nose starts every search pointed 180° wrong, which is
        // what made a truck parked nose-out of an open lane look trapped in a dead end (design doc
        // §48). Same trap as Geometry.HeadingFromRotationComponent; the harness asserts the sign.
        double fx = 2 * (qw * qy + qx * qz), fz = 1 - 2 * (qx * qx + qy * qy);
        truck = (double.Parse(p[1], CultureInfo.InvariantCulture), double.Parse(p[3], CultureInfo.InvariantCulture),
                 Math.Atan2(-fx, -fz));
    }
    else if (p[0] == "ITEM")
    {
        int type = int.Parse(p[1], CultureInfo.InvariantCulture);
        string name = p[2];
        double cx = double.Parse(p[4], CultureInfo.InvariantCulture), cz = double.Parse(p[6], CultureInfo.InvariantCulture);
        double dx = double.Parse(p[7], CultureInfo.InvariantCulture), dy = double.Parse(p[8], CultureInfo.InvariantCulture);
        double dz = double.Parse(p[9], CultureInfo.InvariantCulture);
        // Field 10 of the viz schema is the flags word; keep 0 for an older/other layout.
        uint flags = p.Length > 10 ? uint.Parse(p[10], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0u;
        if (Math.Abs(cx - truck.x) > searchRadius || Math.Abs(cz - truck.z) > searchRadius)
            continue;

        // Same classification as KdopReader/MemClassifier: cones, boards, movers, buildings AND parked
        // vehicles (0x2F with a bit inside 0x300) block; everything else in the memory list mirrors
        // archive items and is ignored here. Two things this file used to miss: the game's 0xC17
        // parking-marker Movers are marker volumes, not objects - their own boxes contain the goal the
        // plugin resolves, so they must not block here either - and the >20 m cull plus the cone size
        // rule are not decoration: KdopReader drops terrain-scale boxes before they become items, and
        // bay detection only sees MemKind.Cone. Classifying by "dx and dz <= 1.15" instead counted 92
        // cones where the plugin counts 56 on the 2026-10-17 scene and detected no bay at all, so the
        // search judged a different obstacle set than the game would.
        if (Math.Max(dx, Math.Max(dy, dz)) > 20.0)
            continue;   // KdopReader.Decode: terrain-scale boxes never become items

        bool isParkingMarker = type == 0x09 && (flags & 0xC17u) == 0xC17u;
        bool isParkedVehicle = type == 0x2F && (flags & 0x300u) != 0;
        bool blocks = type == 0x02 || type == 0x09 || type == 0x24 || isParkedVehicle;
        if (!blocks || isParkingMarker)
            continue;

        string kind;
        if (type == 0x24)
        {
            float ratio = dz > 0 ? (float)(dx / dz) : 0f;
            kind = dx is >= 0.35f and <= 1.15f && dz is >= 0.35f and <= 1.15f
                   && dy is >= 0.75f and <= 1.4f && ratio is >= 0.55f and <= 1.8f ? "cone" : "board";
        }
        else kind = type == 0x09 ? "mover" : type == 0x2F ? "parked" : "building";

        boxes.Add((cx, cz, Math.Max(dx, 0.3f), Math.Max(dz, 0.3f), kind));
    }
}

if (fromX is double fx2 && fromZ is double fz2 && fromHeadingDeg is double fh)
    truck = (fx2, fz2, fh * Math.PI / 180.0);

// The plugin reads its bays out of the same cone positions, so print them here too: a false bay
// would exempt real obstacles in the planner, and that is not visible in a route verdict.
List<Vector2> conePositions = boxes.Where(b => b.kind == "cone").Select(b => new Vector2((float)b.cx, (float)b.cz)).ToList();
List<ParkingBay> foundBays = ParkingBays.Detect(conePositions);
Console.WriteLine($"车位识别：锥桶 {conePositions.Count} 个 → 认出 {foundBays.Count} 个车位");
foreach (ParkingBay bay in foundBays)
{
    Vector2 nose = Geometry.ForwardFromHeading(bay.HeadingRad);
    Console.WriteLine($"  中心=({bay.Center.X:F1}, {bay.Center.Y:F1}) 内部 {bay.InteriorLengthM:F1}×{bay.InteriorWidthM:F1} m " +
                      $"车头方向=({nose.X:F2}, {nose.Y:F2}) 距车 {Math.Sqrt((bay.Center.X - truck.x) * (bay.Center.X - truck.x) + (bay.Center.Y - truck.z) * (bay.Center.Y - truck.z)):F1} m");
}

// Same exemption as AutoParkingPlugin.AddMemoryObstacles (BayInteriorInsetM there, 0.6 m): anything
// inside a detected bay, with the rectangle shrunk so the cone rows stay walls, is the bay's own
// furniture and not a blocker. Without this line the search judged "forward-only infeasible" against
// an obstacle set the planner stopped using in §50 - the bay markings were refusing the very spot.
const double BayInteriorInsetM = 0.6;
bool InBay((double cx, double cz, double dx, double dz, string kind) b)
    => foundBays.Any(bay => bay.Contains(new Vector2((float)b.cx, (float)b.cz), -BayInteriorInsetM));
int bayExempted = boxes.Count(InBay);
boxes.RemoveAll(InBay);
Console.WriteLine($"车位内部豁免：{bayExempted} 个条目在车位线内、不算障碍，剩 {boxes.Count} 个参与判定");

Console.WriteLine($"scene: truck=({truck.x:F1}, {truck.z:F1}) heading={truck.heading * 180 / Math.PI:0}°  " +
                  $"goal=({spotX:F1}, {spotZ:F1}, {spotHeadingDeg:0}°)  blocking boxes={boxes.Count}");

// Overridable via --env-w / --env-l / --turn-radius. The defaults are Settings.EnvelopeWidthM and
// EnvelopeLengthM (§51: one formula, body + 2*(EnvelopePadM + ObstacleMarginM) on each axis) for the
// truck measured live, and the planning radius the measured wheelbase plus PlanRadiusMargin gives.
// NOTE: these are plain variables read by the offset lattice below - the first attempt at this
// parameter left the consts in place (silent .replace miss) and every --env-w run silently
// kept the old envelope, which looked exactly like "the spot is infeasible".
double EnvLength = envLength, EnvWidth = envWidth;
const double StartGraceM = 2.0, EndGraceM = 1.0;   // same exemptions as the in-game checker
double TurnRadius = turnRadiusM;
const double StepM = 1.0, SubstepM = 0.5;

double minX = Math.Min(truck.x, spotX) - 60, maxX = Math.Max(truck.x, spotX) + 60;
double minZ = Math.Min(truck.z, spotZ) - 60, maxZ = Math.Max(truck.z, spotZ) + 60;

// ---- raw occupancy grid (0.25 m) ----------------------------------------------------------------
const double Cell = 0.25;
int gridW = (int)((maxX - minX) / Cell) + 1, gridH = (int)((maxZ - minZ) / Cell) + 1;
var occupied = new bool[gridW, gridH];
foreach (var b in boxes)
{
    int x0 = Math.Max(0, (int)((b.cx - b.dx / 2 - minX) / Cell)), x1 = Math.Min(gridW - 1, (int)((b.cx + b.dx / 2 - minX) / Cell));
    int z0 = Math.Max(0, (int)((b.cz - b.dz / 2 - minZ) / Cell)), z1 = Math.Min(gridH - 1, (int)((b.cz + b.dz / 2 - minZ) / Cell));
    for (int ix = x0; ix <= x1; ix++)
        for (int iz = z0; iz <= z1; iz++)
            occupied[ix, iz] = true;
}

// Footprint sample offsets in body frame (0.3 m lattice over the envelope rectangle).
var offsets = new List<(double lx, double lz)>();
for (double lx = -EnvLength / 2; lx <= EnvLength / 2 + 1e-9; lx += 0.3)
    for (double lz = -EnvWidth / 2; lz <= EnvWidth / 2 + 1e-9; lz += 0.3)
        offsets.Add((lx, lz));

bool PoseBlocked(double x, double z, double heading)
{
    if (x < minX || x > maxX || z < minZ || z > maxZ)
        return true;

    // forward(h) = (sin h, cos h), right(h) = (cos h, -sin h): world = pos + lx*forward + lz*right.
    // Getting these two lines swapped rotates the envelope 90 degrees and blocks the whole lot
    // (that exact bug is why the first run died after 5 expansions).
    double cos = Math.Cos(heading), sin = Math.Sin(heading);
    foreach ((double lx, double lz) in offsets)
    {
        double wx = x + lx * sin + lz * cos, wz = z + lx * cos - lz * sin;
        int ix = (int)((wx - minX) / Cell), iz = (int)((wz - minZ) / Cell);
        if (ix < 0 || ix >= gridW || iz < 0 || iz >= gridH || occupied[ix, iz])
            return true;
    }

    return false;
}

// --test: probe PoseBlocked on a grid around a point, then exit (collision-model sanity check).
if (args.Length > 0 && args.Contains("--test"))
{
    double tx = double.Parse(args[Array.IndexOf(args, "--test") + 1], CultureInfo.InvariantCulture);
    double tz = double.Parse(args[Array.IndexOf(args, "--test") + 2], CultureInfo.InvariantCulture);
    Console.WriteLine($"PoseBlocked grid around ({tx}, {tz}), heading 90deg, env {envWidth}x{envLength}:");
    for (double dz2 = 3.0; dz2 >= -3.0; dz2 -= 0.5)
    {
        var row = new List<string>();
        for (double dx2 = -3.0; dx2 <= 3.0; dx2 += 0.5)
            row.Add(PoseBlocked(tx + dx2, tz + dz2, Math.PI / 2) ? "X" : ".");
        Console.WriteLine($"  z={tz + dz2,7:F1}  {string.Concat(row)}");
    }

    // Which box does the CENTER pose hit (if any)? Walk the same samples PoseBlocked walks.
    double cos = Math.Cos(Math.PI / 2), sin = Math.Sin(Math.PI / 2);
    for (double lx = -EnvLength / 2; lx <= EnvLength / 2 + 1e-9; lx += 0.3)
    {
        for (double lz = -EnvWidth / 2; lz <= EnvWidth / 2 + 1e-9; lz += 0.3)
        {
            double wx = tx + lx * sin + lz * cos, wz = tz + lx * cos - lz * sin;
            int ix = (int)((wx - minX) / Cell), iz = (int)((wz - minZ) / Cell);
            if (ix < 0 || ix >= gridW || iz < 0 || iz >= gridH || !occupied[ix, iz])
                continue;
            foreach (var b in boxes)
            {
                if (Math.Abs(b.cx - wx) <= b.dx / 2 + Cell && Math.Abs(b.cz - wz) <= b.dz / 2 + Cell)
                    Console.WriteLine($"  center sample lx={lx:F2} lz={lz:F2} -> world ({wx:F2}, {wz:F2}) covered by type={b.kind} @({b.cx:F2}, {b.cz:F2}) size {b.dx:F2}x{b.dz:F2}");
            }
        }
    }

    return 0;
}

bool GraceFree(double x, double z, double heading, double fromX, double fromZ, double graceM)
{
    double d = Math.Sqrt((x - fromX) * (x - fromX) + (z - fromZ) * (z - fromZ));
    return d <= graceM || !PoseBlocked(x, z, heading);
}

// ---- 2D heuristic: BFS from the goal over a 1 m grid, obstacles inflated by half envelope width --
const double HCell = 1.0;
int hW = (int)((maxX - minX) / HCell) + 1, hH = (int)((maxZ - minZ) / HCell) + 1;
var hCost = new double[hW, hH];
for (int ix = 0; ix < hW; ix++)
    for (int iz = 0; iz < hH; iz++)
        hCost[ix, iz] = double.PositiveInfinity;
{
    var blockedH = new bool[hW, hH];
    // RAW obstacles, deliberately not inflated: an inflated heuristic marks a 3.4 m cone gap as
    // dead (it is narrower than the envelope), which blinds the search exactly where the solution
    // runs. The heuristic only points; the envelope collision check does the refusing.
    foreach (var b in boxes)
    {
        int x0 = Math.Max(0, (int)((b.cx - b.dx / 2 - minX) / HCell)), x1 = Math.Min(hW - 1, (int)((b.cx + b.dx / 2 - minX) / HCell));
        int z0 = Math.Max(0, (int)((b.cz - b.dz / 2 - minZ) / HCell)), z1 = Math.Min(hH - 1, (int)((b.cz + b.dz / 2 - minZ) / HCell));
        for (int ix = x0; ix <= x1; ix++)
            for (int iz = z0; iz <= z1; iz++)
                blockedH[ix, iz] = true;
    }

    int gx = Math.Clamp((int)((spotX - minX) / HCell), 0, hW - 1), gz = Math.Clamp((int)((spotZ - minZ) / HCell), 0, hH - 1);
    var queue = new Queue<(int x, int z)>();
    hCost[gx, gz] = 0;
    queue.Enqueue((gx, gz));
    while (queue.Count > 0)
    {
        var (cx, cz) = queue.Dequeue();
        double baseCost = hCost[cx, cz];
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                int nx = cx + dx, nz = cz + dz;
                if (nx < 0 || nx >= hW || nz < 0 || nz >= hH || blockedH[nx, nz]) continue;
                double step = dx != 0 && dz != 0 ? 1.414 : 1.0;
                if (baseCost + step < hCost[nx, nz] - 1e-9)
                {
                    hCost[nx, nz] = baseCost + step;
                    queue.Enqueue((nx, nz));
                }
            }
    }
}

double Heuristic(double x, double z)
{
    int hx = (int)((x - minX) / HCell), hz = (int)((z - minZ) / HCell);
    double h = hx >= 0 && hx < hW && hz >= 0 && hz < hH ? hCost[hx, hz] : double.PositiveInfinity;
    if (double.IsInfinity(h))
        h = Math.Sqrt((x - spotX) * (x - spotX) + (z - spotZ) * (z - spotZ));
    return h;
}

// ---- Hybrid A* ----------------------------------------------------------------------------------
const int ThetaBins = 72;
double goalHeading = spotHeadingDeg * Math.PI / 180.0;

static int ThetaBin(double heading)
{
    int bin = (int)Math.Round(heading / (2 * Math.PI) * ThetaBins) % ThetaBins;
    return bin < 0 ? bin + ThetaBins : bin;
}

var explored = new List<(double x, double z)>();
var nodes = new Dictionary<(int, int, int), Node>();
var open = new PriorityQueue<Node, double>();
var closed = new HashSet<(int, int, int)>();

Node start = new(truck.x, truck.z, truck.heading, 1, 0.0, null, null);
start.Key = KeyOf(start);
nodes[start.Key] = start;
open.Enqueue(start, Heuristic(start.X, start.Z));

static (int, int, int) KeyOf(Node n) =>
    ((int)Math.Round(n.X * 4), (int)Math.Round(n.Z * 4), ThetaBin(n.Heading));

var sw = Stopwatch.StartNew();
Node? goalNode = null;
int expansions = 0;

while (open.Count > 0 && expansions < maxExpand && sw.Elapsed.TotalSeconds < 30)
{
    Node current = open.Dequeue();
    if (closed.Contains(current.Key))
        continue;
    closed.Add(current.Key);
    expansions++;
    if (expansions % 20 == 0)
        explored.Add((current.X, current.Z));

    double distToGoal = Math.Sqrt((current.X - spotX) * (current.X - spotX) + (current.Z - spotZ) * (current.Z - spotZ));
    double headingError = Math.Abs(Normalize(current.Heading - goalHeading));
    if (distToGoal <= 0.45 && headingError <= 6 * Math.PI / 180)
    {
        goalNode = current;
        break;
    }

    for (int primitive = 0; primitive < (allowReverse ? 10 : 5); primitive++)
    {
        // Five steer settings (full left, half left, straight, half right, full right) x two
        // gears. The half-steer set exists because threading a 3.4 m gap needs heading control
        // finer than the full-lock step (2 m at full lock was 13.8 degrees per primitive - the
        // 90+/-2 degree corridor mouth was not representable and the search died on it).
        byte gear = primitive < 5 ? (byte)1 : (byte)2;
        if (gearLocked && gear != current.Gear)
            continue;
        double dir = gear == 1 ? 1 : -1;
        double turnSign = (primitive % 5) switch { 0 => -1.0, 1 => -0.5, 2 => 0.0, 3 => 0.5, _ => 1.0 };
        double dTheta = dir * turnSign * StepM / TurnRadius;

        double x = current.X, z = current.Z, heading = current.Heading;
        bool clear = true;
        var trail = new List<(double x, double z, double h)>();
        for (double s = SubstepM; s <= StepM + 1e-9; s += SubstepM)
        {
            heading = current.Heading + dTheta * (s / StepM);
            if (turnSign == 0)
            {
                x = current.X + dir * s * Math.Sin(current.Heading);
                z = current.Z + dir * s * Math.Cos(current.Heading);
            }
            else
            {
                // Circle closed form for forward(h) = (sin h, cos h): with signed radius
                // r = StepM / dTheta, moving from h0 to h along the arc.
                double r = StepM / dTheta;
                x = current.X + r * (Math.Cos(current.Heading) - Math.Cos(heading));
                z = current.Z + r * (Math.Sin(current.Heading) - Math.Sin(heading));
            }

            trail.Add((x, z, heading));
            if (!GraceFree(x, z, heading, truck.x, truck.z, StartGraceM)
                && !GraceFree(x, z, heading, spotX, spotZ, EndGraceM))
            {
                clear = false;
                break;
            }
        }

        if (!clear)
            continue;

        var node = new Node(x, z, heading, gear,
                            current.G + StepM * (gear == 2 ? 1.2 : 1.0)
                            + (gear != current.Gear ? 6.0 : 0.0),
                            current, trail);
        node.Key = KeyOf(node);
        if (closed.Contains(node.Key))
            continue;
        if (nodes.TryGetValue(node.Key, out Node? seen) && seen.G <= node.G)
            continue;

        nodes[node.Key] = node;
        open.Enqueue(node, node.G + Heuristic(x, z));
    }
}

sw.Stop();
Console.WriteLine($"hybrid A*: expansions={expansions} time={sw.ElapsedMilliseconds} ms open={open.Count}");

if (goalNode == null)
{
    Console.WriteLine("RESULT: infeasible - no collision-free maneuver sequence reaches the spot within the limits");
    Console.WriteLine($"direct-line conflicts={DirectLineConflicts()}");
    var failLines = new List<string>
    {
        $"INFEASIBLE\texpansions={expansions}\ttimeMs={sw.ElapsedMilliseconds}\tdirectConflicts={DirectLineConflicts()}"
    };
    foreach ((double ex, double ez) in explored)
        failLines.Add($"EXP\t{ex.ToString("F1", CultureInfo.InvariantCulture)}\t{ez.ToString("F1", CultureInfo.InvariantCulture)}");
    File.WriteAllLines(outPath, failLines);
    return 2;
}

// ---- reconstruct + report -----------------------------------------------------------------------
var path = new List<(double x, double z, double h, byte gear)>();
for (Node? n = goalNode; n != null; n = n.Parent)
{
    if (n.Trail != null)
        for (int i = n.Trail.Count - 1; i >= 0; i--)
            path.Add((n.Trail[i].x, n.Trail[i].z, n.Trail[i].h, n.Gear));
}

path.Reverse();
var outLines = new List<string>
{
    $"ROUTE\tlength={path.Count * SubstepM:0.0} m\tgearSwitches={CountSwitches()}\texpansions={expansions}\ttimeMs={sw.ElapsedMilliseconds}",
    $"DIRECT\tconflicts={DirectLineConflicts()}"
};
foreach ((double ex, double ez) in explored)
    outLines.Add($"EXP\t{ex.ToString("F1", CultureInfo.InvariantCulture)}\t{ez.ToString("F1", CultureInfo.InvariantCulture)}");
foreach ((double x, double z, double h, byte gear) in path)
    outLines.Add($"PT\t{x.ToString("F2", CultureInfo.InvariantCulture)}\t{z.ToString("F2", CultureInfo.InvariantCulture)}\t{h.ToString("F4", CultureInfo.InvariantCulture)}\t{gear}");
File.WriteAllLines(outPath, outLines);

int conflicts = CountConflicts();
Console.WriteLine($"RESULT: length={path.Count * SubstepM:0.0} m gearSwitches={CountSwitches()} conflicts={conflicts}");
Console.WriteLine($"route written to {outPath}");
return 0;

int CountSwitches()
{
    int switches = 0;
    for (int i = 1; i < path.Count; i++)
        if (path[i].gear != path[i - 1].gear) switches++;
    return switches;
}

// Swept-envelope conflict count over the found route, same grace rules as the in-game checker.
int CountConflicts()
{
    int conflicts = 0;
    double total = path.Count * SubstepM;
    for (double along = StartGraceM; along <= total - EndGraceM; along += 0.5)
    {
        int idx = Math.Min((int)(along / SubstepM), path.Count - 1);
        var (x, z, h, _) = path[idx];
        if (PoseBlocked(x, z, h))
            conflicts++;
    }

    return conflicts;
}

// Conflict count of the naive direct line truck->spot, same envelope and grace rules - the
// number an analytic "direct maneuver" candidate family cannot beat when it is nonzero.
int DirectLineConflicts()
{
    double dx = spotX - truck.x, dz = spotZ - truck.z;
    double length = Math.Sqrt(dx * dx + dz * dz);
    double heading = Math.Atan2(dx, dz);
    int conflicts = 0;
    for (double along = StartGraceM; along <= length - EndGraceM; along += 0.5)
    {
        double t = along / length;
        if (PoseBlocked(truck.x + dx * t, truck.z + dz * t, heading))
            conflicts++;
    }

    return conflicts;
}

static double Normalize(double a)
{
    a %= 2 * Math.PI;
    if (a > Math.PI) a -= 2 * Math.PI;
    if (a < -Math.PI) a += 2 * Math.PI;
    return a;
}

internal sealed class Node
{
    public (int, int, int) Key;
    public readonly double X, Z, Heading, G;
    public readonly byte Gear;
    public readonly Node? Parent;
    public readonly List<(double x, double z, double h)>? Trail;

    public Node(double x, double z, double heading, byte gear, double g, Node? parent,
                List<(double x, double z, double h)>? trail)
    {
        X = x; Z = z; Heading = heading; Gear = gear; G = g; Parent = parent; Trail = trail;
    }
}
