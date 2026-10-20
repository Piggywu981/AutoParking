// Live memory probe for the cone-field question (design doc §38 addendum).
//
// The offline archive reads and the overlay agree: at the cone field there are no cone items in
// /map/usa.mbd. This probe reads the running game from outside the process and asks the game's
// own question instead: does base_ctrl_u's nearby kdop-item list contain items (any item class)
// there that the archive data lacks? If yes, the cones are runtime objects and the memory route
// is real; if no, they are pure render geometry.
//
// Resolution logic is lifted from the public ets2la_plugin source:
//   patterns.win32.hpp  - signatures
//   base_ctrl.cpp       - instance pointer via RIP-relative disp, struct offsets from disp32
//   kdop_item.hpp       - item layout: vptr@00, item_type@0x0A, kdop min/max @0x0C..0x34, uid@0x40
//   array_dyn.hpp (Win) - vptr@00, value@0x08, size@0x10, capacity@0x18, allocator@0x20
//
// Usage:
//   dotnet run --project Tools/MemoryProbe -c Release -- [pid|name] [--near X Z [R]] [--array HEXOFF]... [--strings token]...
//     [--deep X Z [R]]      one human-readable block per kdop item near (X, Z)
//     [--deep-tsv X Z [R]]  same walk, stdout carries ONLY machine-readable records:
//         ITEM\t<type:int>\t<uid:0xHEX>\t<flags:0xHEX>\t<x>\t<y>\t<z>\t<dx>\t<dy>\t<dz>
//         STR\t<uid:0xHEX>\t<ascii-run>
//       One record per line, CRLF-terminated like any Console output on Windows, every byte ASCII,
//       floats InvariantCulture "0.###". Narration is suppressed there so nothing has to be filtered.
//       STR carries every string the 串 block prints for that item: runs reached through a pointer keep
//       their @+0xNN provenance label, runs read inline are bare. Consumers match with substring tests.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

string procName = "amtrucks";
int? pidArg = null;
double? nearX = null, nearZ = null, nearR = 150;
var stringTokens = new List<string>();
var strctxTokens = new List<string>();
var extraArrays = new List<long>();
double vizRadius = -1;
bool telemetryOnly = false;
bool wheelsOnly = false;
int bodyOffset = -1;
double trafficSeconds = 0;
double deepX = double.NaN, deepZ = double.NaN, deepR = 25;
bool deepTsv = false;

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    if (a == "--near" && i + 2 < args.Length)
    {
        nearX = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
        nearZ = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
        if (i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var r)) { nearR = r; i++; }
    }
    else if (a == "--strings") { while (i + 1 < args.Length && !args[i + 1].StartsWith("--")) stringTokens.Add(args[++i]); }
    else if (a == "--strctx") { while (i + 1 < args.Length && !args[i + 1].StartsWith("--")) strctxTokens.Add(args[++i]); }
    else if (a == "--array" && i + 1 < args.Length) extraArrays.Add(Convert.ToInt64(args[++i], 16));
    else if (a == "--viz") { vizRadius = 100; if (i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var r)) { vizRadius = r; i++; } }
    else if (a == "--telemetry") { telemetryOnly = true; }
    else if ((a == "--deep" || a == "--deep-tsv") && i + 2 < args.Length)
    {
        deepTsv = a == "--deep-tsv";
        deepX = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
        deepZ = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
        if (i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var rr)) { deepR = rr; i++; }
    }
    else if (a == "--wheels") { wheelsOnly = true; }
    else if (a == "--body") { bodyOffset = 0x18; }
    else if (a == "--traffic")
    {
        trafficSeconds = 3;
        if (i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var ts)) { trafficSeconds = ts; i++; }
    }
    else if (int.TryParse(a, out var p)) pidArg = p;
    else procName = a;
}

if (trafficSeconds > 0)
{
    DoTraffic(trafficSeconds);
    return;
}

if (wheelsOnly)
{
    DoWheels();
    return;
}

if (telemetryOnly)
{
    DoTelemetry();
    return;
}

var proc = pidArg is int pid ? Process.GetProcessById(pid) : Process.GetProcessesByName(procName).FirstOrDefault()
           ?? throw new Exception($"process '{procName}' not running");
proc.Refresh();
var mod = proc.MainModule ?? throw new Exception("no main module");
long imageBase = mod.BaseAddress.ToInt64();
int imageSize = (int)mod.ModuleMemorySize;
if (!deepTsv) Console.WriteLine($"process: {proc.ProcessName} pid={proc.Id}  module: {mod.ModuleName} base=0x{imageBase:X} size=0x{imageSize:X}");

var h = Native.OpenProcess(0x0410 /* QUERY_INFORMATION | VM_READ */, false, proc.Id);
if (h == IntPtr.Zero) throw new Exception($"OpenProcess failed win32={Marshal.GetLastWin32Error()} (elevate the shell if this persists)");

// --strctx only needs the process handle: scan committed memory for each token and print the
// ASCII context around every hit. Trigger ordering lives exactly there - the state machine
// ("state park_trigger_on_stopped") and its siblings are laid out as one script blob, so the
// bytes around a hit are the sibling states / script that references the trigger uid.
if (strctxTokens.Count > 0)
{
    ScanStrContext(h, strctxTokens);
    return;
}

// ---- patterns. nearby_kdop_items / nearby_non_ai_vehicles are byte-identical between the
// source clone and the shipped 1.61 SDK dll; the base_ctrl instance pattern is NOT - 1.61 ships
// the relaxed "48 8b ? ? ? ? ? 48 8b ? 48 8b ? 48 8b 80" (extracted from
// current\Assets\SDKs\1.61\Windows\ets2la_plugin.dll .rdata). The relaxed form can false-match,
// so every hit is decoded and validated; instance ptr and game-actor offsets come from decoding,
// not hardcoded positions.
byte[] image = ReadBytes(h, imageBase, imageSize);
if (!deepTsv) Console.WriteLine($"module image read: {image.Length / (1024 * 1024)} MB");

var baseCtrlCandidates = new List<(long insn, long instPtr, ulong instance, int gameActorOffset)>();
foreach (long insn in FindAllPattern(image, imageBase, "48 8b ? ? ? ? ? 48 8b ? 48 8b ? 48 8b 80"))
{
    byte modrm = image[insn + 2 - imageBase];
    if ((modrm & 0xC7) != 0x05) continue; // first instruction must be mov r64,[rip+disp32]
    long instPtr = RipRel(insn, 3, image, imageBase);
    ulong instance;
    try { instance = ReadU64(h, instPtr); } catch { continue; }
    int ga = DecodeLastDisp32(image, (int)(insn - imageBase), 21);
    if (ga < 0) continue;
    baseCtrlCandidates.Add((insn, instPtr, instance, ga));
}
if (!deepTsv)
{
    Console.WriteLine($"base_ctrl pattern: {baseCtrlCandidates.Count} hit(s)");
    foreach (var c in baseCtrlCandidates)
        Console.WriteLine($"  insn=0x{c.insn:X} (+0x{c.insn - imageBase:X})  instance_ptr=0x{c.instPtr:X} -> 0x{c.instance:X}  game_actor_offset=0x{c.gameActorOffset:X}");
}

