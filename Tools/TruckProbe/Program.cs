using System.Text.RegularExpressions;
using TruckLib.HashFs;
using TruckLib.Models;

// Offline reader over the game's own .scs archives. Answers, from files rather than from a guess,
// what a truck's definition points at and what bounding box its model carries. No game required.
//
//   dotnet run --project Tools/TruckProbe -c Release -- <gameRoot> [fileInsideArchive]

string gameRoot = args.Length > 0 ? args[0] : @"D:\SteamLibrary\steamapps\common\American Truck Simulator";
string? truckName = args.Length > 1 && args[1].StartsWith("#") ? args[1][1..] : null;
string? showFile = args.Length > 1 && !args[1].StartsWith("@") && !args[1].StartsWith("#") ? args[1] : null;
string? listArchive = args.Length > 1 && args[1].StartsWith("@") ? args[1][1..] : null;

// Per-truck definitions are not in the four base archives at all: def.scs holds only five common
// files under /def/vehicle/truck, so every truck ships in its own dlc_*.scs. Hence the scan below.
string[] archives = Directory.GetFiles(gameRoot, "*.scs").OrderBy(a => a, StringComparer.Ordinal).ToArray();

// "@<archive.scs>[:<path>]": "@a.scs" dumps that archive's tree, "@a.scs:/def/..." walks that
// subtree instead. This is how the per-truck folders inside a vehicle DLC were found.
if (listArchive != null)
{
    string archiveName = listArchive;
    string startDir = "/";
    int colon = listArchive.IndexOf(':');
    if (colon > 0)
    {
        archiveName = listArchive[..colon];
        startDir = listArchive[(colon + 1)..];
        if (!startDir.StartsWith('/'))
            startDir = "/" + startDir;
    }

    using IHashFsReader fs = HashFsReader.Open(Path.Combine(gameRoot, archiveName));
    Console.WriteLine($"\n=== {archiveName} :: {startDir}   entries={fs.Entries.Count} ===");
    int n = 0;
    foreach (string f in WalkAll(fs, startDir, 4))
    {
        // /automat/<2 hex>/<hash>.<pmd|pmg|mat|tobj> is the hashed asset store and floods everything
        // else out of view; the def tree is what is being looked for.
        if (f.StartsWith("/automat/", StringComparison.OrdinalIgnoreCase))
            continue;

        Console.WriteLine("  " + f);
        if (++n >= 80) break;
    }

    return;
}

Console.WriteLine($"档案 {archives.Length} 个");

// "#<truckName>" runs the whole chain for one truck: def -> default chassis -> model -> bounding box.
if (truckName != null)
{
    ReportTruck(gameRoot, archives, truckName);
    return;
}

