using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using AutoParking;
using TruckLib;
using TruckLib.HashFs;
using TruckLib.ScsMap;

// Offline reader for one map sector: what the archives actually contain around a coordinate, by
// class, with the tokens, and how much of it the node index can even reach. It exists because "why is
// that crate not drawn" was being answered by guessing; the answer is a fact about the files and does
// not need the game running.
//
// A sector can come from any archive - base_map.scs or a dlc_*.scs - and the game mounts all of them
// with later ones overriding earlier ones. So this takes a directory or one .scs, stages the .mbd
// plus the single sector being asked about from every archive in mount order, and reads the result
// from disk. Both archive formats SCS uses are handled: hashed .scs and plain zips.

Console.OutputEncoding = System.Text.Encoding.UTF8;

double x = Number(1, -74584.01);
double z = Number(2, 25599.00);
double radius = Number(3, 120.0);

string source = args.Length > 0 ? args[0] : "";
if (source.Length == 0)
{
    Console.WriteLine("用法: dotnet run --project Tools/MapSectorDump -c Release -- <游戏目录或某个 .scs> [x z 半径]");
    Console.WriteLine("默认坐标是你泊车那次日志里的位置，默认半径 120 m（和插件的采样一致）。");
    return 2;
}

double Number(int index, double fallback)
{
    return args.Length > index && double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        ? value : fallback;
}

Vector2 center = new((float)x, (float)z);
SectorCoordinate target = Map.GetSectorOfCoordinate(new Vector3((float)x, 0f, (float)z));
string targetName = Sector.SectorFileNameFromSectorCoords(target);
string stage = Path.Combine(Path.GetTempPath(), "autoparking-mapdump");

Console.WriteLine($"# 目标坐标 ({x:0}, {z:0}) → 扇区 {targetName}（每扇区 {Map.SectorSize} m）");

List<string> archives = ResolveArchives(source);
Console.WriteLine($"# 按挂载顺序读 {archives.Count} 个归档：{string.Join(", ", archives.Select(Path.GetFileName).Take(10))}"
                  + (archives.Count > 10 ? " …" : ""));

string? mbdLocal = null;
List<string> sectorDirs = new();
foreach (string archive in archives)
{
    int staged = 0;
    try
    {
        using ArchiveReader reader = ArchiveReader.Open(archive);

        List<string> descriptors = reader.MapDescriptorFiles();
        foreach (string mbd in descriptors)
        {
            string directory = ArchiveReader.SectorDirectoryOf(mbd);
            if (!sectorDirs.Contains(directory))
                sectorDirs.Add(directory);
        }

        string? chosen = descriptors.FirstOrDefault(mbd => reader.HasSectorsOf(mbd, targetName)) ?? descriptors.FirstOrDefault();
        if (mbdLocal == null && chosen != null)
        {
            Write(reader.OpenStream(chosen), stage, chosen);
            mbdLocal = LocalCopy(stage, chosen);
            staged++;
        }

        // A DLC adds sectors to a map without carrying an .mbd of its own, so the directories to look
        // in accumulate across archives: base_map.scs says the map keeps its sectors in /map/usa/, and
        // every later archive is asked for the target sector inside that same directory.
        foreach (string path in reader.SectorFiles(targetName, sectorDirs))
        {
            Write(reader.OpenStream(path), stage, path);
            staged++;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"#   {Path.GetFileName(archive)}：读不了（{ex.GetType().Name}），跳过");
        continue;
    }

    if (staged > 0)
        Console.WriteLine($"#   {Path.GetFileName(archive)}：贡献 {staged} 个文件");
}

if (mbdLocal != null)
{
    // Map.Open enumerates the sector directory sitting next to the .mbd, and TruckLib's disk file
    // system throws when it is missing - create it so "no archive carries this sector" stays a
    // readable answer instead of a stack trace.
    Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(mbdLocal)!,
                                           Path.GetFileNameWithoutExtension(mbdLocal)));
}