long sKdop = FindPattern(image, imageBase, "4c 8d 81 ?? ?? ?? ?? 0f 29 ?? ?? 48 8d 91");
if (sKdop == 0) throw new Exception("nearby_kdop_items pattern not found");
int nearbyKdopOffset = I32(image, sKdop + 3 - imageBase);

long sNonAi = FindPattern(image, imageBase, "f0 0f c1 42 ?? 48 8d b9 ?? ?? ?? ?? 48 8b 07");
int nonAiOffset = sNonAi != 0 ? I32(image, sNonAi + 8 - imageBase) : 0;
if (!deepTsv) Console.WriteLine($"nearby_kdop_items_offset=0x{nearbyKdopOffset:X}  nearby_non_ai_vehicles_offset=0x{nonAiOffset:X} (insn 0x{sKdop:X} +0x{sKdop - imageBase:X})");

var candidate = baseCtrlCandidates.FirstOrDefault(c => c.instance != 0);
if (candidate.instance == 0) throw new Exception("no base_ctrl candidate with a non-null instance - game not in a driving state?");
ulong baseCtrl = candidate.instance;
if (!deepTsv) Console.WriteLine($"base_ctrl_u* = 0x{baseCtrl:X}");

// ---- kdop item arrays. item_type numbering matches TruckLib ItemType (design doc §38).
// The pattern-resolved offset is only a CANDIDATE in 1.61 - sweep every base_ctrl member that
// looks like array_dyn_t<kdop_item_t*>. A real item element points to a C++ object whose vptr
// lands inside the game module, and its kdop floats look like world coords.
if (bodyOffset >= 0)
{
    DoBody(candidate.instance, candidate.gameActorOffset);
    return;
}
if (vizRadius > 0)
{
    DoViz(candidate.instance, vizRadius);
    if (stringTokens.Count > 0) ScanStrings(h, stringTokens);
    return;
}
if (!double.IsNaN(deepX))
{
    DoDeep(candidate.instance, deepX, deepZ, deepR, deepTsv);
    return;
}
ScanBaseCtrlForKdopArrays(candidate.instance);

// ---- heap string scan as corroboration: are cone models even loaded right now?
if (stringTokens.Count > 0) ScanStrings(h, stringTokens);

return;

// ================================================================================================

// --body: how big is the truck the player is actually sitting in? The game answers this itself:
// traffic.cpp:29-31 takes width = |aabox.start.x - end.x|, height = |...y...|, length = |...z...|
// from the vehicle's local axis-aligned box, and uses it to centre every traffic vehicle on the
// map. The chain is all pattern-decoded or fixed in the 1.61 source: base_ctrl + game_actor_offset
// -> game_actor_u, +0x18 -> game_physics_vehicle_u (physics_vehicle_u : vehicle_shared_u), whose
// aabox_t is at +0x18 (start float3, end float3). This is the only source that gives the real
// body length - the telemetry publishes wheel centres and no dimension at all.
void DoBody(ulong basePtr, int gameActorOffset)
{
    ulong actor = ReadU64(h, (long)basePtr + gameActorOffset);
    Console.WriteLine($"base_ctrl=0x{basePtr:X} +0x{gameActorOffset:X} -> game_actor=0x{actor:X}");
    if (actor == 0) { Console.WriteLine("game_actor 为空 - 不在驾驶状态？"); return; }
    ulong truck = ReadU64(h, (long)actor + 0x18);
    Console.WriteLine($"game_actor+0x18 -> game_physics_vehicle=0x{truck:X}");
    if (truck == 0) { Console.WriteLine("车辆指针为空"); return; }
    byte[] bb = ReadBytes(h, (long)truck + 0x18, 0x18);
    float sx = BitConverter.ToSingle(bb, 0), sy = BitConverter.ToSingle(bb, 4), sz = BitConverter.ToSingle(bb, 8);
    float ex = BitConverter.ToSingle(bb, 12), ey = BitConverter.ToSingle(bb, 16), ez = BitConverter.ToSingle(bb, 20);
    Console.WriteLine($"aabox @ truck+0x18: start=({sx:F2}, {sy:F2}, {sz:F2}) end=({ex:F2}, {ey:F2}, {ez:F2})");
    Console.WriteLine($"按游戏自己的算法（traffic.cpp:29-31）：长 {Math.Abs(ex - sx):F2}(x) 高 {Math.Abs(ey - sy):F2}(y) 长 {Math.Abs(ez - sz):F2}(z) m");
    Console.WriteLine("注：aabox 是局部包围盒，x=横向、y=高度、z=纵向，所以长度取 |Δz|、宽度取 |Δx|。");
}

// ================================================================================================

