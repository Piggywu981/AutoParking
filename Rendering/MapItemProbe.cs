using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AutoParking;

/// <summary>
///  What is actually standing in the sampled ground plane, bucketed by map item type.
///
///  Deliberately read-only: this is the measurement step that has to come before any of it is
///  allowed to block a route. The ordering and the truncation live here rather than in the renderer
///  because the same numbers go into a 560 px ImGui window and into the log, and a top-list that
///  depends on dictionary insertion order would make two runs at the same spot unreadable - so the
///  rules are pinned and asserted offline in Tools\PlannerHarness.
/// </summary>
public sealed class MapItemProbe
{
    /// <summary>Token groups shown per type. The distinct count stays exact regardless.</summary>
    public const int MaxTokensPerType = 3;

    public const int MaxLineChars = 108;

    private const int MaxTokenChars = 24;
    private const int MaxFoldedNames = 5;
    private const string NamelessToken = "(无名)";

    public readonly record struct NamedCount(string Name, int Count);

    public sealed class TypeTally
    {
        public string ItemType = "";
        public int Count;

        /// <summary>
        ///  Subset with the map format's collision flag on. The hard/soft split the planner will
        ///  eventually use hangs off this, so it is tallied from the first measurement, not added later.
        /// </summary>
        public int Collidable;

        /// <summary>
        ///  Subset reached through a <c>Compound</c> rather than from the map's own node index. A
        ///  compound keeps its children in its own dictionaries, so a walk that stops at the compound
        ///  reports a stack of crates as one item - which is how an inventory can read "185 items"
        ///  around a depot that is visibly full of them.
        /// </summary>
        public int Nested;

        public double NearestM = double.PositiveInfinity;

        public int DistinctTokens => tokens.Count;

        private readonly Dictionary<string, int> tokens = new(StringComparer.Ordinal);

        public void Add(string token, bool collision, double distance, bool nested)
        {
            Count++;
            if (collision)
                Collidable++;
            if (nested)
                Nested++;
            if (distance < NearestM)
                NearestM = distance;

            tokens.TryGetValue(token, out int seen);
            tokens[token] = seen + 1;
        }

        /// <summary>Most frequent first; equal counts broken by name so two runs of the same spot agree.</summary>
        public IReadOnlyList<NamedCount> TopTokens(int max)
        {
            return tokens.OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                        .Take(max)
                        .Select(pair => new NamedCount(pair.Key, pair.Value))
                        .ToList();
        }
    }

    public Vector2 Center;
    public double RadiusM;
    public int NodesScanned;
    public DateTime BuiltUtc = DateTime.UtcNow;

    /// <summary>Unique map items counted, across all types.</summary>
    public int TotalItems { get; private set; }

    private readonly Dictionary<string, TypeTally> byType = new(StringComparer.Ordinal);
    private readonly HashSet<ulong> seen = new();

    /// <summary>
    ///  Records one map item. An item is reachable from more than one node - a polyline item owns a
    ///  red node and a green node, and a prefab owns several - so the UID is the identity here, not
    ///  the call. Returns false for a UID already recorded, which is the common case, not an error.
    /// </summary>
    public bool Add(ulong uid, string itemType, string? token, bool collision, Vector2 position, bool nested = false)
    {
        if (!seen.Add(uid))
            return false;

        if (!byType.TryGetValue(itemType, out TypeTally? tally))
        {
            tally = new TypeTally { ItemType = itemType };
            byType[itemType] = tally;
        }

        string name = string.IsNullOrEmpty(token) ? NamelessToken : token;
        tally.Add(name, collision, Geometry.Distance(Center, position), nested);
        TotalItems++;
        return true;
    }

    public TypeTally? TallyOf(string itemType)
    {
        return byType.TryGetValue(itemType, out TypeTally? tally) ? tally : null;
    }

    /// <summary>Most numerous first, ties by type name.</summary>
    public IReadOnlyList<TypeTally> Ordered()
    {
        return byType.Values.OrderByDescending(tally => tally.Count)
                          .ThenBy(tally => tally.ItemType, StringComparer.Ordinal)
                          .ToList();
    }

    public string Summary
    {
        get
        {
            IReadOnlyList<TypeTally> ordered = Ordered();
            if (ordered.Count == 0)
                return "窗口内没有地图条目";

            string head = string.Join(" · ", ordered.Take(5).Select(tally => $"{tally.ItemType} {tally.Count}"));
            string tail = ordered.Count > 5 ? $" 等 {ordered.Count} 类" : "";
            return $"{ordered.Count} 类 {TotalItems} 个：{head}{tail}";
        }
    }

    /// <summary>One line per type, most numerous first; anything past the limit folds into a last line.</summary>
    public IReadOnlyList<string> Lines(int maxTypes)
    {
        List<TypeTally> ordered = Ordered().ToList();
        if (ordered.Count == 0)
            return new[] { "窗口内没有地图条目（半径太小，或地图还没解析完）" };

        List<string> lines = new();
        foreach (TypeTally tally in ordered.Take(maxTypes))
            lines.Add(Format(tally));

        List<TypeTally> rest = ordered.Skip(maxTypes).ToList();
        if (rest.Count > 0)
        {
            string listed = string.Join(", ", rest.Take(MaxFoldedNames).Select(tally => $"{tally.ItemType} {tally.Count}"));
            string more = rest.Count > MaxFoldedNames ? $" +{rest.Count - MaxFoldedNames}" : "";
            lines.Add(Clamp($"其他 {rest.Count} 类：{listed}{more}"));
        }

        return lines;
    }

    private static string Format(TypeTally tally)
    {
        string head = $"{tally.ItemType} {tally.Count} · 可碰 {tally.Collidable} · 最近 {Nearest(tally.NearestM)}";
        if (tally.Nested > 0)
            head += $" · 内含 {tally.Nested}";
        IReadOnlyList<NamedCount> top = tally.TopTokens(MaxTokensPerType);
        string suffix = tally.DistinctTokens > top.Count ? $"（共 {tally.DistinctTokens} 种）" : "";

        // Long model tokens are the norm, so drop the least informative groups until the line fits.
        for (int keep = top.Count; keep > 0; keep--)
        {
            string listed = string.Join(", ", top.Take(keep).Select(FormatToken));
            string line = $"{head} · {listed}{suffix}";
            if (line.Length <= MaxLineChars)
                return line;
        }

        return Clamp(head + suffix);
    }

    private static string FormatToken(NamedCount token)
    {
        string name = token.Name.Length <= MaxTokenChars ? token.Name : token.Name[..MaxTokenChars] + "…";
        return $"{name}×{token.Count}";
    }

    private static string Nearest(double meters)
    {
        return double.IsPositiveInfinity(meters) ? "—" : $"{meters:0.0} m";
    }

    private static string Clamp(string line)
    {
        return line.Length <= MaxLineChars ? line : line[..(MaxLineChars - 1)] + "…";
    }
}