if (mbdLocal == null)
{
    Console.WriteLine("# 这些归档里没有任何 .mbd。");
    return 4;
}

Map map = Map.Open(mbdLocal);
Console.WriteLine($"# 解析完成：节点 {map.Nodes.Count} · 条目 {map.MapItems.Count}");
if (map.MapItems.Count == 0)
{
    Console.WriteLine("# 扇区是空的：这些归档里没有该扇区，或者坐标在别的地图上。");
    return 5;
}

Console.WriteLine();

MapItemProbe probe = new() { Center = center, RadiusM = radius };
List<(double Distance, string Kind, string Token, bool Collision, bool Drawable)> nearby = new();
List<(double Distance, string Token, int Nodes, double Area)> prefabOutlines = new();

// Instrumentation for the one hypothesis that explains "small things show, big warehouses do not":
// items are reached through their NODES, so a segment long enough to have both ends outside the query
// circle is never seen even though it crosses the middle of it. Counted per class, with how far the
// segment reaches beyond the radius.
Dictionary<string, int> missedByRadius = new();
Dictionary<string, double> furthestMiss = new();
HashSet<ulong> walked = new();

foreach (Node node in map.Nodes.Within(x - radius, z - radius, x + radius, z + radius))
{
    probe.NodesScanned++;
    Visit(node.ForwardItem, node, false);
    Visit(node.BackwardItem, node, false);
}

void Visit(IMapObject? item, Node arrivedThrough, bool nested)
{
    if (!MapItemSurface.Describe(item, out string kind, out string? token, out bool collision))
        return;

    Vector2 position = MapItemSurface.GroundPosition(item!, arrivedThrough);

    if (!probe.Add(item!.Uid, kind, token, collision, position, nested))
        return;

    walked.Add(item.Uid);

    bool drawable = !kind.Equals("Road", StringComparison.Ordinal)
                    && !kind.Equals("Terrain", StringComparison.Ordinal)
                    && !kind.Equals("Compound", StringComparison.Ordinal)
                    && MapItemSurface.TryShape(item, out _, out _);

    nearby.Add((Geometry.Distance(center, position), kind, token ?? "", collision, drawable));

    if (item is Prefab prefab)
    {
        List<Vector2> nodes = new();
        foreach (INode prefabNode in prefab.Nodes)
        {
            if (prefabNode != null)
                nodes.Add(Geometry.ToPlane(prefabNode.Position));
        }

        if (nodes.Count > 0)
        {
            Vector2[] ring = Geometry.ConvexHull(nodes.ToArray());
            prefabOutlines.Add((Geometry.Distance(center, position), prefab.Model.ToString(),
                                nodes.Count, Geometry.PolygonArea(ring)));
        }
    }

    // Same rule the plugin uses: a compound keeps its children out of the map's own dictionaries.
    if (item is Compound { MapItems: not null } compound)
    {
        foreach (MapItem child in compound.MapItems.Values)
        {
            if (child is not Compound)
                Visit(child, arrivedThrough, true);
        }
    }
}

HashSet<ulong> longSeen = new();
foreach (INode node in map.Nodes.Values)
{
    if (node.ForwardItem is not PolylineItem line || line.Node == null || line.ForwardNode == null)
        continue;

    if (!longSeen.Add(line.Uid))
        continue;

    Vector2 a = Geometry.ToPlane(line.Node.Position);
    Vector2 b = Geometry.ToPlane(line.ForwardNode.Position);
    double nearestEnd = Math.Min(Geometry.Distance(center, a), Geometry.Distance(center, b));
    if (nearestEnd <= radius)
        continue;   // the node walk does reach this one

    double alongSegment = Geometry.Distance(center, Geometry.ClosestPointOnSegment(a, b, center));
    if (alongSegment > radius)
        continue;   // genuinely not near us

    string kind = line.ItemType.ToString();
    missedByRadius.TryGetValue(kind, out int seen);
    missedByRadius[kind] = seen + 1;
    furthestMiss[kind] = Math.Max(furthestMiss.GetValueOrDefault(kind), nearestEnd - radius);
}

