using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TruckLib;
using TruckLib.Sii;

namespace AutoParking;

/// <summary>
///  What a map item actually IS.
///
///  A map item only carries a short unit code (`dlc_fr_219`, `1573`, `2x002`), and the code is not an
///  identity on its own: `dlc_fr_219` exists at the same time as `bld_model.dlc_fr_219` (a barrier
///  column), `bld_scheme.dlc_fr_219` and `model.dlc_fr_219` (a transformer). The readable identity is
///  the file the unit points at (`/model2/sign/traffic_cone_01_fr.pmd`), which is why the lookup key
///  here is the class-qualified unit name and nothing else. Measured across the whole shipped Europe
///  map: 959 cone items (927 of them `Sign`), 108 132 barrier items, 7 045 construction props.
///
///  Parsing `/def/world/*.sii` is the expensive part - 173 files, ~10 s on this machine - so the table
///  fills incrementally through <see cref="Step"/> and everything falls back to "unknown" until it is
///  ready. Unknown is a real answer here: the planner must not block on something it cannot name.
/// </summary>
public sealed class MapItemIdentity
{
    private sealed record Entry(string Desc, float Step);

    /// <summary>
    ///  Ordered so the specific families win: the first entry whose marker appears in the model path
    ///  decides the label. `Hard` is what a route may not cross; everything not marked hard is drawn but
    ///  trusted not to stop the truck (road paint, vegetation, wires overhead).
    /// </summary>
    private static readonly (string Marker, string Label, bool Hard)[] Families =
    {
        ("cone", "锥桶", true),
        ("barrier", "护栏", true),
        ("fence", "围栏", true),
        ("railing", "护栏", true),
        ("wall", "墙", true),
        ("scaffold", "脚手架", true),
        ("formwork", "施工件", true),
        ("construction", "施工件", true),
        ("crate", "箱子", true),
        ("pallet", "托盘", true),
        ("container", "集装箱", true),
        ("barrel", "防撞桶", true),
        ("heap", "料堆", true),
        ("pile", "桩料", true),
        ("bollard", "防撞柱", true),
        ("lamp", "灯杆", false),
        ("street_light", "灯杆", false),
        ("power_line", "电线", false),
        ("powerline", "电线", false),
        ("treewall", "植被", false),
        ("vegetation", "植被", false),
        ("/tree", "植被", false),
        ("grass", "植被", false),
        ("shrub", "植被", false),
        ("hedge", "植被", false),
        ("curb", "路面标线", false),
        ("lines", "路面标线", false),
        ("decal", "路面标线", false),
        ("marking", "路面标线", false),
        ("warehouse", "建筑", true),
        ("/building/", "建筑", true),
        ("hall", "建筑", true),
        ("administration", "建筑", true),
    };

    public const string UnknownLabel = "未分类";

    // Only these definition files carry the classes a map item can reference; the rest of /def/world/
    // is traffic defs, paint and audio, which cost the same to parse and answer nothing.
    private static readonly string[] InterestingFileNameParts = { "model", "sign", "prefab" };

    private readonly Dictionary<string, Entry> byUnit = new(StringComparer.Ordinal);
    private List<string> pending = new();
    private IFileSystem? bound;

    public int TotalFiles { get; private set; }
    public int ParsedFiles { get; private set; }
    public int Units => byUnit.Count;
    public bool Ready => pending.Count == 0 && TotalFiles > 0;

    /// <summary>
    ///  Points the table at a game file system and queues the definition files. Cheap: one directory
    ///  listing. Call again when the installation changes.
    /// </summary>
    public void Bind(IFileSystem fileSystem)
    {
        if (ReferenceEquals(fileSystem, bound) && TotalFiles > 0)
            return;

        byUnit.Clear();
        ParsedFiles = 0;
        TotalFiles = 0;
        pending = new();
        bound = fileSystem;

        IList<string> files;
        try { files = fileSystem.GetFiles("/def/world/"); }
        catch
        {
            bound = null;
            return;
        }

        // Parse the files that carry the families people actually ask about first: the base
        // `sign.sii`/`model.sii`/`curve_model.sii` hold cones, props and barrier chains, while the
        // alphabetical order would leave `sign.sii` for last and make the layer read "未解析" for the
        // first several rebuilds even though the data was always there.
        pending = files.Where(IsInteresting)
                       .OrderBy(IsPriorityFile).ThenBy(path => path, StringComparer.Ordinal)
                       .ToList();
        TotalFiles = pending.Count;
    }

