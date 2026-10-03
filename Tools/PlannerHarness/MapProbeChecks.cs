using System.Numerics;
using AutoParking;

/// <summary>
///  Checks for <see cref="MapItemProbe"/> - the read-only inventory that answers "what is actually
///  in this 120 m around the truck" before any of it is allowed to influence a route.
///  The ordering and truncation rules live in the probe rather than in the renderer precisely so
///  they can be asserted here: in game they are printed into a 560 px window and into the log, and
///  a nondeterministic top-list there would make two runs at the same spot unreadable.
/// </summary>
internal static class MapProbeChecks
{
    public static int Run()
    {
        int failures = 0;

        // 1) Ordering must not depend on insertion order, and a repeated UID must count once.
        MapItemProbe ascending = new() { Center = new Vector2(0, 0), RadiusM = 120 };
        ascending.Add(1, "Sign", "sign_stop", false, new Vector2(30, 0));
        ascending.Add(2, "Model", "street_lamp_01", true, new Vector2(10, 0));
        ascending.Add(3, "Buildings", "warehouse_30x12", true, new Vector2(8, 4));
        ascending.Add(4, "Buildings", "warehouse_30x12", true, new Vector2(20, 0));
        ascending.Add(4, "Buildings", "warehouse_30x12", true, new Vector2(4, 0));   // same UID, second node
        ascending.Add(5, "Model", "container_20", false, new Vector2(0, 60));

        MapItemProbe descending = new() { Center = new Vector2(0, 0), RadiusM = 120 };
        descending.Add(4, "Buildings", "warehouse_30x12", true, new Vector2(20, 0));
        descending.Add(5, "Model", "container_20", false, new Vector2(0, 60));
        descending.Add(3, "Buildings", "warehouse_30x12", true, new Vector2(8, 4));
        descending.Add(2, "Model", "street_lamp_01", true, new Vector2(10, 0));
        descending.Add(1, "Sign", "sign_stop", false, new Vector2(30, 0));
        descending.Add(4, "Buildings", "warehouse_30x12", true, new Vector2(4, 0));

        // Six reports, five distinct UIDs (4 is reported from both ends of the same polyline),
        // four distinct nodes' items in three types.
        bool unique = ascending.TotalItems == 5 && descending.TotalItems == 5;
        Report(ref failures, unique, "UID 去重", $"共 {ascending.TotalItems} 个（判据 5：同一 UID 只算一次）");

        bool stableOrder = ascending.Summary == descending.Summary;
        Report(ref failures, stableOrder, "排序与插入顺序无关", stableOrder
            ? "两次插入顺序不同、清单一致"
            : $"升序「{ascending.Summary}」≠ 降序「{descending.Summary}」");

        // 2) Per-type tallies: count, collision split, nearest distance. One tally per item type.
        MapItemProbe.TypeTally? buildings = ascending.TallyOf("Buildings");
        bool buildingCount = buildings?.Count == 2;
        Report(ref failures, buildingCount, "同类型合成一条", $"Buildings 计 {buildings?.Count} 段（判据 2）");

        MapItemProbe.TypeTally model = ascending.TallyOf("Model")!;
        bool collisionSplit = model.Count == 2 && model.Collidable == 1;
        Report(ref failures, collisionSplit, "可碰撞分开计数",
            $"Model {model.Count} 个 / 其中可碰 {model.Collidable}（判据 2 / 1）");

        bool nearest = Math.Abs(model.NearestM - 10.0) < 1e-6;
        Report(ref failures, nearest, "最近距离取最小", $"Model 最近 {model.NearestM:0.##} m（判据 10）");

        // 3) Token histogram: exact distinct count, truncated display, deterministic tie-break.
        MapItemProbe crowded = new() { Center = new Vector2(0, 0), RadiusM = 120 };
        ulong uid = 100;
        for (int i = 0; i < 9; i++) crowded.Add(++uid, "Model", "lamp_a", true, new Vector2(i, 0));
        for (int i = 0; i < 7; i++) crowded.Add(++uid, "Model", "lamp_b", true, new Vector2(0, i));
        for (int i = 0; i < 7; i++) crowded.Add(++uid, "Model", "lamp_c", false, new Vector2(i, i));
        for (int i = 0; i < 1; i++) crowded.Add(++uid, "Model", "lamp_d", false, new Vector2(50, i));

        MapItemProbe.TypeTally crowdedModel = crowded.TallyOf("Model")!;
        bool distinctExact = crowdedModel.DistinctTokens == 4;
        Report(ref failures, distinctExact, "去重后的模型种类数",
            $"{crowdedModel.DistinctTokens} 种（判据 4，显示截断不影响计数）");

        List<string> shown = crowdedModel.TopTokens(MapItemProbe.MaxTokensPerType).Select(t => t.Name).ToList();
        bool tieBroken = shown.SequenceEqual(new[] { "lamp_a", "lamp_b", "lamp_c" });
        Report(ref failures, tieBroken, "同数量按名字定序",
            $"显示 [{string.Join(", ", shown)}]（判据 lamp_a, lamp_b, lamp_c）");
        Report(ref failures, !shown.Contains("lamp_d"), "低频种类被截断",
            $"lamp_d 不在显示列表：{!shown.Contains("lamp_d")}");

        // 4) Rendering budget: the map window and the log both have a height limit.
        MapItemProbe many = new() { Center = new Vector2(0, 0), RadiusM = 120 };
        ulong next = 1000;
        for (int type = 0; type < 20; type++)
        {
            for (int i = 0; i < type + 1; i++)
                many.Add(++next, "type_" + type.ToString("00"), "some_purposefully_long_model_token_name", true,
                         new Vector2(type * 3f, i));
        }

        List<string> lines = many.Lines(6).ToList();
        bool bounded = lines.Count == 7;   // 6 types + 1 folded remainder
        Report(ref failures, bounded, "超出上限的类型折成一行",
            $"{lines.Count} 行（判据 7：6 类 + 1 行「其他」）");
        int widest = lines.Max(line => line.Length);
        Report(ref failures, widest <= 110, "单行宽度受控",
            $"最长 {widest} 字符（判据 ≤ 110，窗口只有 560 px）");
        Report(ref failures, lines[^1].StartsWith("其他"), "末行是折叠摘要", $"末行：{lines[^1]}");

        // 5) Empty and unknown-token cases: the probe must degrade into readable text, not throw.
        MapItemProbe empty = new() { Center = new Vector2(0, 0), RadiusM = 120 };
        Report(ref failures, empty.TotalItems == 0 && empty.Lines(6).Count() == 1 && empty.Summary.Length > 0,
               "空探针可读", $"「{empty.Summary}」/「{empty.Lines(6).First()}」");

        MapItemProbe nameless = new() { Center = new Vector2(0, 0), RadiusM = 120 };
        nameless.Add(1, "Hinge", null, false, new Vector2(3, 0));
        nameless.Add(2, "Hinge", null, false, new Vector2(4, 0));
        MapItemProbe.TypeTally hinges = nameless.TallyOf("Hinge")!;
        Report(ref failures, hinges.Count == 2, "无名条目归入占位名",
            $"Hinge {hinges.Count} 个，token 显示「{string.Join(",", hinges.TopTokens(3).Select(t => t.Name))}」");

        MapItemProbe repeated = new() { Center = new Vector2(0, 0), RadiusM = 120 };
        for (int i = 0; i < 20; i++) repeated.Add(7, "Road", "road_2l", false, new Vector2(i, 0));
        Report(ref failures, repeated.TotalItems == 1, "同一 item 多次到达只计一次",
            $"Road {repeated.TotalItems}（判据 1）");

        // 6) Compound children: a crate stack reached through one compound has to be counted as the
        // items it actually holds, and the inventory has to say how many came from inside one -
        // otherwise "185 items" reads as an empty depot next to a yard full of boxes.
        MapItemProbe nested = new() { Center = new Vector2(0, 0), RadiusM = 120 };
        nested.Add(1, "Compound", "crate_stack", true, new Vector2(12, 0));
        nested.Add(2, "Model", "crate_1", true, new Vector2(12.4f, 0), nested: true);
        nested.Add(3, "Model", "crate_1", true, new Vector2(12.8f, 0), nested: true);
        nested.Add(4, "Model", "crate_2", false, new Vector2(13f, 0), nested: true);

        bool nestedSplit = nested.TallyOf("Model")!.Count == 3 && nested.TallyOf("Model")!.Nested == 3
                           && nested.TallyOf("Compound")!.Nested == 0;
        Report(ref failures, nestedSplit, "compound 内的道具单独计数",
            $"Model {nested.TallyOf("Model")!.Count} 个 / 内含 {nested.TallyOf("Model")!.Nested} · " +
            $"Compound 内含 {nested.TallyOf("Compound")!.Nested}（判据 3 / 3 / 0）");

        bool nestedShown = nested.Lines(6).Any(line => line.Contains("内含 3"));
        Report(ref failures, nestedShown, "明细行显示内含数", $"出现「内含 3」：{nestedShown}");

        return failures;
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        if (!passed) failures++;
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
    }
}