Console.WriteLine($"## 穿过 {radius:0} m 圆但端点全在圆外的条目（节点遍历取不到，只按端点判半径就会漏）");
if (missedByRadius.Count == 0)
    Console.WriteLine("  没有：这一层不是漏画的原因");
foreach ((string kind, int count) in missedByRadius.OrderByDescending(p => p.Value))
    Console.WriteLine($"  {kind,-16} {count} 条 · 端点最远超出半径 {furthestMiss[kind]:0} m");

Console.WriteLine();
Console.WriteLine("## Buildings 明细（端点距离 / 线段最近距离 / 长度 / 方位，用来和屏幕上看到的墙对表）");
foreach ((Buildings item, Vector2 a, Vector2 c) building in map.MapItems.Values.OfType<Buildings>()
            .Select(b => (b, a: Geometry.ToPlane(b.Node.Position), c: Geometry.ToPlane(b.ForwardNode.Position)))
            .Where(pair => Math.Min(Geometry.Distance(center, pair.a), Geometry.Distance(center, pair.c)) <= radius * 2.0)
            .OrderBy(pair => Geometry.Distance(center, Geometry.ClosestPointOnSegment(pair.a, pair.c, center))))
{
    double segmentDistance = Geometry.Distance(center, Geometry.ClosestPointOnSegment(building.a, building.c, center));
    double length = Geometry.Distance(building.a, building.c);
    double nearestEnd = Math.Min(Geometry.Distance(center, building.a), Geometry.Distance(center, building.c));
    Vector2 mid = (building.a + building.c) * 0.5f;
    double bearing = Math.Atan2(mid.X - center.X, center.Y - mid.Y) * 180.0 / Math.PI;

    Console.WriteLine($"  线段 {segmentDistance,6:0.0} m · 端点 {nearestEnd,6:0.0} m · " +
                      $"长 {length,5:0.0} m · 方位 {bearing,6:0}° · {building.item.Name}");
}

// Completeness check for the walk itself: scan every item the archive holds, take the ground
// position from the item's own nodes, and compare against what the node query produced. If the two
// agree, "nothing physical near us" is a fact about the data, not an artefact of how we look it up.
Dictionary<string, int> insideByTable = new();
List<string> missedByWalk = new();
foreach (MapItem item in map.MapItems.Values)
{
    Vector2 position;
    switch (item)
    {
        case SingleNodeItem { Node: not null } single: position = Geometry.ToPlane(single.Node.Position); break;
        case PolylineItem { Node: not null } line: position = Geometry.ToPlane(line.Node.Position); break;
        default: continue;
    }

    if (Geometry.Distance(center, position) > 40.0)
        continue;

    string kind = item.ItemType.ToString();
    insideByTable.TryGetValue(kind, out int seen);
    insideByTable[kind] = seen + 1;
    if (walked.Add(item.Uid))
        missedByWalk.Add($"{item.ItemType} {item.Uid:X}");
}

Console.WriteLine();
Console.WriteLine("## 全表直扫 40 m 内（不经节点遍历，用来验证遍历本身没漏）");
foreach ((string kind, int count) in insideByTable.OrderByDescending(p => p.Value))
    Console.WriteLine($"  {kind,-16} {count}");

Console.WriteLine($"  节点遍历没走到的（仅统计单节点/折线类）：{missedByWalk.Count}");

Console.WriteLine();
Console.WriteLine($"## 半径 {radius:0} m 内（插件能看见的就是这些）");Console.WriteLine(probe.Summary);
foreach (string line in probe.Lines(12))
    Console.WriteLine("  " + line);