    private static bool IsPriorityFile(string path)
    {
        string name = Path.GetFileName(path);
        return !(name.Equals("sign.sii", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("model.sii", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("curve_model.sii", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInteresting(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        return name.EndsWith(".sii") && InterestingFileNameParts.Any(part => name.Contains(part));
    }

    /// <summary>
    ///  Parses up to <paramref name="filesPerCall"/> queued definition files. Returns the number
    ///  parsed, so a caller that needs the table before it does anything can keep calling until 0.
    /// </summary>
    public int Step(int filesPerCall)
    {
        if (bound == null || pending.Count == 0)
            return 0;

        int done = 0;
        while (done < filesPerCall && pending.Count > 0)
        {
            string path = pending[0];
            pending.RemoveAt(0);
            done++;
            ParsedFiles++;

            try
            {
                SiiFile sii = SiiFile.Open(path, bound, true);
                foreach (Unit unit in sii.Units)
                {
                    IDictionary attributes = (IDictionary)unit.Attributes;
                    string? desc = Attribute(attributes, "model_desc") ?? Attribute(attributes, "prefab_desc");
                    if (string.IsNullOrEmpty(desc))
                        continue;

                    byUnit[unit.Name ?? ""] = new Entry(desc!, ParseFloat(Attribute(attributes, "fixed_step")));
                }
            }
            catch
            {
                // One unreadable definition file must not cost the whole table; the items it defined
                // simply stay unclassified and are reported as such.
            }
        }

        return done;
    }

    private static string? Attribute(IDictionary attributes, string key)
        => attributes.Contains(key) ? Convert.ToString(attributes[key]) : null;

    private static float ParseFloat(string? value)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : 0f;

    /// <summary>The class prefixes a map item type can use for its unit code, most likely first.</summary>
    private static readonly Dictionary<string, string[]> Prefixes = new(StringComparer.Ordinal)
    {
        ["Sign"] = new[] { "sign" },
        ["Model"] = new[] { "model" },
        ["Curve"] = new[] { "curve" },
        ["Buildings"] = new[] { "bld_model", "bld_scheme" },
        ["Prefab"] = new[] { "prefab" },
        ["AnimatedModel"] = new[] { "animated_model", "model" },
    };

    /// <summary>
    ///  Resolves one item to a family label. Returns false when the table cannot name it, which is the
    ///  answer the caller has to act on: unknown items get drawn, never trusted to block.
    /// </summary>
    public bool TryDescribe(string itemType, string token, out string label, out string desc, out bool hard)
    {
        label = UnknownLabel;
        desc = "";
        hard = false;

        if (token.Length == 0 || !Prefixes.TryGetValue(itemType, out string[]? prefixes))
            return false;

        foreach (string prefix in prefixes)
        {
            if (!byUnit.TryGetValue($"{prefix}.{token}", out Entry? entry) || entry.Desc.Length == 0)
                continue;

            desc = entry.Desc;
            for (int i = 0; i < Families.Length; i++)
            {
                if (!desc.Contains(Families[i].Marker, StringComparison.OrdinalIgnoreCase))
                    continue;
                label = Families[i].Label;
                hard = Families[i].Hard;
                return true;
            }
            return false;
        }

        return false;
    }

    /// <summary>Meters between instances of a curve model, from its definition; 0 when there is none.</summary>
    public float CurveStep(string token)
    {
        if (token.Length == 0)
            return 0f;
        return byUnit.TryGetValue($"curve.{token}", out Entry? entry) ? entry.Step : 0f;
    }

    public string Status
    {
        get
        {
            if (TotalFiles == 0)
                return "身份表未绑定";
            return Ready ? $"身份表 {Units} 单位" : $"身份表 {ParsedFiles}/{TotalFiles} 文件";
        }
    }
}
