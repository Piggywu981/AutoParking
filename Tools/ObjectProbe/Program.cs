using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using TruckLib;
using TruckLib.HashFs;
using TruckLib.Models;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
using TruckLib.Sii;

namespace ObjectProbe;

// Offline answer to "can this object be extracted at all", read from the archives rather than from the
// running game. The plugin kept hitting the same wall in three different places (design doc 28, 30,
// 32): props standing right in front of the truck are not in the map item list, and whether that is a
// fidelity setting, a missing walk, or a limit of the data format changes what the fix can be.
//
//   def   - is the object even a placeable unit in /def/world/*.sii, and what does its definition say?
//   ppd   - does a prefab's .ppd carry a placed-model list, and does the prefab's .pmd keep
//           individually named parts (props reachable) or one baked mesh (props not reachable)?
//   token - is a map item's name field a packed token that decodes to the unit name, or an opaque
//           number? Decides whether name-keyed lookups like PmdFileHandler's can work at all.

internal static class Program
{
    const string DefaultGame = @"D:\SteamLibrary\steamapps\common\Euro Truck Simulator 2";
    static readonly string[] BaseArchives =
        { "core.scs", "def.scs", "base_cfg.scs", "base_map.scs", "base_share.scs", "base.scs", "base_vehicle.scs" };

    // Words that mark a part as a placed prop rather than baked terrain.
    static readonly string[] PropWords =
        { "pylcon", "cone", "barrier", "traf", "work", "box", "crate", "pallet", "sign", "lamp", "fence", "fabric" };

    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        if (command == "token") return TokenMode(args.Skip(1).ToList());

        string gameDir = args.Length > 1 ? args[1] : DefaultGame;
        List<string> operands = args.Skip(2).ToList();