Console.WriteLine();
Console.WriteLine($"## {radius:0} m 内最近的 40 个条目");
foreach ((double distance, string kind, string token, bool collision, bool drawable) entry in nearby
            .OrderBy(e => e.Distance).Take(40))
{
    Console.WriteLine($"  {entry.distance,7:0.0} m  {entry.kind,-16} 可碰={(entry.collision ? "1" : "0")} " +
                      $"可画={(entry.drawable ? "1" : "0")}  {entry.token}");
}

Console.WriteLine();
Console.WriteLine($"## 附近 prefab 的凸包轮廓（地图上沙色那些框就是它）");
foreach ((double distance, string token, int nodes, double area) outline in prefabOutlines
            .OrderBy(o => o.Distance))
{
    Console.WriteLine($"  {outline.distance,7:0.0} m  {outline.nodes} 个控制点 · 凸包 {outline.area:0} m²  {outline.token}");
}

Console.WriteLine();
Console.WriteLine($"## 整个 {targetName} 扇区按类型（离线版全图清点）");
foreach (IGrouping<string, MapItem> group in map.MapItems.Values
            .GroupBy(item => item.ItemType.ToString()).OrderByDescending(g => g.Count()))
{
    Console.WriteLine($"  {group.Key,-18} {group.Count()}");
}

// The number that separates "not in the data" from "in the data but unreachable from the node index",
// which is the difference between a missing class and a missing walk.
HashSet<ulong> reachable = new();
foreach (INode node in map.Nodes.Values)
{
    if (node.ForwardItem is MapItem forward)
        reachable.Add(forward.Uid);
    if (node.BackwardItem is MapItem backward)
        reachable.Add(backward.Uid);
}

int unreachable = 0;
foreach (ulong uid in map.MapItems.Keys)
{
    if (!reachable.Contains(uid))
        unreachable++;
}

Console.WriteLine();
Console.WriteLine($"## 节点索引可达性：条目 {map.MapItems.Count} · 从节点可达 {reachable.Count} · 走不到的 {unreachable}");
return 0;

static List<string> ResolveArchives(string source)
{
    if (!Directory.Exists(source))
        return new List<string> { source };

    // Mount order matters: the game reads base first and lets DLC override it, and the sector that is
    // missing from base_map.scs is expected to be inside one of the dlc_*.scs files.
    List<string> files = Directory.GetFiles(source, "*.scs").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
    string modDirectory = Path.Combine(source, "mod");
    if (Directory.Exists(modDirectory))
        files.AddRange(Directory.GetFiles(modDirectory, "*.scs").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase));

    return files;
}

static string LocalCopy(string stage, string archivePath)
{
    string relative = archivePath.Replace('\\', '/').TrimStart('/');
    return Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar));
}

static void Write(Stream source, string stage, string archivePath)
{
    string local = LocalCopy(stage, archivePath);
    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
    using FileStream target = File.Create(local);
    source.CopyTo(target);
}

/// <summary>
///  One .scs, either format, seen only as "which map files does it hold, and give me a stream".
/// </summary>
abstract class ArchiveReader : IDisposable
{
    public static ArchiveReader Open(string path)
    {
        bool zip;
        using (FileStream probe = File.OpenRead(path))
            zip = probe.ReadByte() == 'P' && probe.ReadByte() == 'K';

        return zip ? new ZipArchiveReader(path) : (ArchiveReader)new HashFsArchiveReader(path);
    }

    public abstract List<string> MapDescriptorFiles();
    public abstract bool HasSectorsOf(string mbdPath, string sectorName);

    /// <summary>
    ///  Files of one sector that this archive carries, looked for in every map directory seen so far
    ///  across all archives - a DLC adds sectors without shipping an .mbd of its own.
    /// </summary>
    public abstract List<string> SectorFiles(string sectorName, List<string> sectorDirs);
    public abstract Stream OpenStream(string archivePath);

    public abstract void Dispose();