foreach (string archivePath in archives)
{
    string archive = Path.GetFileName(archivePath);

    IHashFsReader fs;
    try
    {
        fs = HashFsReader.Open(archivePath);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{archive}] 打不开: {ex.GetType().Name}");
        continue;
    }

    using (fs)
    {
        List<string> truckDefs = WalkAll(fs, "/def/vehicle/truck")
            .Where(f => f.EndsWith(".model.sii", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (truckDefs.Count == 0)
            continue;

        Console.WriteLine($"\n[{archive}] entries={fs.Entries.Count} truck model defs={truckDefs.Count}");
        foreach (string d in truckDefs.Take(6))
            Console.WriteLine("    " + d);
    }
}

/// <summary>Recursive directory walk. A folder with no directory entry ends that branch, but the
/// walk still reaches everything that does have one.</summary>
static IEnumerable<string> WalkAll(IHashFsReader fs, string dir, int maxDepth = 12)
{
    DirectoryListing listing;
    try
    {
        listing = fs.GetDirectoryListing(dir, filesOnly: false, returnAbsolute: true);
    }
    catch (Exception)
    {
        yield break;
    }

    foreach (string f in listing.Files)
        yield return f;

    if (maxDepth <= 0)
        yield break;

    foreach (string sub in listing.Subdirectories)
        foreach (string f in WalkAll(fs, sub, maxDepth - 1))
            yield return f;
}

if (showFile != null)
{
    // A leading slash passed through a shell becomes a host path, so normalize it here.
    string file = showFile.StartsWith('/') ? showFile : "/" + showFile;

    // The file can live in any archive, so try them in order rather than assuming def.scs.
    bool found = false;
    foreach (string archivePath in archives)
    {
        using IHashFsReader fs = HashFsReader.Open(archivePath);
        if (fs.TryGetEntry(file, out _) == EntryType.File)
        {
            Console.WriteLine($"\n=== {Path.GetFileName(archivePath)} :: {file} ===");
            Console.WriteLine(fs.ReadAllText(file));
            found = true;
            break;
        }
    }

    if (!found)
        Console.WriteLine($"\n（没有档案包含 {file}）");
}

/// <summary>
///  One truck, whole chain: find the def archive, read data.sii for the default chassis, read that
///  chassis's model path, then load the model's bounding box out of its archive.
///
///  The truck is not one model - chassis, cabin and hood are separate parts, each with its own
///  bounding box - so what comes out here is the chassis part, not the assembled vehicle. That
///  distinction is the whole reason the plugin cannot just ask for "the truck's size".
/// </summary>
static void ReportTruck(string gameRoot, string[] archives, string name)
{
    string dataPath = $"/def/vehicle/truck/{name}/data.sii";

    IHashFsReader? defFs = null;
    foreach (string archivePath in archives)
    {
        IHashFsReader fs = HashFsReader.Open(archivePath);
        if (fs.TryGetEntry(dataPath, out _) == EntryType.File)
        {
            defFs = fs;
            Console.WriteLine($"def 来自 {Path.GetFileName(archivePath)}");
            break;
        }

        fs.Dispose();
    }

    if (defFs == null)
    {
        Console.WriteLine($"没有档案包含 {dataPath}");
        return;
    }

    using (defFs)
    {
        string dataSii = defFs.ReadAllText(dataPath);
        Match chassis = Regex.Match(dataSii, @"chassis\|([^""\s]+\.sii)");
        if (!chassis.Success)
        {
            Console.WriteLine("data.sii 里没有 chassis fallback 条目");
            return;
        }

        string chassisFile = chassis.Groups[1].Value;
        string chassisPath = $"/def/vehicle/truck/{name}/chassis/{chassisFile}";
        Console.WriteLine($"默认底盘 {chassisFile}");

        string chassisSii = defFs.ReadAllText(chassisPath);
        Match model = Regex.Match(chassisSii, @"model:\s*""([^""]+)""");
        if (!model.Success)
        {
            Console.WriteLine("底盘定义里没有 model 条目");
            return;
        }

        string pmdPath = model.Groups[1].Value;
        Console.WriteLine($"模型 {pmdPath}");

        // Model.Open needs the sibling .pmg, so the model file has to come from whichever archive
        // holds both. Model.Open(path, IFileSystem) is what makes the archive usable directly.
        foreach (string archivePath in archives)
        {
            using IHashFsReader fs = HashFsReader.Open(archivePath);
            if (fs.TryGetEntry(pmdPath, out _) != EntryType.File)
                continue;

            try
            {
                Model part = Model.Open(pmdPath, fs);
                AxisAlignedBox? box = part.BoundingBox;

                Console.WriteLine($"\n模型来自 {Path.GetFileName(archivePath)}");
                if (box == null)
                {
                    Console.WriteLine("  这个模型没有包围盒");
                    return;
                }

                PrintBox("底盘", box);
                Console.WriteLine($"  对角线 {part.BoundingBoxDiagonalSize:0.00} m");

                // Skeleton and locators are what would give an axle position without the game running:
                // if wheel bones exist, wheelbase and track width come out of the model itself.
                DumpSkeleton(part);
                DumpLocators(part);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  载入模型失败: {ex.GetType().Name}: {ex.Message}");
            }

            return;
        }

        Console.WriteLine("没有档案包含该模型文件");
    }
}

/// <summary>Skeleton and parts are where a wheel position would come from without the game running.
/// The collections are not typed against a known element in the decompiled metadata, so this walks
/// them as IEnumerable and prints whatever ToString yields rather than guessing an element type.</summary>
static void DumpSkeleton(Model part)
{
    object? skeleton = part.Skeleton;
    Console.WriteLine($"  Skeleton = {(skeleton == null ? "null" : $"{skeleton.GetType().Name} ({CountOf(skeleton)} 项)")}");

    if (skeleton is not System.Collections.IEnumerable sk)
        return;

    int shown = 0;
    foreach (object? item in sk)
    {
        if (item == null) continue;
        string s = item.ToString() ?? "";
        Console.WriteLine("    骨骼 " + (s.Length > 160 ? s[..160] + "…" : s));
        if (++shown >= 12) { Console.WriteLine("    …"); break; }
    }
}

static void DumpLocators(Model part)
{
    object? parts = part.Parts;
    Console.WriteLine($"  Parts = {(parts == null ? "null" : $"{parts.GetType().Name} ({CountOf(parts)} 项)")}");

    if (parts is not System.Collections.IEnumerable pt)
        return;

    int shown = 0;
    foreach (object? item in pt)
    {
        if (item == null) continue;
        string s = item.ToString() ?? "";
        Console.WriteLine("    部件 " + (s.Length > 160 ? s[..160] + "…" : s));
        if (++shown >= 6) { Console.WriteLine("    …"); break; }
    }
}

static int CountOf(object value)
{
    int n = 0;
    if (value is System.Collections.IEnumerable e)
        foreach (object? _ in e) n++;
    return n;
}

static void PrintBox(string label, AxisAlignedBox box)
{
    Console.WriteLine($"  {label} Start = ({box.Start.X:0.00}, {box.Start.Y:0.00}, {box.Start.Z:0.00})");
    Console.WriteLine($"  {label} End   = ({box.End.X:0.00}, {box.End.Y:0.00}, {box.End.Z:0.00})");
    Console.WriteLine($"  {label} 各轴跨度 = X {Math.Abs(box.End.X - box.Start.X):0.00}  " +
                      $"Y {Math.Abs(box.End.Y - box.Start.Y):0.00}  Z {Math.Abs(box.End.Z - box.Start.Z):0.00}  (m)");
}