        return command switch
        {
            "def" => DefMode(gameDir, operands),
            "ppd" => PpdMode(gameDir, operands),
            "files" => FilesMode(gameDir, operands),
            "units" => UnitsMode(gameDir, operands),
            "scan" => ScanMode(gameDir, operands),
            "curve" => CurveMode(gameDir, operands),
            "kdop" => KdopMode(gameDir, operands),
            "near" => NearMode(gameDir, operands),
            "mapscan" => MapScanMode(gameDir, operands),
            _ => Usage(),
        };
    }

    static int Usage()
    {
        Console.WriteLine("用法: dotnet run --project Tools/ObjectProbe -c Release -- <def|ppd|token> [游戏目录] [参数]");
        Console.WriteLine($"  def   [关键词...]   扫 /def/world/*.sii，列出匹配的模型/prefab 单位（默认 {DefaultGame}）");
        Console.WriteLine("  ppd   <ppd 路径>     解析一个 prefab 描述符 + 同名 .pmd，看有没有可命名的道具");
        Console.WriteLine("  files [目录] [过滤]  列出归档里某个路径下的条目（默认 /def/world/）");
        Console.WriteLine("  units <sii 路径> [过滤] 列出一个 SII 里的单位（过滤同时看名字和 desc 路径）");
        Console.WriteLine("  scan  [token...]   在 base_map + 所有 DLC 的扇区文件里找这些 token（默认锥桶的两个）");
        Console.WriteLine("  near  <x> <z> [半径]  该坐标 N 米内到底有哪些条目（含 DLC 扇区）");
        Console.WriteLine("  token <字符串|数值>  双向验证 token 编码/解码");
        return 2;
    }

    // ── def ────────────────────────────────────────────────────────────────────────────

    static int DefMode(string gameDir, List<string> operands)
    {
        string[] keywords = operands.Count > 0
            ? operands.ToArray()
            : new[] { "pylcon", "cone", "barrier", "traffic", "traf", "work", "construction" };

        Console.WriteLine($"# 归档目录：{gameDir}");
        Console.WriteLine($"# 关键词：{string.Join(", ", keywords)}");

        List<(string Name, IFileSystem Fs)> archives = OpenArchives(gameDir);
        if (archives.Count == 0)
        {
            Console.WriteLine("# 一个 .scs 都打不开，检查游戏目录。");
            return 3;
        }

        int printed = 0;
        foreach ((string name, IFileSystem fs) in archives)
        {
            IList<string> files;
            try { files = fs.GetFiles("/def/world/"); }
            catch (Exception ex)
            {
                Console.WriteLine($"#   {name}：/def/world/ 列不出来（{ex.GetType().Name}: {ex.Message}）");
                continue;
            }

            string[] siis = files.Where(f => f.EndsWith(".sii", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (siis.Length == 0) continue;

            Console.WriteLine($"#   {name}：/def/world/ 有 {siis.Length} 个 .sii");

            foreach (string siiPath in siis)
            {
                SiiFile sii;
                try { sii = SiiFile.Open(siiPath, fs, true); }
                catch (Exception) { continue; }

                foreach (Unit unit in sii.Units)
                {
                    string unitName = unit.Name ?? "";
                    IDictionary attrs = (IDictionary)unit.Attributes;
                    string described = AttrOf(attrs, "model_desc") ?? AttrOf(attrs, "prefab_desc") ?? "";

                    // ETS2 names model units with opaque codes (`model.2a`, `model.1048`); the readable
                    // identity is the file the unit points at. Matching on the name alone is what made
                    // doc 30 conclude that tokens are hashes - they are codes, and the name is in the path.
                    bool match = keywords.Any(k => unitName.Contains(k, StringComparison.OrdinalIgnoreCase)
                                                  || described.Contains(k, StringComparison.OrdinalIgnoreCase));
                    if (!match) continue;

                    if (printed++ == 80)
                    {
                        Console.WriteLine("  …（前 80 条；用 def [目录] <更窄的关键词> 再看）");
                        return 0;
                    }

                    Console.WriteLine($"  {unitName,-24} [{Path.GetFileName(siiPath)}] class={unit.Class} desc={described}");

                    foreach (DictionaryEntry entry in attrs)
                    {
                        string key = (string)(entry.Key ?? "");
                        if (key is "model_desc" or "prefab_desc") continue;
                        string value = Flat(Convert.ToString(entry.Value) ?? "");
                        if (value.Length == 0 || value == "0" || value == "false") continue;
                        Console.WriteLine($"        {key} = {value}");
                    }
                }
            }
        }

        Console.WriteLine($"# 命中 {printed} 个单位");
        return 0;
    }

    static string? AttrOf(IDictionary attrs, string key)
        => attrs.Contains(key) ? Convert.ToString(attrs[key]) : null;

    static string Flat(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();

    // ── files / units ──────────────────────────────────────────────────────────────────

    // Which archive holds what, and under what name. Guessing a path from another game's layout is how
    // the last round of "the field must exist somewhere" searches came up empty.
    static int FilesMode(string gameDir, List<string> operands)
    {
        string directory = operands.Count > 0 ? operands[0] : "/def/world/";
        string filter = operands.Count > 1 ? operands[1] : "";

        foreach ((string name, IFileSystem fs) in OpenArchives(gameDir))
        {
            IList<string> files;
            try { files = fs.GetFiles(directory); }
            catch (Exception ex)
            {
                Console.WriteLine($"#   {name}：{directory} 列不出来（{ex.GetType().Name}）");
                continue;
            }

            string[] hits = files.Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            Console.WriteLine($"#   {name}：{directory} 共 {files.Count} 项，匹配 \"{filter}\" 的 {hits.Length} 项");
            foreach (string hit in hits.Take(60)) Console.WriteLine($"  {hit}");
            if (hits.Length > 60) Console.WriteLine($"  …（共 {hits.Length}）");
        }
        return 0;
    }

    static int UnitsMode(string gameDir, List<string> operands)
    {
        if (operands.Count == 0)
        {
            Console.WriteLine("# 需要 .sii 路径，例如 /def/world/model.sii；第二个参数是可选的名字过滤");
            return 2;
        }

        string siiPath = operands[0];
        string filter = operands.Count > 1 ? operands[1] : "";

        foreach ((string name, IFileSystem fs) in OpenArchives(gameDir))
        {
            SiiFile sii;
            try { sii = SiiFile.Open(siiPath, fs, true); }
            catch { continue; }

            Unit[] hits = sii.Units.Where(u =>
            {
                IDictionary a = (IDictionary)u.Attributes;
                string d = AttrOf(a, "model_desc") ?? AttrOf(a, "prefab_desc") ?? "";
                return (u.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                       || d.Contains(filter, StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            Console.WriteLine($"#   {name}：{siiPath} 共 {sii.Units.Count} 个单位，匹配 \"{filter}\" 的 {hits.Length} 个");
            foreach (Unit unit in hits.Take(120))
            {
                IDictionary attrs = (IDictionary)unit.Attributes;
                string described = AttrOf(attrs, "model_desc") ?? AttrOf(attrs, "prefab_desc") ?? "";
                Console.WriteLine($"  {unit.Name,-30} class={unit.Class} {described}");
            }
            if (hits.Length > 120) Console.WriteLine($"  …（共 {hits.Length}）");
        }
        return 0;
    }

    // ── ppd ────────────────────────────────────────────────────────────────────────────

    static int PpdMode(string gameDir, List<string> operands)
    {
        if (operands.Count == 0)
        {
            Console.WriteLine("# 需要 .ppd 路径，例如 /prefab/road_work/xxx.ppd（先用 def road_work 找 prefab_desc）");
            return 2;
        }

        string ppdPath = operands[0];
        List<(string Name, IFileSystem Fs)> archives = OpenArchives(gameDir);
        (string Name, IFileSystem Fs)? found = null;
        foreach ((string name, IFileSystem fs) in archives)
        {
            try { if (fs.FileExists(ppdPath)) { found = (name, fs); break; } }
            catch { }
        }

        if (found == null)
        {
            Console.WriteLine($"# 这些归档里找不到 {ppdPath}");
            return 4;
        }

        IFileSystem holder = found.Value.Fs;
        Console.WriteLine($"# {ppdPath} 来自 {found.Value.Name}");

        byte[] bytes = holder.ReadAllBytes(ppdPath);
        Console.WriteLine($"# 文件大小 {bytes.Length} 字节");

        Console.WriteLine();
        Console.WriteLine("## .ppd 头部（uint32 序列，前 30 个）");
        List<uint> header = new();
        for (int i = 0; i + 4 <= bytes.Length && header.Count < 30; i += 4) header.Add(BitConverter.ToUInt32(bytes, i));
        Console.WriteLine("  " + string.Join(" ", header.Select((v, idx) => $"[{idx}]{v}")));

        PrefabDescriptor? descriptor = null;
        try { descriptor = PrefabDescriptor.Load(bytes); }
        catch (Exception ex) { Console.WriteLine($"# TruckLib 读不了这个 .ppd：{ex.GetType().Name}: {ex.Message}"); }

        if (descriptor != null)
        {
            Console.WriteLine();
            Console.WriteLine("## TruckLib 解析出来的段");
            Console.WriteLine($"  Version {descriptor.Version} · Nodes {descriptor.Nodes.Count} · NavCurves {descriptor.NavCurves.Count} · "
                              + $"Signs {descriptor.Signs.Count} · Semaphores {descriptor.Semaphores.Count} · SpawnPoints {descriptor.SpawnPoints.Count} · "
                              + $"MapPoints {descriptor.MapPoints.Count} · TriggerPoints {descriptor.TriggerPoints.Count} · "
                              + $"Intersections {descriptor.Intersections.Count} · NavNodes {descriptor.NavNodes.Count}");

            // Anything public and list-shaped other than those sections would be the placed-model list.
            PropertyInfo[] listish = typeof(PrefabDescriptor)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.Contains("model", StringComparison.OrdinalIgnoreCase)
                            || p.Name.Contains("item", StringComparison.OrdinalIgnoreCase)
                            || p.Name.Contains("prop", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Console.WriteLine($"  名字像摆放列表的公开属性：{(listish.Length == 0 ? "无" : string.Join(", ", listish.Select(p => p.Name)))}");
        }

        Console.WriteLine();
        Console.WriteLine("## 同名文件（prefab 的实体几何在 .pmd/.pmg 这一层）");
        string baseName = Path.GetFileNameWithoutExtension(ppdPath);
        string directory = ppdPath[..(ppdPath.LastIndexOf('/') + 1)];
        IList<string> siblings;
        try { siblings = holder.GetFiles(directory); }
        catch (Exception ex)
        {
            Console.WriteLine($"# 目录 {directory} 列不出来：{ex.GetType().Name}");
            return 5;
        }

        string[] sameBase = siblings
            .Where(f => Path.GetFileNameWithoutExtension(f).Equals(baseName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (string file in sameBase) Console.WriteLine($"  {file}");

        string? pmd = sameBase.FirstOrDefault(f => f.EndsWith(".pmd", StringComparison.OrdinalIgnoreCase));
        if (pmd == null)
        {
            Console.WriteLine("  没有同名 .pmd：几何不在 prefab 自己的文件里，得由地图条目的 Model token 反查。");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"## 打开 {pmd}");
        TruckLib.Models.Model model;
        try { model = TruckLib.Models.Model.Open(pmd, holder); }
        catch (Exception ex)
        {
            Console.WriteLine($"# Model.Open 失败：{ex.GetType().Name}: {ex.Message}");
            return 0;
        }

        AxisAlignedBox box = model.BoundingBox;
        Console.WriteLine($"  Looks {model.Looks.Count} · Variants {model.Variants.Count} · Parts {model.Parts.Count} · "
                          + $"Skeleton {model.Skeleton.Count} · 包围盒中心 {model.BoundingBoxCenter} 对角 {model.BoundingBoxDiagonalSize:0.0} m");

        int propHits = 0;
        foreach (Part part in model.Parts.Take(40))
        {
            string name = SafeToken(part.Name);
            bool hit = PropWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (hit) propHits++;
            string pieces = string.Join(" / ", part.Pieces.Take(2).Select(p => $"{p.BoundingBoxCenter:0.0} {p.Vertices.Count}v"));
            Console.WriteLine($"  part {(hit ? "★" : " ")} {name,-26} pieces {part.Pieces.Count,3} locators {part.Locators.Count,2}  {pieces}");
        }
        if (model.Parts.Count > 40) Console.WriteLine($"  …（共 {model.Parts.Count} 个 part）");

        Console.WriteLine();
        Console.WriteLine($"## 骨骼 {model.Skeleton.Count} 个（前 40）");
        foreach (Bone bone in model.Skeleton.Take(40))
            Console.WriteLine($"  {SafeToken(bone.Name),-26} T {bone.Translation} parent {bone.Parent}");

        Console.WriteLine();
        Console.WriteLine($"## 判定：part 名带道具词的 {propHits}/{model.Parts.Count}；骨骼 {model.Skeleton.Count}；"
                          + $"模型包围盒 {box}");
        return 0;
    }

    static string SafeToken(Token token)
    {
        try
        {
            string decoded = token.String;
            return decoded.Length > 0 ? decoded : token.Value.ToString();
        }
        catch { return token.Value.ToString(); }
    }

    // ── token ──────────────────────────────────────────────────────────────────────────

    static int TokenMode(List<string> operands)
    {
        if (operands.Count == 0)
        {
            Console.WriteLine("# 例如: token 1061 5110 277 pylcon_big d_aport_phx");
            return 2;
        }

        foreach (string raw in operands)
        {
            if (ulong.TryParse(raw, out ulong value))
            {
                Token token = new(value);
                Console.WriteLine($"  {value,-22} → \"{token.String}\"  ({token.String.Length} 字符)");
                continue;
            }

            if (Token.IsValidToken(raw))
            {
                Token token = new(raw);
                Console.WriteLine($"  \"{raw}\" → {token.Value}  ({raw.Length} 字符, 上限 {Token.MaxLength})");
            }
            else
            {
                Console.WriteLine($"  \"{raw}\" 不是合法 token：超过 {Token.MaxLength} 字符或含字符集外的字符"
                                  + "（0-9 a-z _）。这样的名字在地图条目里只能以数值形式出现。");
            }
        }
        return 0;
    }

    // ── scan ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///  Does any sector in the shipped map carry an item pointing at these units? A subcurve writes its
    ///  model as a raw 8-byte token and Curve items live in the sector's .aux file, so the whole map can
    ///  be answered by searching those bytes instead of parsing every sector. A negative result here
    ///  means the object is not map content at all, which is a different fix than a fidelity setting.
    /// </summary>
    static int ScanMode(string gameDir, List<string> operands)
    {
        string[] names = operands.Count > 0 ? operands.ToArray() : new[] { "2x002", "rus_2y002" };
        Dictionary<string, ulong> needles = new();
        foreach (string name in names)
        {
            if (!Token.IsValidToken(name))
            {
                Console.WriteLine($"# \"{name}\" 不能作 token（超过 {Token.MaxLength} 字符），跳过");
                continue;
            }
            needles[name] = new Token(name).Value;
        }
        if (needles.Count == 0) return 2;

        Console.WriteLine("# 搜索 token：" + string.Join(", ", needles.Select(kv => $"{kv.Key}={kv.Value}")));

        string[] mapDirs = { "/map/europe/", "/map/usa/" };
        string[] archives = Directory.GetFiles(gameDir, "*.scs")
            .Where(f => Path.GetFileName(f).Equals("base_map.scs", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(f).StartsWith("dlc_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Console.WriteLine($"# 候选归档 {archives.Length} 个");
        int totalHits = 0;
        int filesScanned = 0;

        foreach (string archive in archives)
        {
            IFileSystem fs;
            try { fs = OpenArchive(archive); }
            catch (Exception ex)
            {
                Console.WriteLine($"#   {Path.GetFileName(archive)}：打不开（{ex.GetType().Name}）");
                continue;
            }

            using (fs as IDisposable)
            {
                foreach (string mapDir in mapDirs)
                {
                    IList<string> sectors;
                    try
                    {
                        sectors = fs.GetFiles(mapDir)
                            .Where(f => Path.GetFileName(f).StartsWith("sec-", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    }
                    catch { continue; }

                    if (sectors.Count == 0) continue;

                    int archiveHits = 0;
                    foreach (string sector in sectors)
                    {
                        byte[] bytes;
                        try { bytes = fs.ReadAllBytes(sector); }
                        catch { continue; }
                        filesScanned++;

                        foreach ((string name, ulong value) in needles)
                        {
                            int count = Count(bytes, BitConverter.GetBytes(value));
                            if (count == 0) continue;
                            archiveHits += count;
                            totalHits++;
                            Console.WriteLine($"  {Path.GetFileName(archive)}  {sector}  {name} ×{count}");
                        }
                    }

                    Console.WriteLine($"#   {Path.GetFileName(archive)}：{mapDir} 扇区文件 {sectors.Count}，命中 {archiveHits}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"## 扫过 {filesScanned} 个扇区文件，命中 {totalHits} 处");
        return 0;
    }

    static int Count(byte[] data, ReadOnlySpan<byte> pattern)
    {
        int count = 0;
        ReadOnlySpan<byte> rest = data;
        int offset = 0;
        while (true)
        {
            int idx = rest.IndexOf(pattern);
            if (idx < 0) break;
            count++;
            offset += idx + 1;
            rest = data.AsSpan(offset);
        }
        return count;
    }

    // ── curve ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///  Reads one staged sector and prints its Curve items — the item class that repeats a model along
    ///  a path, which is where the traffic cones live (`curve.2x002` → /model2/sign/traffic_cone_02_fr.pmd).
    ///  This is the layer doc 30/32 could not see: Curve is ItemType 0x2C, dropped below Extreme fidelity,
    ///  and the plugin's classifier never printed its subcurve tokens.
    /// </summary>
    static int CurveMode(string gameDir, List<string> operands)
    {
        if (operands.Count == 0)
        {
            Console.WriteLine("# 需要扇区前缀，例如 /map/europe/sec-0002+0002（scan 模式会给出命中的扇区）");
            return 2;
        }

        string sectorPrefix = operands[0].TrimEnd('/', '\\');
        string tokenFilter = operands.Count > 1 ? operands[1] : "";

        Dictionary<string, CurveDef> defs = LoadCurveDefs(gameDir);
        Console.WriteLine($"# /def/world/curve_model*.sii 里 {defs.Count} 个 curve 单位");

        string stage = Path.Combine(Path.GetTempPath(), "autoparking-objectprobe");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);

        List<(string Name, IFileSystem Fs)> archives = OpenArchives(gameDir);
        int stagedFiles = 0;
        string? mbdLocal = null;
        foreach ((string name, IFileSystem fs) in archives)
        {
            try
            {
                foreach (string mbd in fs.GetFiles("/map/").Where(f => f.EndsWith(".mbd")))
                {
                    string dir = mbd[..(mbd.LastIndexOf('/') + 1)];
                    string sectorDir = SectorDirectoryOf(mbd);
                    IList<string> candidates;
                    try { candidates = fs.GetFiles(sectorDir); }
                    catch { continue; }

                    string[] wanted = candidates.Where(f => Path.GetFileName(f).StartsWith(
                        Path.GetFileName(sectorPrefix), StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (wanted.Length == 0) continue;

                    CopyAll(fs, mbd, stage);
                    mbdLocal = LocalOf(stage, mbd);
                    foreach (string file in wanted) { CopyAll(fs, file, stage); stagedFiles++; }
                    Console.WriteLine($"# 暂存自 {name}：{mbd} + {string.Join(", ", wanted.Select(Path.GetFileName))}");
                }
            }
            catch { }
        }

        if (mbdLocal == null)
        {
            Console.WriteLine($"# 没有任何归档带 {sectorPrefix}.* 的扇区文件");
            return 4;
        }

        Map map = Map.Open(mbdLocal);
        Console.WriteLine($"# 解析完成：节点 {map.Nodes.Count} · 条目 {map.MapItems.Count}");

        Dictionary<string, int> tally = new();
        int shown = 0;
        foreach (MapItem item in map.MapItems.Values)
        {
            if (item is not Curve curve) continue;

            string token = curve.Subcurves.FirstOrDefault(s => s != null)?.Model.String ?? "";
            tally[token] = tally.GetValueOrDefault(token) + 1;

            if (tokenFilter.Length > 0 && !token.Contains(tokenFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (shown++ == 40) { Console.WriteLine("  …（前 40 条）"); break; }

            Vector3 a = curve.Node.Position;
            Vector3 b = curve.ForwardNode.Position;
            CurveDef? def = defs.TryGetValue(token, out CurveDef? found) ? found : null;
            double straight = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));

            Console.WriteLine();
            Console.WriteLine($"  Curve {curve.Uid:X16}  token \"{token}\"  长 {curve.Length:0.0} m（两节点直线 {straight:0.0} m）"
                              + $"  碰撞 {(curve.Collision ? "开" : "关")}  定位点 {curve.Locators.Count}");
            Console.WriteLine($"    后节点 ({a.X:0.0}, {a.Y:0.0}, {a.Z:0.0}) → 前节点 ({b.X:0.0}, {b.Y:0.0}, {b.Z:0.0})");
            for (int i = 0; i < curve.Subcurves.Length; i++)
            {
                Subcurve? sub = curve.Subcurves[i];
                if (sub == null) continue;
                Console.WriteLine($"    子曲线 {i}: model=\"{sub.Model}\" look=\"{sub.Look}\" first=\"{sub.FirstPart}\" "
                                  + $"last=\"{sub.LastPart}\" 缩放 {sub.Scale:0.00} 拉伸 {sub.Stretch:0.00} 固定步距 {sub.FixedStep:0.00}");
            }
            if (def != null)
            {
                Console.WriteLine($"    定义：{def.Desc}  （name=\"{def.DisplayName}\" 步距 {def.Step:0.00} m）");
                float step = def.Step > 0 ? def.Step : 1f;
                int instances = (int)(curve.Length / step);
                Console.WriteLine($"    沿路实例数 ≈ {instances}；前 8 个（按两节点直线等分，真实路径是样条，位置会更弯）：");
                for (int i = 0; i < Math.Min(8, instances); i++)
                {
                    float t = (i * step) / Math.Max(curve.Length, 0.001f);
                    Console.WriteLine($"      ({a.X + (b.X - a.X) * t:0.0}, {a.Z + (b.Z - a.Z) * t:0.0})");
                }
            }
            else if (token.Length > 0)
            {
                Console.WriteLine($"    定义：/def 里查不到 \"{token}\"（可能来自 DLC 的 curve_model.*.sii）");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"## 本扇区 Curve 条目 {tally.Values.Sum()} 个，token 种类 {tally.Count}");
        foreach ((string token, int count) in tally.OrderByDescending(kv => kv.Value).Take(25))
        {
            string desc = defs.TryGetValue(token, out CurveDef? d) ? d.Desc : "";
            Console.WriteLine($"  {token,-16} ×{count,-4} {desc}");
        }
        Console.WriteLine($"# 暂存文件 {stagedFiles} 个于 {stage}");
        return 0;
    }

    sealed record CurveDef(string Name, string Desc, string DisplayName, float Step);

    static Dictionary<string, CurveDef> LoadCurveDefs(string gameDir)
    {
        Dictionary<string, CurveDef> defs = new();
        foreach ((string _, IFileSystem fs) in OpenArchives(gameDir))
        {
            IList<string> files;
            try { files = fs.GetFiles("/def/world/"); }
            catch { continue; }

            foreach (string siiPath in files.Where(f => f.EndsWith(".sii") && Path.GetFileName(f).Contains("curve_model")))
            {
                SiiFile sii;
                try { sii = SiiFile.Open(siiPath, fs, true); }
                catch { continue; }

                foreach (Unit unit in sii.Units)
                {
                    string name = (unit.Name ?? "").StartsWith("curve.") ? (unit.Name ?? "")[6..] : (unit.Name ?? "");
                    IDictionary attrs = (IDictionary)unit.Attributes;
                    defs[name] = new CurveDef(name,
                        AttrOf(attrs, "model_desc") ?? "",
                        AttrOf(attrs, "name") ?? "",
                        ParseFloat(AttrOf(attrs, "fixed_step")));
                }
            }
        }
        return defs;
    }

    static float ParseFloat(string? value)
        => float.TryParse(value, NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;

    static string SectorDirectoryOf(string mbdPath)
    {
        int cut = mbdPath.LastIndexOf('/');
        string parent = cut < 0 ? "" : mbdPath[..(cut + 1)];
        return $"{parent}{Path.GetFileNameWithoutExtension(mbdPath)}/";
    }

    static void CopyAll(IFileSystem fs, string archivePath, string stage)
    {
        string local = LocalOf(stage, archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllBytes(local, fs.ReadAllBytes(archivePath));
    }

    static string LocalOf(string stage, string archivePath)
        => Path.Combine(stage, archivePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    // ── mapscan ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///  Whole-map inventory: every item class, and inside each class which model the item points at
    ///  (resolved through /def/world/*.sii, because ETS2 unit names are opaque codes and only the
    ///  definition's file path says what the object is). This is the measurement that decides whether a
    ///  given obstacle is "in the data and we are not reading it" versus "not in the data at all" —
    ///  doc 30/32 could only ask the question because the Curve layer was invisible to them.
    /// </summary>
    static int MapScanMode(string gameDir, List<string> operands)
    {
        string[] keywords = operands.Count > 0
            ? operands.ToArray()
            : new[] { "cone", "barrier", "construction", "work", "scaffold", "fence" };

        string stage = Path.Combine(Path.GetTempPath(), "autoparking-objectmap");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);

        Console.WriteLine("# 暂存整张地图的扇区文件…");
        int staged = StageSectors(gameDir, stage, out string? mbdLocal);
        if (mbdLocal == null)
        {
            Console.WriteLine("# 没找到 .mbd 或任何扇区文件。");
            return 4;
        }
        Console.WriteLine($"# 暂存 {staged} 个扇区文件，地图描述符 {mbdLocal}");

        InventoryMap map = new();
        map.Read(mbdLocal);
        Console.WriteLine($"# 解析完成：条目 {map.MapItems.Count} · 节点 {map.Nodes.Count}");

        Dictionary<string, string> defs = LoadAllDefs(gameDir);
        Dictionary<string, int> byType = new();
        Dictionary<string, string> descOf = new();
        Dictionary<string, List<ItemHit>> byDescWord = new();
        Dictionary<string, int> unresolved = new();

        foreach (MapItem item in map.MapItems.Values)
        {
            string type = item.ItemType.ToString();
            byType[type] = byType.GetValueOrDefault(type) + 1;

            (string[] units, string token, Vector3 a, Vector3? b, float length) = Describe(item);
            if (units.Length == 0) continue;

            // `dlc_fr_219` exists as `bld_model.dlc_fr_219` (a barrier column), `bld_scheme.dlc_fr_219` and
            // `model.dlc_fr_219` (a transformer) at once, so the bare code the item stores is not an
            // identity — only the class-qualified unit name is.
            string key = units[0];
            string desc = descOf.TryGetValue(key, out string? cached)
                ? cached
                : units.Select(u => defs.GetValueOrDefault(u, "")).FirstOrDefault(s => s.Length > 0) ?? "";
            descOf[key] = desc;
            if (desc.Length == 0)
            {
                unresolved[type] = unresolved.GetValueOrDefault(type) + 1;
                continue;
            }

            foreach (string word in keywords)
            {
                if (!desc.Contains(word, StringComparison.OrdinalIgnoreCase)) continue;
                if (!byDescWord.TryGetValue(word, out List<ItemHit>? list))
                    byDescWord[word] = list = new();
                list.Add(new ItemHit(token, desc, type, item.Uid, a, b, length));
            }
        }

        Console.WriteLine();
        Console.WriteLine("## 条目类型清点（括号里是 token 查不到定义的数量）");
        foreach ((string type, int count) in byType.OrderByDescending(kv => kv.Value))
        {
            int missing = unresolved.GetValueOrDefault(type);
            Console.WriteLine($"  {type,-18} {count}{(missing > 0 ? $"  （未解析 {missing}）" : "")}");
        }

        Console.WriteLine();
        Console.WriteLine("## 按模型路径关键词");
        foreach (string word in keywords)
        {
            var hits = byDescWord.TryGetValue(word, out var found) ? found : new();
            Dictionary<string, int> tokens = hits.GroupBy(h => h.Token).ToDictionary(g => g.Key, g => g.Count());
            Dictionary<string, int> types = hits.GroupBy(h => h.Type).ToDictionary(g => g.Key, g => g.Count());
            Console.WriteLine($"  \"{word}\"：{hits.Count} 个条目，token {tokens.Count} 种；按类型 "
                              + string.Join(" · ", types.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ×{kv.Value}")));

            foreach ((string token, int count) in tokens.OrderByDescending(kv => kv.Value).Take(8))
            {
                string kinds = string.Join("/", hits.Where(h => h.Token == token).Select(h => h.Type).Distinct());
                Console.WriteLine($"      {token,-16} ×{count,-5} [{kinds}] {descOf.GetValueOrDefault(token, hits.First(h => h.Token == token).Desc)}");
            }
            foreach (var hit in hits.Take(6))
            {
                string sector = Map.GetSectorOfCoordinate(hit.A).ToString();
                string to = hit.B == null ? "" : $" → ({hit.B.Value.X:0}, {hit.B.Value.Z:0})";
                Console.WriteLine($"      例：{hit.Type} uid {hit.Uid:X16} 扇区 {sector} · ({hit.A.X:0}, {hit.A.Z:0}){to} · 长 {hit.Length:0.0} m");
            }
        }

        return 0;
    }

    record ItemHit(string Token, string Desc, string Type, ulong Uid, Vector3 A, Vector3? B, float Length);

    /// <summary>
    ///  Drops the classes that are pure ground paint so a whole-map read fits in RAM; the item-class
    ///  tally below therefore reports what the probe kept, not the game's total.
    /// </summary>
    sealed class InventoryMap : Map
    {
        protected override bool PostProcessItem(MapItem item)
            => item is not (Terrain or BezierPatch or FarModel or MapOverlay);

        protected override void OnSectorLoading(Sector sector, int index, int total)
        {
            if (index % 100 == 0) Console.WriteLine($"#   扇区 {index}/{total}");
        }
    }

    /// <summary>
    ///  The item's model reference plus the ground geometry we can already reach for it. The unit name is
    ///  rebuilt with each prefix the class could use, because the bare code is not unique across classes.
    /// </summary>
    static (string[] Units, string Token, Vector3 A, Vector3? B, float Length) Describe(MapItem item) => item switch
    {
        Curve curve => With(new[] { "curve" },
                            curve.Subcurves.FirstOrDefault(s => s != null)?.Model.String ?? "",
                            curve.Node.Position, curve.ForwardNode?.Position, curve.Length),
        TruckLib.ScsMap.Model model => With(new[] { "model" }, model.Name.String, model.Node.Position, null, 0f),
        Prefab prefab => With(new[] { "prefab" }, prefab.Model.String, prefab.Nodes[0].Position, null, 0f),
        TruckLib.ScsMap.Sign sign => With(new[] { "sign" }, sign.Model.String, sign.Node.Position, null, 0f),
        AnimatedModel animated => With(new[] { "animated_model", "model" }, animated.Model.String, animated.Node.Position, null, 0f),
        Buildings buildings => With(new[] { "bld_model", "bld_scheme" }, buildings.Name.String,
                                    buildings.Node.Position, buildings.ForwardNode?.Position, 0f),
        _ => (Array.Empty<string>(), "", Vector3.Zero, null, 0f),
    };

    static (string[], string, Vector3, Vector3?, float) With(string[] prefixes, string token,
                                                             Vector3 a, Vector3? b, float length)
        => (token.Length == 0 ? Array.Empty<string>() : prefixes.Select(p => $"{p}.{token}").ToArray(),
            token, a, b, length);

    static string CurveDescFor(string token)
        => CurveDefs?.TryGetValue(token, out CurveDef? def) == true ? def.Desc : "";

    static Dictionary<string, CurveDef>? CurveDefs;

    /// <summary>
    ///  Every unit in /def/world/*.sii that carries a model/prefab path, keyed by its full unit name
    ///  (`model.1573`, `curve.2x002`, `sign.dlc_fr_219`…) — the class prefix is part of the identity.
    /// </summary>
    static Dictionary<string, string> LoadAllDefs(string gameDir)
    {
        Dictionary<string, string> defs = new();
        foreach ((string _, IFileSystem fs) in OpenArchives(gameDir))
        {
            IList<string> files;
            try { files = fs.GetFiles("/def/world/"); }
            catch { continue; }

            foreach (string siiPath in files.Where(f => f.EndsWith(".sii")))
            {
                SiiFile sii;
                try { sii = SiiFile.Open(siiPath, fs, true); }
                catch { continue; }

                foreach (Unit unit in sii.Units)
                {
                    IDictionary attrs = (IDictionary)unit.Attributes;
                    string? desc = AttrOf(attrs, "model_desc") ?? AttrOf(attrs, "prefab_desc");
                    if (string.IsNullOrEmpty(desc)) continue;
                    defs[unit.Name ?? ""] = desc;
                }
            }
        }
        return defs;
    }

    static int StageSectors(string gameDir, string stage, out string? mbdLocal)
    {
        mbdLocal = null;
        List<string> archives = Directory.GetFiles(gameDir, "*.scs")
            .Where(f =>
            {
                string name = Path.GetFileName(f);
                return name.Equals("base_map.scs", StringComparison.OrdinalIgnoreCase)
                       || name.StartsWith("dlc_", StringComparison.OrdinalIgnoreCase)
                       || name.StartsWith("mod_", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int copied = 0;
        List<string> sectorDirs = new();
        bool mbdSeen = false;
        foreach (string archive in archives)
        {
            IFileSystem fs;
            try { fs = OpenArchive(archive); }
            catch { continue; }

            using (fs as IDisposable)
            {
                // A DLC adds sectors to the map without shipping an .mbd of its own, so the sector
                // directories accumulate across archives and every later archive is asked for sec-*
                // files inside all of them. Requiring an .mbd per archive is what silently dropped the
                // whole DLC map content on the floor the first time.
                try
                {
                    foreach (string mbd in fs.GetFiles("/map/").Where(f => f.EndsWith(".mbd")))
                    {
                        string dir = SectorDirectoryOf(mbd);
                        if (!sectorDirs.Contains(dir)) sectorDirs.Add(dir);
                        if (!mbdSeen)
                        {
                            CopyAll(fs, mbd, stage);
                            mbdLocal = LocalOf(stage, mbd);
                            mbdSeen = true;
                        }
                    }
                }
                catch { }

                foreach (string dir in sectorDirs)
                {
                    IList<string> files;
                    try { files = fs.GetFiles(dir); }
                    catch { continue; }

                    foreach (string file in files.Where(f => Path.GetFileName(f).StartsWith("sec-", StringComparison.OrdinalIgnoreCase)))
                    {
                        CopyAll(fs, file, stage);
                        copied++;
                    }
                }
            }
        }
        return copied;
    }

    /// <summary>
    ///  Are the real extents in the map files? `MapItem.Kdop` is an internal property, but its type
    ///  derives from the public `KdopBounds` with `Minimums[5]`/`Maximums[5]`, which is the same 25-DOP
    ///  the game keeps in memory (kdop_item_t carries minimums[5]/maximums[5]). Reading it through
    ///  reflection settles whether a static obstacle needs a PMD parse per token to have a size at all.
    /// </summary>
    static int KdopMode(string gameDir, List<string> operands)
    {
        if (operands.Count == 0)
        {
            Console.WriteLine("# 用法: kdop <扇区前缀> [token 或关键词过滤]");
            return 2;
        }

        string stage = Path.Combine(Path.GetTempPath(), "autoparking-objectprobe");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);

        string? mbdLocal = StageOneSector(gameDir, operands[0].TrimEnd('/', '\\'), stage);
        if (mbdLocal == null) return 4;

        Map map = Map.Open(mbdLocal);
        Dictionary<string, string> defs = LoadAllDefs(gameDir);
        string filter = operands.Count > 1 ? operands[1] : "";

        PropertyInfo? kdopProperty = typeof(MapItem).GetProperty("Kdop",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        Console.WriteLine($"# MapItem.Kdop 可见性：{(kdopProperty == null ? "取不到" : kdopProperty.Name)}，"
                          + $"类型 {kdopProperty?.PropertyType.Name}（公开基类 KdopBounds）");
        if (kdopProperty == null) return 5;

        int printed = 0;
        Dictionary<string, int> withExtents = new();
        foreach (MapItem item in map.MapItems.Values)
        {
            (string[] units, string token, Vector3 a, Vector3? b, float length) = Describe(item);
            if (units.Length == 0) continue;

            string desc = units.Select(u => defs.GetValueOrDefault(u, "")).FirstOrDefault(s => s.Length > 0) ?? "";
            string kind = item.ItemType.ToString();
            if (filter.Length > 0 && !desc.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                   && !token.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (item.Uid % 7 != 0 && filter.Length == 0 && printed > 12) continue;

            object? raw = kdopProperty.GetValue(item);
            if (raw is not KdopBounds bounds)
            {
                Console.WriteLine($"  {kind} {token} → 反射拿不到 KdopBounds");
                continue;
            }

            string min = string.Join(", ", bounds.Minimums.Select(v => $"{v:0.0}"));
            string max = string.Join(", ", bounds.Maximums.Select(v => $"{v:0.0}"));
            withExtents[kind] = withExtents.GetValueOrDefault(kind) + 1;

            if (printed++ > 24) continue;
            Console.WriteLine($"  {kind,-9} {token,-14} 节点 ({a.X:0.0}, {a.Y:0.0}, {a.Z:0.0})");
            Console.WriteLine($"      min [{min}]");
            Console.WriteLine($"      max [{max}]   → {desc}");
        }

        Console.WriteLine();
        Console.WriteLine("## 每类都取到了 KdopBounds：");
        foreach ((string kind, int count) in withExtents.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {kind,-12} {count}");
        return 0;
    }

    /// <summary>
    ///  Stages the .mbd plus one sector's files from the first archive that carries them.
    /// </summary>
    static string? StageOneSector(string gameDir, string sectorPrefix, string stage)
    {
        string? mbdLocal = null;
        List<string> sectorDirs = new();

        foreach ((string name, IFileSystem fs) in OpenArchives(gameDir, includeDlc: true))
        {
            // A DLC adds sectors without carrying an .mbd of its own, so the directories to look in
            // accumulate across archives. Deriving them only from the archive that has the .mbd made
            // every DLC get skipped here and reported "that sector does not exist".
            try
            {
                foreach (string mbd in fs.GetFiles("/map/").Where(f => f.EndsWith(".mbd")))
                {
                    string dir = SectorDirectoryOf(mbd);
                    if (!sectorDirs.Contains(dir)) sectorDirs.Add(dir);
                    if (mbdLocal == null)
                    {
                        CopyAll(fs, mbd, stage);
                        mbdLocal = LocalOf(stage, mbd);
                    }
                }
            }
            catch { }

            foreach (string dir in sectorDirs)
            {
                IList<string> candidates;
                try { candidates = fs.GetFiles(dir); }
                catch { continue; }

                foreach (string file in candidates.Where(f => Path.GetFileName(f).StartsWith(
                             Path.GetFileName(sectorPrefix), StringComparison.OrdinalIgnoreCase)))
                {
                    CopyAll(fs, file, stage);
                    Console.WriteLine($"# 暂存自 {name}：{file}");
                }
            }
        }

        if (mbdLocal == null)
            Console.WriteLine($"# 没有任何归档带 {sectorPrefix}.* 的扇区文件");
        return mbdLocal;
    }

    /// <summary>
    ///  "What is actually standing within N meters of this world coordinate?" — asked against the files
    ///  instead of the running plugin, so a plugin read that finds nothing can be told apart from data
    ///  that is not there. Stages the one sector containing the point, from any archive including DLCs.
    /// </summary>
    static int NearMode(string gameDir, List<string> operands)
    {
        if (operands.Count < 2)
        {
            Console.WriteLine("# 用法: near <游戏目录> <x> <z> [半径，默认 120]");
            return 2;
        }

        float x = float.Parse(operands[0], CultureInfo.InvariantCulture);
        float z = float.Parse(operands[1], CultureInfo.InvariantCulture);
        double radius = operands.Count > 2
            ? double.Parse(operands[2], CultureInfo.InvariantCulture) : 120.0;

        SectorCoordinate sector = Map.GetSectorOfCoordinate(new Vector3(x, 0f, z));
        string sectorName = Sector.SectorFileNameFromSectorCoords(sector);
        Console.WriteLine($"# 坐标 ({x:0}, {z:0}) → 扇区 {sectorName}（每扇区 {Map.SectorSize} m）");

        string stage = Path.Combine(Path.GetTempPath(), "autoparking-objectprobe");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);

        string? mbdLocal = StageOneSector(gameDir, sectorName, stage);
        if (mbdLocal == null) return 4;

        Map map = Map.Open(mbdLocal);
        Dictionary<string, string> defs = LoadAllDefs(gameDir);
        Vector2 center = new(x, z);

        List<(double Distance, string Kind, string Token, string Desc, Vector2[] Points, bool Collision, ulong Uid)> found = new();
        foreach (MapItem item in map.MapItems.Values)
        {
            (string[] units, string token, Vector3 a, Vector3? b, float length) = Describe(item);
            Vector2 at = new(a.X, a.Z);
            double distance = Math.Sqrt(Math.Pow(at.X - center.X, 2) + Math.Pow(at.Y - center.Y, 2));

            // A polyline item can reach into the circle with its far end outside it, so test the
            // segment-to-point distance as well as the start node.
            if (distance > radius && b != null)
            {
                Vector2 to = new(b.Value.X, b.Value.Z);
                distance = Math.Min(distance, SegmentDistance(center, at, to));
            }
            if (distance > radius) continue;

            string desc = units.Length == 0 ? ""
                : units.Select(u => defs.GetValueOrDefault(u, "")).FirstOrDefault(s => s.Length > 0) ?? "";
            found.Add((distance, item.ItemType.ToString(), token, desc, Array.Empty<Vector2>(),
                       CollisionOf(item), item.Uid));
        }

        Console.WriteLine($"# 扇区共 {map.MapItems.Count} 条目 / {map.Nodes.Count} 节点；{radius:0} m 内 {found.Count} 个");
        foreach (var hit in found.OrderBy(h => h.Distance).Take(60))
            Console.WriteLine($"  {hit.Distance,7:0.0} m  {hit.Kind,-10} {hit.Token,-16} 碰撞 {(hit.Collision ? "开" : "关")}  uid {hit.Uid:X16}  {hit.Desc}");
        if (found.Count > 60) Console.WriteLine($"  …（共 {found.Count}）");

        Console.WriteLine();
        Console.WriteLine("## 按类型");
        foreach ((string kind, int count) in found.GroupBy(h => h.Kind).Select(g => (g.Key, g.Count()))
                                                  .OrderByDescending(g => g.Item2))
            Console.WriteLine($"  {kind,-12} {count}");
        return 0;
    }

    static bool CollisionOf(MapItem item) => item switch
    {
        TruckLib.ScsMap.Model model => model.Collision,
        Buildings buildings => buildings.Collision,
        Curve curve => curve.Collision,
        Compound compound => compound.Collision,
        BezierPatch patch => patch.Collision,
        _ => false,
    };

    static double SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        double length2 = ab.X * ab.X + ab.Y * ab.Y;
        if (length2 < 1e-6) return Math.Sqrt((p - a).X * (p - a).X + (p - a).Y * (p - a).Y);
        double t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / length2, 0, 1);
        Vector2 closest = a + ab * (float)t;
        return Math.Sqrt((p.X - closest.X) * (p.X - closest.X) + (p.Y - closest.Y) * (p.Y - closest.Y));
    }

    // ── archives ────────────────────────────────────────────────────────────────────────

    static IFileSystem OpenArchive(string path)
    {
        bool zip;
        using (FileStream probe = File.OpenRead(path))
            zip = probe.ReadByte() == 'P' && probe.ReadByte() == 'K';

        return zip ? (IFileSystem)new ZipFileSystem(path) : HashFsReader.Open(path);
    }

    /// <summary>
    ///  Every archive the game would mount, base first and DLC after - the same order the game uses,
    ///  because a DLC redefines units that the base archives also name.
    /// </summary>
    static List<(string Name, IFileSystem Fs)> OpenArchives(string gameDir, bool includeDlc = false)
    {
        List<string> paths = BaseArchives.Select(name => Path.Combine(gameDir, name))
                                         .Where(File.Exists).ToList();
        if (includeDlc)
        {
            paths.AddRange(Directory.GetFiles(gameDir, "dlc_*.scs")
                                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase));
        }

        List<(string Name, IFileSystem Fs)> opened = new();
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            try { opened.Add((name, OpenArchive(path))); }
            catch (Exception ex)
            {
                Console.WriteLine($"#   {name}：打不开（{ex.GetType().Name}: {ex.Message}）");
            }
        }
        return opened;
    }
}

/// <summary>
///  Plain zips (mods, and some base archives) are read the same way as the hashed ones.
/// </summary>
internal sealed class ZipFileSystem : IFileSystem
{
    private readonly ZipArchive archive;

    public ZipFileSystem(string path) => archive = ZipFile.OpenRead(path);

    public char DirectorySeparator => '/';
    public bool FileExists(string path) => Entry(path) != null;
    public bool DirectoryExists(string path) => GetFiles(path).Count > 0;
    public Stream Open(string path) => Entry(path)?.Open() ?? throw new FileNotFoundException(path);
    public string GetParent(string path) => path.TrimEnd('/')[..(path.TrimEnd('/').LastIndexOf('/') + 1)];

    public byte[] ReadAllBytes(string path)
    {
        using Stream s = Open(path);
        using MemoryStream buffer = new();
        s.CopyTo(buffer);
        return buffer.ToArray();
    }

    public string ReadAllText(string path) => ReadAllText(path, System.Text.Encoding.UTF8);
    public string ReadAllText(string path, System.Text.Encoding encoding) => encoding.GetString(ReadAllBytes(path));

    public IList<string> GetFiles(string path)
    {
        string prefix = path.TrimStart('/');
        if (prefix.Length > 0 && !prefix.EndsWith("/")) prefix += "/";

        return archive.Entries
            .Select(e => "/" + e.FullName.Replace('\\', '/'))
            .Where(p => p.StartsWith("/" + prefix, StringComparison.OrdinalIgnoreCase) && !p.EndsWith("/"))
            .ToList();
    }

    private ZipArchiveEntry? Entry(string path)
    {
        string normalized = path.TrimStart('/');
        return archive.GetEntry(normalized) ?? archive.GetEntry(normalized.Replace('/', Path.DirectorySeparatorChar));
    }
}