    /// <summary>
    ///  Where a map keeps its sectors, by the convention SCS uses and TruckLib reads: a directory with
    ///  the same name as the .mbd, sitting next to it.
    /// </summary>
    public static string SectorDirectoryOf(string mbdPath)
    {
        string normalized = mbdPath.Replace('\\', '/');
        int cut = normalized.LastIndexOf('/');
        string parent = cut < 0 ? "" : normalized[..(cut + 1)];
        return $"{parent}{Path.GetFileNameWithoutExtension(normalized)}/";
    }
}

sealed class ZipArchiveReader : ArchiveReader
{
    private readonly ZipArchive archive;

    public ZipArchiveReader(string path) => archive = ZipFile.OpenRead(path);

    private List<string> Paths => archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();

    public override List<string> MapDescriptorFiles()
        => Paths.Where(p => p.EndsWith(".mbd", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();

    public override bool HasSectorsOf(string mbdPath, string sectorName)
        => SectorFiles(sectorName, new List<string> { SectorDirectoryOf(mbdPath) }).Count > 0;

    public override List<string> SectorFiles(string sectorName, List<string> sectorDirs)
    {
        string prefix = sectorName + ".";
        return Paths.Where(p => !p.EndsWith("/") && Path.GetFileName(p).StartsWith(prefix, StringComparison.Ordinal)
                                && (sectorDirs.Count == 0
                                    || sectorDirs.Any(dir => p.StartsWith(dir, StringComparison.Ordinal))))
                    .ToList();
    }

    public override Stream OpenStream(string archivePath)
        => archive.GetEntry(archivePath.TrimStart('/'))?.Open() ?? Stream.Null;

    public override void Dispose() => archive.Dispose();
}

sealed class HashFsArchiveReader : ArchiveReader
{
    private readonly IFileSystem fs;
    private List<string>? descriptors;

    public HashFsArchiveReader(string path) => fs = HashFsReader.Open(path);

    public override List<string> MapDescriptorFiles()
    {
        if (descriptors != null)
            return descriptors;

        // IFileSystem only lists files, so the .mbd is found by walking the archive's own directory
        // listing a few levels down instead of hardcoding a path that differs between games.
        HashFsReaderBase reader = (HashFsReaderBase)fs;
        List<string> found = new();
        List<string> directories = new() { "/" };

        for (int depth = 0; depth < 4 && directories.Count > 0; depth++)
        {
            List<string> deeper = new();
            foreach (string directory in directories)
            {
                DirectoryListing listing;
                try
                {
                    listing = reader.GetDirectoryListing(directory, false, true);
                }
                catch (Exception)
                {
                    // Not every entry the parent listed is a directory that can be listed - some
                    // archives carry placeholder paths, and an archive with no map in it is normal.
                    continue;
                }

                found.AddRange(listing.Files.Where(f => f.EndsWith(".mbd", StringComparison.OrdinalIgnoreCase)));
                deeper.AddRange(listing.Subdirectories.Select(child => child.EndsWith("/") ? child : child + "/"));
            }

            directories = deeper;
        }

        descriptors = found.Distinct().ToList();
        return descriptors;
    }

    public override bool HasSectorsOf(string mbdPath, string sectorName)
        => fs.FileExists(SectorDirectoryOf(mbdPath) + sectorName + ".base");

    public override List<string> SectorFiles(string sectorName, List<string> sectorDirs)
    {
        List<string> files = new();
        string prefix = sectorName + ".";

        foreach (string directory in sectorDirs)
        {
            IList<string> listing;
            try
            {
                listing = fs.GetFiles(directory);
            }
            catch (Exception)
            {
                continue;   // this archive does not have that map directory at all
            }

            foreach (string file in listing)
            {
                if (Path.GetFileName(file).StartsWith(prefix, StringComparison.Ordinal))
                    files.Add(file);
            }
        }

        return files.Distinct().ToList();
    }

    public override Stream OpenStream(string archivePath) => fs.Open(archivePath);

    public override void Dispose() => (fs as IDisposable)?.Dispose();
}