// --wheels: read the per-wheel positions the game publishes for the truck the player is standing in.
// The host maps them as three float[16] planes (configVector.truckWheelPositions, read at
// Telemetry/Program.cs:513-521: X plane, then +64 for Y, then +128 for Z) and AutoParking already
// measures the wheelbase out of them - but the vehicle WIDTH is still a hand-typed 2.5 m, which is
// what the obstacle envelope is built from. This prints the track so the width can be judged against
// the truck that is actually being driven.
void DoWheels()
{
    byte[]? buf = null;
    foreach (int size in new[] { 16384, 4096 })
    {
        try
        {
            using var mm = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(@"Local\SCSTelemetry", System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
            using var acc = mm.CreateViewAccessor(0, size, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            buf = new byte[size];
            acc.ReadArray(0, buf, 0, size);
            break;
        }
        catch (Exception e) { Console.WriteLine($"  size {size}: {e.GetType().Name} {e.Message}"); }
    }
    if (buf is null) { Console.WriteLine("SCSTelemetry unreadable"); return; }

    int hits = 0;
    for (int o = 0; o + 192 <= buf.Length; o += 4)
    {
        float[] xs = Plane(buf, o), ys = Plane(buf, o + 64), zs = Plane(buf, o + 128);
        int n = NonZeroCount(xs);
        if (n < 2 || n > 8 || n != NonZeroCount(ys) || n != NonZeroCount(zs)) continue;
        if (ys.Take(n).Any(v => Math.Abs(v) > 3f || Math.Abs(v) < 0.05f)) continue;

        double alongSpread = Math.Abs((double)zs.Take(n).Max() - zs.Take(n).Min());
        double crossSpread = Math.Abs((double)xs.Take(n).Max() - xs.Take(n).Min());
        if (alongSpread is < 1.0 or > 9.0 || crossSpread is < 0.8 or > 4.0) continue;

        hits++;
        Console.WriteLine($"\ncandidate at +0x{o:X}  轮数={n}");
        for (int i = 0; i < n; i++)
            Console.WriteLine($"  轮 {i}: 横向 {xs[i]:+0.00;-0.00}  高度 {ys[i]:0.00}  纵向 {zs[i]:+0.00;-0.00}");
        double track = crossSpread;
        var groups = zs.Take(n).OrderBy(v => v).ToList();
        Console.WriteLine($"  轮距（左右轮心距）= {track:0.00} m，最外轮心横向 ±{Math.Max(Math.Abs(xs.Take(n).Min()), Math.Abs(xs.Take(n).Max())):0.00} m");
        Console.WriteLine($"  纵向跨度（≈轴距）= {alongSpread:0.00} m；轴位 {string.Join(", ", groups.Select(v => v.ToString("0.00")))}");
        Console.WriteLine($"  → 轮心算出的车宽下限 {track:0.00} m；再算上轮胎本身才算完整宽度，轮胎宽度这一段遥测不发布");
        if (hits >= 4) return;
    }
    if (hits == 0) Console.WriteLine("没找到车轮块（签名不匹配）");

    static float[] Plane(byte[] b, int at)
    {
        var a = new float[16];
        for (int i = 0; i < 16; i++) a[i] = BitConverter.ToSingle(b, at + i * 4);
        return a;
    }

    static int NonZeroCount(float[] a) => a.Count(v => v != 0f);
}

// ================================================================================================

// --traffic: the convention-free nose test. An AI vehicle that is driving forward has just told us
// where its nose is: the direction it moved. Slots are reused between frames, so pair by the game's
// own id and throw away anything that would need an impossible speed - pairing by index "measured"
// cars moving 2 km in six seconds. Layout: TrafficVehicle in core.hpp, x,y,z @0..8, qw,qx,qy,qz
// @12..24, length @36, speed @40, id @50; 54-byte vehicle in a 174-byte slot, 40 slots.
void DoTraffic(double seconds)
{
    const int Slot = 174;
    byte[]? first = ReadSegment(6960);
    if (first is null) return;
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    byte[]? second = ReadSegment(6960);
    if (second is null) return;

    var byId = new Dictionary<short, (float x, float z, float qw, float qx, float qy, float qz)>();
    for (int i = 0; i < 40; i++)
    {
        int o = i * Slot;
        float len = BitConverter.ToSingle(second, o + 36);
        short id = BitConverter.ToInt16(second, o + 50);
        if (len <= 0 || id == 0) continue;
        byId[id] = (BitConverter.ToSingle(second, o), BitConverter.ToSingle(second, o + 8),
                    BitConverter.ToSingle(second, o + 12), BitConverter.ToSingle(second, o + 16),
                    BitConverter.ToSingle(second, o + 20), BitConverter.ToSingle(second, o + 24));
    }

    int moved = 0, plus = 0, minus = 0;
    for (int i = 0; i < 40; i++)
    {
        int o = i * Slot;
        float len = BitConverter.ToSingle(first, o + 36);
        short id = BitConverter.ToInt16(first, o + 50);
        if (len <= 0 || id == 0 || !byId.TryGetValue(id, out var now)) continue;
        double dx = now.x - BitConverter.ToSingle(first, o);
        double dz = now.z - BitConverter.ToSingle(first, o + 8);
        double dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist < 1.5 || dist > seconds * 25) continue;
        double fx = 2.0 * (now.qw * now.qy + now.qx * now.qz), fz = 1.0 - 2.0 * (now.qx * now.qx + now.qy * now.qy);
        double dot = (dx * fx + dz * fz) / dist;
        moved++;
        if (dot > 0.7) plus++;
        else if (dot < -0.7) minus++;
        Console.WriteLine($"  id={id} 移动 {dist:F1} m · 行进方向=({dx / dist:F2}, {dz / dist:F2}) · R·(0,0,+1)=({fx:F2}, {fz:F2}) · " +
                          $"点积 {dot:F2} → {(dot > 0 ? "行进方向与 R·(0,0,+1) 同向" : "行进方向与 R·(0,0,+1) 反向")}");
    }
    Console.WriteLine($"\n移动过的 AI 车 {moved} 辆：{plus} 辆与 R·(0,0,+1) 同向，{minus} 辆反向。");
    if (moved == 0)
        Console.WriteLine("这一带没有 AI 车在动（封闭练习场常见），判不了。");

    byte[]? ReadSegment(int size)
    {
        foreach (var name in new[] { @"Local\ETS2LATraffic", "ETS2LATraffic" })
        {
            try
            {
                using var mm = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(name, System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
                using var acc = mm.CreateViewAccessor(0, size, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                var b = new byte[size];
                acc.ReadArray(0, b, 0, size);
                return b;
            }
            catch (Exception e) { Console.WriteLine($"{name}: {e.GetType().Name} {e.Message}"); }
        }
        return null;
    }
}

// ================================================================================================

// --telemetry: what does the number the plugin steers by actually point at? AutoParkingPlugin
// takes truckPlacement.rotation.X as the heading; the injected game-side plugin publishes the truck
// quaternion for the same frame (Local\ETS2LACameraProps, truck_rot @112..124 in core.hpp). The
// quaternion alone cannot settle it - that is the whole lesson of §47, which read R·(0,0,+1) as the
// nose when SCS models face local -Z. So print both, and let the operator's eyes close the loop.
void DoTelemetry()
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    float tx = float.NaN, ty = float.NaN, tz = float.NaN, qw = 1, qx = 0, qy = 0, qz = 0;
    System.IO.MemoryMappedFiles.MemoryMappedFile? cam = null;
    foreach (var name in new[] { "ETS2LACameraProps", @"Local\ETS2LACameraProps" })
    {
        try { cam = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(name, System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read); break; }
        catch { }
    }
    if (cam is null) { Console.WriteLine("ETS2LACameraProps not openable - is the game running with the SDK plugin?"); return; }
    using (var acc = cam.CreateViewAccessor(0, 128, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read))
    {
        acc.Read(100, out tx); acc.Read(104, out ty); acc.Read(108, out tz);
        acc.Read(112, out qw); acc.Read(116, out qx); acc.Read(120, out qy); acc.Read(124, out qz);
    }
    Console.WriteLine($"truck=({tx:F2}, {ty:F2}, {tz:F2}) quat=(w {qw:F6}, x {qx:F6}, y {qy:F6}, z {qz:F6})");

    double tailX = 2 * (qw * qy + qx * qz), tailZ = 1 - 2 * (qx * qx + qy * qy);
    Console.WriteLine($"R·(0,0,+1)=({tailX:F4}, {tailZ:F4}) —— SCS 模型车头在本地 -Z，这个向量是车尾");
    Console.WriteLine($"真实车头 = ({-tailX:F4}, {-tailZ:F4})，本仓库约定 heading {Math.Atan2(tailX, tailZ) * 180 / Math.PI:F2}°");
    float camx = 0, camy = 0, camz = 0;
    using (var acc2 = cam.CreateViewAccessor(0, 128, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read))
    {
        acc2.Read(4, out camx); acc2.Read(8, out camy); acc2.Read(12, out camz);
    }
    Console.WriteLine($"  相机字段 @4..12 = ({camx:F1}, {camy:F1}, {camz:F1})，离车 " +
                      $"{Math.Sqrt((camx - tx) * (camx - tx) + (camz - tz) * (camz - tz)):F0} m —— 1.61 布局与源码克隆不符，" +
                      "\"尾随相机在车尾\"这条自动判据不可用（§48 试过，作废）。");

    byte[]? buf = null;
    foreach (int size in new[] { 65536, 16384, 4096 })
    {
        try
        {
            using var mm = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(@"Local\SCSTelemetry", System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
            using var acc = mm.CreateViewAccessor(0, size, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            buf = new byte[size];
            acc.ReadArray(0, buf, 0, size);
            Console.WriteLine($"SCSTelemetry: read {size} bytes");
            break;
        }
        catch (Exception e) { Console.WriteLine($"  size {size}: {e.GetType().Name} {e.Message}"); }
    }
    if (buf is null) { Console.WriteLine("SCSTelemetry unreadable"); return; }

    int found = 0;
    for (int i = 0; i + 48 <= buf.Length; i += 8)
    {
        double px = BitConverter.ToDouble(buf, i), py = BitConverter.ToDouble(buf, i + 8), pz = BitConverter.ToDouble(buf, i + 16);
        if (Math.Abs(px - tx) > 0.6 || Math.Abs(pz - tz) > 0.6 || py < -60 || py > 600) continue;
        double r0 = BitConverter.ToDouble(buf, i + 24), r1 = BitConverter.ToDouble(buf, i + 32), r2 = BitConverter.ToDouble(buf, i + 40);
        Console.WriteLine($"\nposition match at +0x{i:X}: coord=({px:F4}, {py:F4}, {pz:F4})");
        Console.WriteLine($"  宿主映射为 truckPlacement.rotation 的三个 double: {r0.ToString("F6", inv)}, {r1.ToString("F6", inv)}, {r2.ToString("F6", inv)}" +
                          $"（X = {r0 * 360:F2}°，宿主 Vehicles.cs:161 用 *360 当度数 → 单位是圈）");
        foreach ((string label, double heading) in new (string, double)[]
                 { ("shipped raw yaw", r0 * 2 * Math.PI), ("§47 yaw+180", (r0 + 0.5) * 2 * Math.PI) })
        {
            double pfx = -Math.Sin(heading), pfz = -Math.Cos(heading);
            double diff = Math.Atan2(pfx * -tailZ - pfz * -tailX, pfx * -tailX + pfz * -tailZ) * 180 / Math.PI;
            Console.WriteLine($"  {label,-14}: heading {heading * 180 / Math.PI,8:F2}°  forward=({pfx:F4}, {pfz:F4})  " +
                              $"与真实车头相差 {diff,7:F2}°  {(Math.Abs(diff) < 20 ? "✓ 一致" : "✗ 车头/车尾对调")}");
        }
        if (++found == 2) break;
    }
    if (found == 0) Console.WriteLine("position triple not found in the segment - layout differs from the host reader's assumptions");
}

// ================================================================================================

// --viz: truck pose comes from the injected ets2la_plugin's shared memory (Local\ETS2LACameraProps,
// CameraMemData in core.hpp: truck_pos @100..108, truck_rot quat @112..124). The item universe is
// the MERGE of base_ctrl+0x10 (archive items + some runtime items) and +0x650 (the truck-adjacent
// runtime rows live ONLY here - §41), deduplicated by uid. Output is TSV for the matplotlib plot.
void DoViz(ulong basePtr, double radius)
{
    float tx = float.NaN, ty = float.NaN, tz = float.NaN, qw = 0, qx = 0, qy = 0, qz = 0;
    try
    {
        System.IO.MemoryMappedFiles.MemoryMappedFile? mm = null;
        foreach (var name in new[] { "ETS2LACameraProps", @"Local\ETS2LACameraProps" })
        {
            try { mm = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(name, System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read); break; }
            catch { }
        }
        using var acc = mm?.CreateViewAccessor(0, 128, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
        if (acc != null)
        {
            acc.Read(100, out tx); acc.Read(104, out ty); acc.Read(108, out tz);
            acc.Read(112, out qw); acc.Read(116, out qx); acc.Read(120, out qy); acc.Read(124, out qz);
        }
    }
    catch (Exception e) { Console.WriteLine($"camera shared memory unreadable: {e.Message}"); }
    Console.WriteLine($"TRUCK\t{tx.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}\t{ty.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}\t{tz.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}\t{qw:G9}\t{qx:G9}\t{qy:G9}\t{qz:G9}");

    if (float.IsNaN(tx) && nearX is double nx0 && nearZ is double nz0) { tx = (float)nx0; tz = (float)nz0; Console.WriteLine($"camera memory unavailable - falling back to ({nx0}, {nz0})"); }
    if (float.IsNaN(tx)) throw new Exception("no truck position: camera shared memory missing and no --near given");

    var seen = new HashSet<ulong>();
    int collected = 0;
    foreach (long off in new long[] { 0x10, 0x650 })
    {
        byte[] head = ReadBytes(h, (long)basePtr + off, 0x28);
        ulong value = BitConverter.ToUInt64(head, 0x08);
        ulong size = BitConverter.ToUInt64(head, 0x10);
        if (value == 0 || size == 0 || size > 200_000) continue;
        byte[] ptr8 = new byte[8], it = new byte[0x48];
        for (ulong i = 0; i < size; i++)
        {
            try
            {
                Native.ReadProcessMemory(h, (long)value + (long)i * 8, ptr8, 8, out _);
                ulong elem = BitConverter.ToUInt64(ptr8, 0);
                if (!seen.Add(elem) ) continue;
                if (!Native.ReadProcessMemory(h, (long)elem, it, 0x48, out _)) continue;
            }
            catch { continue; }
            float minx = BitConverter.ToSingle(it, 0x0C), miny = BitConverter.ToSingle(it, 0x10), minz = BitConverter.ToSingle(it, 0x14);
            float maxx = BitConverter.ToSingle(it, 0x20), maxy = BitConverter.ToSingle(it, 0x24), maxz = BitConverter.ToSingle(it, 0x28);
            float dx = maxx - minx, dy = maxy - miny, dz = maxz - minz;
            if (MathF.Max(dx, MathF.Max(dy, dz)) > 20f) continue; // buildings/terrain boxes are not interesting here
            float cx = (minx + maxx) / 2, cy = (miny + maxy) / 2, cz = (minz + maxz) / 2;
            double dist = Math.Sqrt((cx - tx) * (cx - tx) + (cz - tz) * (cz - tz));
            if (dist > radius) continue;
            int type = it[0x0A];
            uint flags = BitConverter.ToUInt32(it, 0x34);
            ulong uid = BitConverter.ToUInt64(it, 0x40);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Console.WriteLine($"ITEM\t{type}\t{TypeName(type)}\t0x{uid:X12}\t{cx.ToString("F2", inv)}\t{cy.ToString("F2", inv)}\t{cz.ToString("F2", inv)}\t{dx.ToString("F2", inv)}\t{dy.ToString("F2", inv)}\t{dz.ToString("F2", inv)}\t0x{flags:X}\t{dist.ToString("F1", inv)}");
            collected++;
        }
    }
    Console.WriteLine($"SUMMARY\ttruck=({tx:F1}, {ty:F1}, {tz:F1})\tradius={radius:F0}\titems={collected}");
}

// --deep: DoViz says WHAT is near a point; this says WHAT EACH THING IS. Two identity channels the game
// carries itself: the MSVC x64 RTTI chain hanging off the vtable pointer (the C++ class name), and every
// ASCII run reachable from the item's own first 0x100 bytes (tokens, model paths, trigger names).
// That is how the required parking spot stops being something we infer from cones and becomes something
// we read.
//
// --deep-tsv is the SAME walk with a machine-readable print format, not a second scan path: same array
// sweep, same quiet skip set, same uid dedupe, same distance filter and radius, same 3000-item cap. Only
// the printing differs, and in that mode stdout holds records and nothing else - a consumer must not have
// to filter narration out of the identity channel. Record shape (one per line, CRLF-terminated like every
// other Console line on Windows; the numeric parses tolerate the trailing \r, but an STR line's LAST field
// is an identity run, not a number, so a reader must trim \r before it uses that text):
//   ITEM\t<type:int>\t<uid:0xHEX>\t<flags:0xHEX>\t<x>\t<y>\t<z>\t<dx>\t<dy>\t<dz>
//   STR\t<uid:0xHEX>\t<ascii-run>
// x/y/z is the k-DOP centre, dx/dy/dz its extents, floats InvariantCulture "0.###". STR lines come from
// the one scan DeepItemStrings() performs - the same walk --deep prints as 串 - so the two modes cannot
// disagree about a uid; a uid may repeat, one line per run, and each run is 0x20-0x7E by construction.
void DoDeep(ulong basePtr, double x, double z, double r, bool tsv)
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    List<(long off, ulong value, ulong size)> arrays = new();
    for (long off = 0; off < 0x6000; off += 8)
    {
        byte[] head;
        try { head = ReadBytes(h, (long)basePtr + off, 0x28); } catch { continue; }
        ulong value = BitConverter.ToUInt64(head, 0x08);
        ulong size = BitConverter.ToUInt64(head, 0x10);
        ulong cap = BitConverter.ToUInt64(head, 0x18);
        if (value == 0 || size == 0 || size > 200_000 || cap < size) continue;
        arrays.Add((off, value, size));
    }
    if (!tsv) Console.WriteLine($"base_ctrl 里像 array_dyn_t 的成员 {arrays.Count} 个");

    var seen = new HashSet<ulong>();
    int shown = 0, skipped = 0;
    // Terrain/Sound/VisArea/MapArea are ground or volume bookkeeping, never parking obstacles;
    // their bulk drowned the 400-item print cap when the course filled the radius.
    var quiet = new HashSet<int> { 0x01, 0x15, 0x30, 0x2A };
    foreach (var (aOff, value, size) in arrays)
    {
        byte[] ptrs;
        try { ptrs = ReadBytes(h, (long)value, checked((int)(size * 8))); } catch { continue; }
        for (int i = 0; i < (int)size && shown < 3000; i++)
        {
            ulong elem = BitConverter.ToUInt64(ptrs, i * 8);
            if (elem == 0 || !seen.Add(elem)) continue;
            byte[] it;
            try { it = ReadBytes(h, (long)elem, 0x100); } catch { continue; }

            float minx = BitConverter.ToSingle(it, 0x0C), miny = BitConverter.ToSingle(it, 0x10), minz = BitConverter.ToSingle(it, 0x14);
            float maxx = BitConverter.ToSingle(it, 0x20), maxy = BitConverter.ToSingle(it, 0x24), maxz = BitConverter.ToSingle(it, 0x28);
            bool kdopSane = float.IsFinite(minx) && float.IsFinite(maxx) && maxx >= minx && maxz >= minz
                            && Math.Abs(minx) < 100_000 && Math.Abs(minz) < 100_000;
            double cx = kdopSane ? (minx + maxx) / 2 : 0, cz = kdopSane ? (minz + maxz) / 2 : 0;
            double dist = Math.Sqrt((cx - x) * (cx - x) + (cz - z) * (cz - z));
            if (!kdopSane || dist > r) continue;

            int type = it[0x0A];
            if (quiet.Contains(type)) { skipped++; continue; }
            ulong uid = BitConverter.ToUInt64(it, 0x40);
            uint flags = BitConverter.ToUInt32(it, 0x34);
            if (tsv)
            {
                Console.WriteLine($"ITEM\t{type}\t0x{uid:X12}\t0x{flags:X}\t" +
                                  $"{Num(cx, inv)}\t{Num((miny + maxy) / 2, inv)}\t{Num(cz, inv)}\t" +
                                  $"{Num(maxx - minx, inv)}\t{Num(maxy - miny, inv)}\t{Num(maxz - minz, inv)}");
                foreach (string s in DeepItemStrings(it, (long)elem).records)
                    Console.WriteLine($"STR\t0x{uid:X12}\t{s}");
            }
            else
            {
                ulong vptr = BitConverter.ToUInt64(it, 0);
                bool vptrInModule = vptr >= (ulong)imageBase && vptr < (ulong)(imageBase + imageSize);
                Console.WriteLine($"\nITEM 0x{elem:X}  类型 0x{type:X2} {TypeName(type)}  uid=0x{uid:X12}  flags=0x{flags:X}");
                Console.WriteLine($"     @({cx:F2}, {((miny + maxy) / 2):F2}, {cz:F2})  尺寸 {(maxx - minx):F2}x{(maxy - miny):F2}x{(maxz - minz):F2}  离目标 {dist:F2} m");
                Console.WriteLine($"     vptr=0x{vptr:X}{(vptrInModule ? "（游戏模块内）" : "（模块外）")}  类名={ClassName(vptr) ?? "?"}");

                foreach (string s in DeepItemStrings(it, (long)elem).human.Take(12)) Console.WriteLine($"     串  {s}");
            }
            shown++;
        }
    }
    if (!tsv) Console.WriteLine($"\n共展开 {shown} 个条目（另有 {skipped} 个 Terrain/Sound/VisArea/MapArea 静默跳过）");
}

// Every string one kdop item carries, scanned ONCE for both print modes so the console and the machine
// records can never disagree about an item. Two channels: (1) the element's own first 0x100 bytes hold
// pointers, and walking every 8-byte word and reading 96 bytes at each plausible user-mode pointer is
// where the IDENTITY strings live - the trigger's `state park_trigger_on_stopped`, an `/automat/...`
// path, anything the item only references instead of storing - and (2) the ASCII runs stored inline in
// those 0x100 bytes, which the pointer scan has not already reached. The TSV STR channel used to read
// only channel (2), which made the sidecar strictly weaker than the console it was built to replace.
// Returns the pair of views over one scan: `human` is what --deep prints as 串 (each entry labelled
// @+0xNN for a pointer window, 内联 for an inline run), `records` is the same strings with the
// non-ASCII 内联 marker dropped so a STR field stays 0x20-0x7E, while the @+0xNN provenance is kept
// because it is the only thing that says WHERE the run was found. An inline run that a pointer window
// already produced is folded into that entry, so a uid never emits a string twice *across channels*.
// Duplicates inside the pointer channel are preserved exactly as the console prints them, including
// the quirk that the original Contains(run) test compares a bare run against already-labelled entries
// and so never fires - measured here: 14 (uid, run) pairs among the 819 STR rows appear twice, and
// every one of them is a 串 line --deep prints twice for that same item, so the records add nothing
// the console does not already say.
(List<string> human, List<string> records) DeepItemStrings(byte[] it, long elem)
{
    const string inlineMarker = "内联 "; // presentation-only; never part of a record field
    var human = new List<string>();
    var records = new List<string>();
    for (int w = 0; w + 8 <= 0x100; w += 8)
    {
        ulong p = BitConverter.ToUInt64(it, w);
        if (p < 0x10000 || p > 0x7FFFFFFFFFFF) continue;
        foreach (string s in ReadAsciiRuns(h, (long)p, 96, 4))
        {
            if (s.Length < 4 || human.Contains(s)) continue;
            human.Add($"@+0x{w:X2} {s}");
            records.Add($"@+0x{w:X2} {s}");
        }
        if (human.Count >= 8) break;
    }
    foreach (string s in ReadAsciiRuns(h, elem, 0x100, 5))
        if (!human.Any(f => f.EndsWith(s)))
        {
            human.Add(inlineMarker + s);
            records.Add(s);
        }
    return (human, records);
}

// One formatter for every float field of the TSV record: InvariantCulture "0.###", which for a finite
// value is digits, '.' and '-' only. Non-finite values get the literal token NaN instead of the format's
// own rendering, and that is deliberate rather than measured-necessary: kdopSane above only vouches for
// x and z, so a non-item element can still reach the printer with a garbage y, and this keeps the record
// alphabet independent of the runtime's NumberFormat infinity symbol. Measured on this SDK's runtime
// (dotnet fsi, same net10.0 framework, no InvariantGlobalization flag): (+inf).ToString("0.###", inv) is
// "Infinity" and NaN is "NaN", both ASCII and both accepted back by double.Parse, so no live sample ever
// needed this clamp - an unbounded-radius scan reached the 3000-item cap (3000 ITEM rows, 392 STR rows)
// and produced zero non-finite fields. With it, a consumer has exactly one non-numeric token to expect.
static string Num(double v, System.Globalization.CultureInfo inv) =>
    double.IsFinite(v) ? v.ToString("0.###", inv) : "NaN";

// MSVC x64 RTTI: [vtable-8] holds either an absolute CompleteObjectLocator pointer or a 32-bit RVA of
// one (the /guard:rf form); the COL's +12 is a 32-bit RVA of the TypeDescriptor, whose +16 is the
// mangled name. Games differ on which of the four combinations they use, so try them all and keep the
// answer that actually looks like a class name.
string? ClassName(ulong vptr)
{
    if (vptr < 0x10000) return null;
    foreach (bool colIsRva in new[] { true, false })
    {
        foreach (int colSlot in new[] { -8, -16 })
        {
            try
            {
                ulong raw = ReadU64(h, (long)vptr + colSlot);
                long col = colIsRva ? imageBase + (long)(raw & 0xFFFFFFFF) : (long)raw;
                if (col < imageBase || col > imageBase + imageSize) continue;
                byte[] colBuf = ReadBytes(h, col, 0x20);
                foreach (int tdField in new[] { 12, 8, 16 })
                {
                    ulong tdRaw = tdField == 8 || tdField == 16 ? BitConverter.ToUInt64(colBuf, tdField) : BitConverter.ToUInt32(colBuf, tdField);
                    long td = tdRaw > 0xFFFFFFFF ? (long)tdRaw : imageBase + (long)tdRaw;
                    if (td < imageBase || td > imageBase + imageSize) continue;
                    foreach (int nameAt in new[] { 16 })
                    {
                        foreach (bool nameIsPtr in new[] { true, false })
                        {
                            try
                            {
                                long addr = imageBase;
                                if (nameIsPtr)
                                {
                                    ulong np = ReadU64(h, td + nameAt);
                                    addr = np > 0xFFFFFFFF ? (long)np : imageBase + (long)np;
                                }
                                else addr = td + nameAt;
                                byte[] nb = ReadBytes(h, addr, 64);
                                int len = Array.IndexOf(nb, (byte)0);
                                if (len < 5) continue;
                                string name = System.Text.Encoding.ASCII.GetString(nb, 0, len);
                                if (name.StartsWith(".?AV") || name.StartsWith(".?AU"))
                                    return name.TrimStart('.', '?', 'A', 'V', 'U').TrimEnd('@');
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }
    }
    return null;
}

static List<string> ReadAsciiRuns(IntPtr handle, long address, int size, int minLen)
{
    var runs = new List<string>();
    byte[] buf = new byte[size];
    if (!Native.ReadProcessMemory(handle, address, buf, size, out nint got) || got == 0) return runs;
    int i = 0;
    while (i < got)
    {
        if (buf[i] >= 0x20 && buf[i] < 0x7F)
        {
            int j = i;
            while (j < got && buf[j] >= 0x20 && buf[j] < 0x7F) j++;
            if (j - i >= minLen) runs.Add(System.Text.Encoding.ASCII.GetString(buf, i, j - i));
            i = j;
        }
        else i++;
    }
    return runs;
}

// ================================================================================================

bool LooksLikeKdopItem(IntPtr handle, ulong elem)
{
    try
    {
        byte[] it = ReadBytes(handle, (long)elem, 0x38);
        ulong vptr = BitConverter.ToUInt64(it, 0);
        if (vptr < (ulong)imageBase || vptr >= (ulong)(imageBase + imageSize)) return false;
        float minx = BitConverter.ToSingle(it, 0x0C), miny = BitConverter.ToSingle(it, 0x10), minz = BitConverter.ToSingle(it, 0x14);
        float maxx = BitConverter.ToSingle(it, 0x20), maxy = BitConverter.ToSingle(it, 0x24), maxz = BitConverter.ToSingle(it, 0x28);
        bool sane(float v) => float.IsFinite(v);
        return sane(minx) && sane(miny) && sane(minz) && sane(maxx) && sane(maxy) && sane(maxz)
            && Math.Abs(minx) < 100_000 && Math.Abs(minz) < 100_000 && miny > -50 && miny < 500
            && maxx >= minx && maxy >= miny && maxz >= minz;
    }
    catch { return false; }
}

void ScanBaseCtrlForKdopArrays(ulong basePtr)
{
    Console.WriteLine();
    Console.WriteLine("=== base_ctrl member sweep for array_dyn_t<kdop_item_t*> ===");
    for (long off = 0x00; off <= 0x6000; off += 8)
    {
        byte[] head;
        try { head = ReadBytes(h, (long)basePtr + off, 0x28); }
        catch { break; }
        ulong value = BitConverter.ToUInt64(head, 0x08);
        ulong size = BitConverter.ToUInt64(head, 0x10);
        ulong cap = BitConverter.ToUInt64(head, 0x18);
        if (value == 0 || size == 0 || size > 200_000 || cap < size || cap > 2_000_000) continue;
        int sample = (int)Math.Min(size, 48);
        byte[] elems;
        try { elems = ReadBytes(h, (long)value, sample * 8); }
        catch { continue; }
        int pass = Enumerable.Range(0, sample).Count(i => LooksLikeKdopItem(h, BitConverter.ToUInt64(elems, i * 8)));
        if (sample > 0 && pass >= sample * 4 / 5)
        {
            Console.WriteLine($"  base_ctrl+0x{off:X}: size={size} cap={cap}  {pass}/{sample} sampled elements look like kdop items");
            DumpKdopArray($"base_ctrl+0x{off:X}", (long)basePtr + off);
        }
    }
}

void DumpKdopArray(string label, long arrayAddr)
{
    Console.WriteLine($"=== {label} ===");
    byte[] head = ReadBytes(h, arrayAddr, 0x28);
    ulong value = BitConverter.ToUInt64(head, 0x08);
    ulong size = BitConverter.ToUInt64(head, 0x10);
    Console.WriteLine($"  value=0x{value:X} size={size}");
    if (value == 0 || size == 0 || size > 200_000) { Console.WriteLine("  unusable"); return; }

    var histogram = new SortedDictionary<int, int>();
    var small = new List<(int type, ulong uid, float cx, float cy, float cz, float dx, float dy, float dz, uint flags)>();
    int inBox = 0;
    var boxTypes = new SortedDictionary<int, int>();
    byte[] ptr8 = new byte[8], it = new byte[0x48];
    for (ulong i = 0; i < size; i++)
    {
        try
        {
            Native.ReadProcessMemory(h, (long)value + (long)i * 8, ptr8, 8, out _);
            ulong elem = BitConverter.ToUInt64(ptr8, 0);
            if (!Native.ReadProcessMemory(h, (long)elem, it, 0x48, out _)) continue;
        }
        catch { continue; }
        int type = it[0x0A];
        histogram[type] = histogram.GetValueOrDefault(type) + 1;
        float minx = BitConverter.ToSingle(it, 0x0C), miny = BitConverter.ToSingle(it, 0x10), minz = BitConverter.ToSingle(it, 0x14);
        float maxx = BitConverter.ToSingle(it, 0x20), maxy = BitConverter.ToSingle(it, 0x24), maxz = BitConverter.ToSingle(it, 0x28);
        float dx = maxx - minx, dy = maxy - miny, dz = maxz - minz;
        float cx = (minx + maxx) / 2, cy = (miny + maxy) / 2, cz = (minz + maxz) / 2;

        if (nearX is double nx && nearZ is double nz && Math.Abs(cx - nx) <= nearR && Math.Abs(cz - nz) <= nearR)
        {
            inBox++;
            boxTypes[type] = boxTypes.GetValueOrDefault(type) + 1;
        }
        if (dx is > 0 and < 3.5f && dz is > 0 and < 3.5f && dy is > 0 and < 4.0f)
        {
            uint flags = BitConverter.ToUInt32(it, 0x34);
            small.Add((type, BitConverter.ToUInt64(it, 0x40), cx, cy, cz, dx, dy, dz, flags));
        }
    }

    Console.WriteLine($"  item_type histogram over {size} items:");
    foreach (var (type, count) in histogram.OrderByDescending(kv => kv.Value).Take(14))
        Console.WriteLine($"    0x{type:X2} {TypeName(type),-15} {count,7}");
    if (histogram.Count > 14) Console.WriteLine($"    ... {histogram.Count - 14} more types");

    if (nearX is double nx2 && nearZ is double nz2)
    {
        Console.WriteLine($"  items within {nearR} m box of ({nx2}, {nz2}): {inBox}");
        foreach (var (type, count) in boxTypes.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"    0x{type:X2} {TypeName(type),-15} {count,7}");
        var near = small
            .Select(s => (s, dist: Math.Sqrt((s.cx - nx2) * (s.cx - nx2) + (s.cz - nz2) * (s.cz - nz2))))
            .Where(t => t.dist <= nearR!.Value)
            .OrderBy(t => t.dist).Take(60).ToList();
        Console.WriteLine($"  small items (extents < 4 m) within {nearR} m: {near.Count}");
        foreach (var (s, dist) in near)
            Console.WriteLine($"    {TypeName(s.type),-13} uid=0x{s.uid:X12} @ ({s.cx,8:F1}, {s.cy,6:F1}, {s.cz,8:F1})  size {s.dx:F1}x{s.dy:F1}x{s.dz:F1}  flags=0x{s.flags:X}  dist={dist,6:F1} m");
    }
    Console.WriteLine();
}

// ================================================================================================

void ScanStrings(IntPtr handle, List<string> tokens)
{
    Console.WriteLine();
    Console.WriteLine("=== committed-memory string scan ===");
    var needles = tokens.Select(t => (token: t, bytes: Encoding.ASCII.GetBytes(t))).ToList();
    var counts = new Dictionary<string, int>();
    var samples = new Dictionary<string, List<long>>();

    long addr = 0x10000;
    while (Native.VirtualQueryEx(handle, addr, out var mbi, (uint)Marshal.SizeOf<Native.MBI>()))
    {
        long region = (long)mbi.BaseAddress;
        long len = (long)mbi.RegionSize;
        addr = region + len;
        if (mbi.State != 0x1000 /* MEM_COMMIT */) continue;
        int protect = (int)(mbi.Protect & 0xFF);
        if (protect is not (0x02 or 0x04) /* PAGE_READONLY | PAGE_READWRITE */) continue;

        const int chunk = 32 << 20;
        for (long off = 0; off < len; off += chunk)
        {
            int take = (int)Math.Min(chunk + 128, len - off);
            byte[] buf;
            try { buf = ReadBytes(handle, region + off, take); }
            catch { break; }
            foreach (var (token, bytes) in needles)
            {
                int idx = 0;
                while ((idx = IndexOf(buf, bytes, idx)) >= 0)
                {
                    counts[token] = counts.GetValueOrDefault(token) + 1;
                    if (!samples.TryGetValue(token, out var list)) samples[token] = list = new();
                    if (list.Count < 5) list.Add(region + off + idx);
                    idx += 1;
                }
            }
        }
    }

    foreach (var token in tokens)
        Console.WriteLine($"  \"{token}\": {counts.GetValueOrDefault(token)} hits {(samples.TryGetValue(token, out var s) && s.Count > 0 ? "- first at 0x" + string.Join(", 0x", s) : "- not present in committed memory")}");
}

// --strctx: needle search + surrounding context. ScanStrings answers HOW OFTEN; this answers
// WHAT ELSE IS THERE. For trigger ordering the interesting part is never the token itself but
// its neighbours - sibling states, script lines, item uids - which all sit within a few dozen
// bytes of the hit inside the same script blob.
void ScanStrContext(IntPtr handle, List<string> tokens)
{
    Console.WriteLine("=== committed-memory string scan with context ===");
    var needles = tokens.Select(t => (token: t, bytes: Encoding.ASCII.GetBytes(t))).ToList();
    var counts = new Dictionary<string, int>();

    const int ctxBefore = 96, ctxAfter = 160, maxHitsPerToken = 12;
    long addr = 0x10000;
    while (Native.VirtualQueryEx(handle, addr, out var mbi, (uint)Marshal.SizeOf<Native.MBI>()))
    {
        long region = (long)mbi.BaseAddress;
        long len = (long)mbi.RegionSize;
        addr = region + len;
        if (mbi.State != 0x1000 /* MEM_COMMIT */) continue;
        int protect = (int)(mbi.Protect & 0xFF);
        if (protect is not (0x02 or 0x04) /* PAGE_READONLY | PAGE_READWRITE */) continue;

        const int chunk = 32 << 20;
        for (long off = 0; off < len; off += chunk)
        {
            int take = (int)Math.Min(chunk + 128, len - off);
            byte[] buf;
            try { buf = ReadBytes(handle, region + off, take); }
            catch { break; }
            foreach (var (token, bytes) in needles)
            {
                int idx = 0;
                while ((idx = IndexOf(buf, bytes, idx)) >= 0)
                {
                    counts[token] = counts.GetValueOrDefault(token) + 1;
                    if (counts[token] <= maxHitsPerToken)
                    {
                        long hit = region + off + idx;
                        byte[] ctx;
                        try { ctx = ReadBytes(handle, hit - ctxBefore, ctxBefore + bytes.Length + ctxAfter); }
                        catch { ctx = Array.Empty<byte>(); }
                        Console.WriteLine($"  [{token}] hit #{counts[token]} @ 0x{hit:X}");
                        if (ctx.Length > 0)
                        {
                            var runs = ReadAsciiRunsFromBuffer(ctx, 4);
                            foreach (var run in runs)
                                Console.WriteLine($"      {run}");
                        }
                    }
                    idx += 1;
                }
            }
        }
    }

    foreach (var token in tokens)
        Console.WriteLine($"  \"{token}\": {counts.GetValueOrDefault(token)} hits (context printed for first {maxHitsPerToken})");
}

static List<string> ReadAsciiRunsFromBuffer(byte[] buf, int minLen)
{
    var runs = new List<string>();
    int i = 0;
    while (i < buf.Length)
    {
        if (buf[i] >= 0x20 && buf[i] < 0x7F)
        {
            int j = i;
            while (j < buf.Length && buf[j] >= 0x20 && buf[j] < 0x7F) j++;
            if (j - i >= minLen) runs.Add(System.Text.Encoding.ASCII.GetString(buf, i, j - i));
            i = j;
        }
        else i++;
    }
    return runs;
}

static string TypeName(int type) => type switch
{
    0x01 => "Terrain", 0x02 => "Buildings", 0x03 => "Road", 0x04 => "Prefab", 0x05 => "Model",
    0x06 => "Company", 0x07 => "Service", 0x08 => "CutPlane", 0x09 => "Mover", 0x0B => "EnvArea",
    0x0C => "CityArea", 0x0D => "Hinge", 0x0F => "AnimModel", 0x12 => "MapOverlay", 0x13 => "Ferry",
    0x15 => "Sound", 0x16 => "Garage", 0x17 => "CameraPoint", 0x1C => "Walker", 0x22 => "Trigger",
    0x23 => "FuelPump", 0x24 => "Sign", 0x25 => "BusStop", 0x26 => "TrafficArea", 0x27 => "Bezier",
    0x28 => "Compound", 0x29 => "Trajectory", 0x2A => "MapArea", 0x2B => "FarModel", 0x2C => "Curve",
    0x2D => "CameraPath", 0x2E => "Cutscene", 0x2F => "Hookup", 0x30 => "VisArea", 0x31 => "Gate",
    _ => "?",
};

static long RipRel(long insnAddr, int dispOffset, byte[] image, long imageBase) =>
    insnAddr + dispOffset + I32(image, insnAddr + dispOffset - imageBase) + 4;

static List<long> FindAllPattern(byte[] data, long baseAddr, string pattern)
{
    string[] parts = pattern.Split(' ');
    byte?[] pat = parts.Select(p => p.Contains('?') ? (byte?)null : Convert.ToByte(p, 16)).ToArray();
    byte first = pat[0]!.Value;
    var hits = new List<long>();
    int max = data.Length - pat.Length;
    int i = 0;
    while (i <= max)
    {
        if (data[i] != first) { int next = Array.IndexOf(data, first, i + 1); if (next < 0 || next > max) break; i = next; }
        bool ok = true;
        for (int j = 1; j < pat.Length; j++)
            if (pat[j].HasValue && data[i + j] != pat[j].Value) { ok = false; break; }
        if (ok) hits.Add(baseAddr + i);
        i++;
    }
    return hits;
}

// Walks at most maxLen bytes of "48 8b modrm [sib] [disp]" instructions from start and returns
// the disp32 of the last one that has one (mod=10), i.e. the game_actor member offset in the
// pattern's final mov. Returns -1 if the walk leaves instructions.
static int DecodeLastDisp32(byte[] image, int start, int maxLen)
{
    int last = -1, pos = 0;
    while (pos + 3 <= maxLen)
    {
        if (image[start + pos] != 0x48 || image[start + pos + 1] != 0x8b) break;
        byte modrm = image[start + pos + 2];
        int len = (modrm & 0xC7) == 0x05 ? 7
                : (modrm & 0xC0) == 0x80 ? 7 + ((modrm & 0x07) == 0x04 ? 1 : 0)
                : (modrm & 0xC0) == 0x40 ? 4 + ((modrm & 0x07) == 0x04 ? 1 : 0)
                : 3;
        if ((modrm & 0xC0) == 0x80 && pos + len <= maxLen + 4) last = BitConverter.ToInt32(image, start + pos + 3 + ((modrm & 0x07) == 0x04 ? 1 : 0));
        pos += len;
        if (pos == maxLen) break;
    }
    return last;
}

static int I32(byte[] image, long fileOff) => BitConverter.ToInt32(image, checked((int)fileOff));

static long FindPattern(byte[] data, long baseAddr, string pattern)
{
    string[] parts = pattern.Split(' ');
    byte?[] pat = parts.Select(p => p.Contains('?') ? (byte?)null : Convert.ToByte(p, 16)).ToArray();
    byte first = pat[0]!.Value;
    int max = data.Length - pat.Length;
    int i = 0;
    while (i <= max)
    {
        if (data[i] != first) { int next = Array.IndexOf(data, first, i + 1); if (next < 0 || next > max) return 0; i = next; }
        bool ok = true;
        for (int j = 1; j < pat.Length; j++)
            if (pat[j].HasValue && data[i + j] != pat[j].Value) { ok = false; break; }
        if (ok) return baseAddr + i;
        i++;
    }
    return 0;
}

static int IndexOf(byte[] hay, byte[] needle, int from)
{
    for (int i = from; i <= hay.Length - needle.Length; i++)
    {
        i = Array.IndexOf(hay, needle[0], i);
        if (i < 0 || i > hay.Length - needle.Length) return -1;
        bool ok = true;
        for (int j = 1; j < needle.Length; j++)
            if (hay[i + j] != needle[j]) { ok = false; break; }
        if (ok) return i;
    }
    return -1;
}

static byte[] ReadBytes(IntPtr h, long address, int size)
{
    byte[] buf = new byte[size];
    if (!Native.ReadProcessMemory(h, address, buf, size, out nint read) || read != size)
        throw new Exception($"ReadProcessMemory 0x{address:X} got {read}/{size} win32={Marshal.GetLastWin32Error()}");
    return buf;
}

ulong ReadU64(IntPtr h, long address)
{
    byte[] b = ReadBytes(h, address, 8);
    return BitConverter.ToUInt64(b, 0);
}

static class Native
{
    [DllImport("kernel32", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool ReadProcessMemory(IntPtr h, long address, byte[] buf, nint size, out nint read);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool VirtualQueryEx(IntPtr h, long address, out MBI buffer, uint length);

    [DllImport("kernel32")]
    internal static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MBI
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}
