using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ETS2LA.Logging;

namespace AutoParking;

/// <summary>
///  Reads the running game's own map-item lists from outside the process and publishes an
///  immutable snapshot for the map layer.
///
///  Why this exists: the roadwork cones standing in the world are spawned at runtime and never
///  pass through sector parsing, so the host's MapData cannot contain them (design doc §38
///  addendum + §41). The game itself keeps them as Sign-class kdop_item_t objects hanging off
///  the base_ctrl_u controller singleton. Everything here was measured with Tools\MemoryProbe
///  against the live process; this class is that probe, productized.
///
///  Reading chain (ets2la_plugin-derived, re-verified for 1.61):
///    main module image → base_ctrl instance pattern → instance pointer (RIP-relative)
///    → member sweep for array_dyn_t&lt;kdop_item_t*&gt; lists (offsets are build-specific)
///    → kdop_item_t: vptr@00, item_type@0x0A, k-DOP min/max@0x0C..0x34, flags@0x34, uid@0x40.
///    → what an item *is* is not decided here: MemClassifier.Classify(type, flags, dx, dy, dz) is
///      pure so Tools\PlannerHarness can assert every kind rule without a game process. Only the
///      "over 20 m across is not worth publishing" size filter stayed here, and it is a publishing
///      rule, not an identity rule.
///
///  Hard rules this class follows:
///    - never runs on the tick thread or the render thread (§39/§40: a stalled tick is how the
///      game drops our control claim). All work happens on one background task.
///    - read-only: OpenProcess with VM_READ only, no injection, no writes.
///    - every failure degrades to a status string; the map keeps working without this layer.
/// </summary>
internal sealed class KdopReader
{
    /// <summary>One drawn object from the game's memory. PlanePos follows the plugin's map
    ///  convention: (SCS X, SCS Z) — the same plane CurrentPose.Position lives on.</summary>
    public sealed record MemItem(int Type, MemKind Kind, ulong Uid, Vector2 PlanePos,
                                 float Height, float Dx, float Dy, float Dz, uint Flags);

    /// <summary>
    ///  The player's own vehicle box, measured the way the game measures every vehicle: the local
    ///  axis-aligned box's extents (traffic.cpp:29-31 - width = |dx|, height = |dy|, length = |dz|).
    ///  This is the only source that gives the real body LENGTH; the telemetry publishes wheel centres
    ///  and no dimension at all. Zero when unavailable (design doc §52).
    /// </summary>
    public readonly record struct TruckBody(double LengthM, double WidthM, double HeightM)
    {
        public bool Available => LengthM > 0 && WidthM > 0 && HeightM > 0;
    }

    public sealed class Snapshot
    {
        public required IReadOnlyList<MemItem> Items;
        public required TruckBody Body;
        public required DateTime StampUtc;
        public required string Status;
        public required long ReadMilliseconds;
    }

    // The 1.61 base_ctrl instance pattern, extracted from the shipped ets2la_plugin.dll's .rdata
    // (the 1.60-era source pattern no longer matches). Deliberately loose, so every hit is decoded
    // and validated before use. The nearby_kdop_items pattern has been byte-stable across both.
    private const string BaseCtrlPattern = "48 8b ? ? ? ? ? 48 8b ? 48 8b ? 48 8b 80";
    private const string NearbyKdopPattern = "4c 8d 81 ? ? ? ? 0f 29 ? ? 48 8d 91";

    private const long SweepMaxOffset = 0x6000;
    private const int MaxCachedItems = 20000;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(2);
    private static readonly string[] GameProcessNames = { "amtrucks", "eurotrucks" };

    private readonly AutoParkingPlugin plugin;
    private readonly object gate = new();
    private Snapshot? snapshot;

    private CancellationTokenSource? cancellation;
    private Task? loop;

    // Resolved per game process. Everything below is touched only by the loop task.
    private Process? gameProcess;
    private IntPtr processHandle;
    private long imageBase;
    private int imageSize;
    private byte[]? image;
    private long instancePtrAddress;
    private ulong lastInstance;
    private int fullListOffset = -1;
    private int gameActorOffset = -1;
    private int nearbyListOffset = -1;
    private readonly Dictionary<ulong, MemItem> itemCache = new();

    public KdopReader(AutoParkingPlugin plugin)
    {
        this.plugin = plugin;
    }

    public void Start()
    {
        if (loop != null)
            return;

        cancellation = new CancellationTokenSource();
        loop = Task.Run(() => Run(cancellation!.Token));
    }

    public void Stop()
    {
        cancellation?.Cancel();
        try { loop?.Wait(1000); } catch { /* the loop exits on its own */ }
        loop = null;
        cancellation?.Dispose();
        cancellation = null;

        if (processHandle != IntPtr.Zero)
        {
            CloseHandle(processHandle);
            processHandle = IntPtr.Zero;
        }
        gameProcess = null;
        lock (gate) snapshot = null;
    }

    /// <summary>Latest snapshot, or null before the first successful read. Thread-safe.</summary>
    public Snapshot? Current
    {
        get { lock (gate) return snapshot; }
    }

    private void Run(CancellationToken token)
    {
        bool lastOk = false;
        while (!token.IsCancellationRequested)
        {
            bool enabled;
            try { enabled = plugin.Settings.ShowMemoryItems; }
            catch { enabled = false; }

            try
            {
                if (!enabled)
                {
                    if (lastOk)
                        Logger.Info("AutoParking: [[内存条目]] 关闭，读取循环待机");
                    lastOk = false;
                    Thread.Sleep(IdleInterval);
                    continue;
                }

                Snapshot fresh = Refresh();
                lock (gate) snapshot = fresh;

                if (fresh.Status.StartsWith("读到") != lastOk)
                {
                    Logger.Info($"AutoParking: [[内存条目]] {fresh.Status}");
                }
                lastOk = fresh.Status.StartsWith("读到");
            }
            catch (Exception e)
            {
                // Never let this loop take anything down; the map simply loses this layer.
                if (lastOk)
                    Logger.Info($"AutoParking: [[内存条目]] 读取失败: {e.Message}");
                lastOk = false;
                ResetProcess();
            }

            Thread.Sleep(enabled ? RefreshInterval : IdleInterval);
        }
    }

    private Snapshot Refresh()
    {
        long start = Stopwatch.GetTimestamp();

        if (!EnsureProcess())
        {
            lock (gate) snapshot = new Snapshot { Items = Array.Empty<MemItem>(), Body = default, StampUtc = DateTime.UtcNow, Status = "游戏进程未找到", ReadMilliseconds = 0 };
            return snapshot;
        }

        if (image == null)
        {
            ResolveOffsets();
            if (nearbyListOffset < 0 && fullListOffset < 0)
            {
                long ms = ElapsedMs(start);
                Snapshot fail = new() { Items = Array.Empty<MemItem>(), Body = default, StampUtc = DateTime.UtcNow, Status = "特征码未命中（游戏更新后需重找，见 AGENTS.md）", ReadMilliseconds = ms };
                lock (gate) snapshot = fail;
                return fail;
            }
        }

        // The instance pointer lives in game data, not in our cache: re-read it every refresh so a
        // game restart (new heap, new instance) is noticed within one cycle without re-scanning.
        ulong instance = ReadU64(processHandle, instancePtrAddress);
        if (instance != lastInstance)
        {
            itemCache.Clear();
            lastInstance = instance;
        }

        TruckBody body = instance != 0 ? ReadTruckBody(instance) : default;
        List<MemItem> items = new(256);
        if (instance != 0)
        {
            if (fullListOffset >= 0)
                Collect(items, instance, fullListOffset);
            if (nearbyListOffset >= 0 && nearbyListOffset != fullListOffset)
                Collect(items, instance, nearbyListOffset);
        }

        long ms2 = ElapsedMs(start);
        int cones = 0, boards = 0, movers = 0, buildings = 0, triggers = 0, parked = 0;
        foreach (MemItem item in items)
        {
            switch (item.Kind)
            {
                case MemKind.Cone: cones++; break;
                case MemKind.Board: boards++; break;
                case MemKind.Mover: movers++; break;
                case MemKind.Building: buildings++; break;
                // The two runtime kinds the marker feature is built on: the 0x22 goal volume that sits
                // on the 0xC17 pair (its own game-side state string reads park_trigger_on_stopped),
                // and the 0x2F hookups standing in the row around it. Counted apart from Other because
                // this string is the only in-game evidence that the new rules fire at all - neither
                // kind is otherwise tellable on the map, one being invisible and the other a gray
                // footprint that looks like every other parked box.
                case MemKind.Trigger: triggers++; break;
                case MemKind.ParkedVehicle: parked++; break;
            }
        }

        string status = instance == 0
            ? "游戏中无实例（主菜单？）"
            : $"读到 {items.Count} 项 · 锥桶 {cones} · 牌 {boards} · 建筑 {buildings} · 动态 {movers} "
              + $"· 触发 {triggers} · 停放 {parked} · {ms2:0} ms"
              + (body.Available ? $" · 车体 {body.LengthM:0.00}×{body.WidthM:0.00}×{body.HeightM:0.00} m" : " · 车体未读到");

        Snapshot fresh = new() { Items = items, Body = body, StampUtc = DateTime.UtcNow, Status = status, ReadMilliseconds = ms2 };
        lock (gate) snapshot = fresh;
        return fresh;
    }

    /// <summary>
    ///  base_ctrl + game_actor_offset -> game_actor_u, +0x18 -> game_physics_vehicle_u
    ///  (physics_vehicle_u : vehicle_shared_u), whose aabox_t sits at +0x18 as two float3 corners.
    ///  Anything implausible is reported as unavailable: this value overwrites the vehicle box the
    ///  planner sweeps, so a stale pointer must never be allowed to shrink or stretch it.
    /// </summary>
    private TruckBody ReadTruckBody(ulong instance)
    {
        if (gameActorOffset <= 0)
            return default;

        try
        {
            ulong actor = ReadU64(processHandle, (long)instance + gameActorOffset);
            if (actor == 0)
                return default;
            ulong truck = ReadU64(processHandle, (long)actor + 0x18);
            if (truck == 0)
                return default;

            byte[] box = ReadBytes(processHandle, (long)truck + 0x18, 0x18);
            double width = Math.Abs(BitConverter.ToSingle(box, 12) - BitConverter.ToSingle(box, 0));
            double height = Math.Abs(BitConverter.ToSingle(box, 16) - BitConverter.ToSingle(box, 4));
            double length = Math.Abs(BitConverter.ToSingle(box, 20) - BitConverter.ToSingle(box, 8));
            return VehicleGeometry.PlausibleBodyBox(length, width, height)
                ? new TruckBody(length, width, height)
                : default;
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    ///  The base_ctrl pattern ends in a mov r64, [r64+disp32] whose displacement is the game_actor
    ///  member. Walk the instruction run and return the last base-plus-disp32 displacement, or -1 if
    ///  the bytes stop looking like that sequence - the same decode Tools\MemoryProbe does.
    /// </summary>
    private static int DecodeLastBaseDisp32(byte[] image, int start, int maxLen)
    {
        int last = -1, pos = 0;
        while (pos + 3 <= maxLen)
        {
            if (image[start + pos] != 0x48 || image[start + pos + 1] != 0x8b)
                break;
            byte modrm = image[start + pos + 2];
            int len = (modrm & 0xC7) == 0x05 ? 7
                    : (modrm & 0xC0) == 0x80 ? 7 + ((modrm & 0x07) == 0x04 ? 1 : 0)
                    : (modrm & 0xC0) == 0x40 ? 4 + ((modrm & 0x07) == 0x04 ? 1 : 0)
                    : 3;
            if ((modrm & 0xC0) == 0x80 && pos + len <= maxLen + 4)
                last = BitConverter.ToInt32(image, start + pos + 3 + ((modrm & 0x07) == 0x04 ? 1 : 0));
            pos += len;
            if (pos == maxLen)
                break;
        }

        return last;
    }

    private void Collect(List<MemItem> into, ulong instance, int listOffset)
    {
        byte[] head = ReadBytes(processHandle, (long)instance + listOffset, 0x28);
        ulong value = BitConverter.ToUInt64(head, 0x08);
        ulong size = BitConverter.ToUInt64(head, 0x10);
        if (value == 0 || size == 0 || size > 200_000)
            return;

        byte[] pointers = ReadBytes(processHandle, (long)value, checked((int)(size * 8)));
        byte[] item = new byte[0x48];

        for (int i = 0; i < (int)size; i++)
        {
            ulong elem = BitConverter.ToUInt64(pointers, i * 8);
            if (elem == 0)
                continue;

            if (!itemCache.TryGetValue(elem, out MemItem? cached))
            {
                if (!ReadProcessMemory(processHandle, (long)elem, item, 0x48, out nint read) || read != 0x48)
                    continue;

                cached = ParseItem(item);
                if (cached == null)
                    continue;

                if (itemCache.Count < MaxCachedItems)
                    itemCache[elem] = cached;
            }

            into.Add(cached);
        }
    }

    private static MemItem? ParseItem(byte[] it)
    {
        int type = it[0x0A];
        float minx = BitConverter.ToSingle(it, 0x0C), miny = BitConverter.ToSingle(it, 0x10), minz = BitConverter.ToSingle(it, 0x14);
        float maxx = BitConverter.ToSingle(it, 0x20), maxy = BitConverter.ToSingle(it, 0x24), maxz = BitConverter.ToSingle(it, 0x28);
        float dx = maxx - minx, dy = maxy - miny, dz = maxz - minz;

        // Garbage filter first: a dangling pointer or wrong layout must not reach the map.
        if (!IsFinite(minx) || !IsFinite(miny) || !IsFinite(minz) || !IsFinite(maxx) || !IsFinite(maxy) || !IsFinite(maxz))
            return null;
        if (MathF.Abs(minx) > 100_000 || MathF.Abs(minz) > 100_000 || miny < -50 || miny > 500)
            return null;
        if (dx < 0 || dy < 0 || dz < 0)
            return null;
        if (MathF.Max(dx, MathF.Max(dy, dz)) > 20f)
            return null; // buildings/terrain boxes: the archive layer already draws those it knows

        ulong uid = BitConverter.ToUInt64(it, 0x40);
        uint flags = BitConverter.ToUInt32(it, 0x34);
        // Classification itself is MemClassifier's (pure, harness-asserted). The size filter above
        // stays here on purpose: it decides what this layer publishes at all, not what an item is.
        MemKind kind = MemClassifier.Classify(type, flags, dx, dy, dz);
        return new MemItem(type, kind, uid, new Vector2((minx + maxx) / 2, (minz + maxz) / 2),
                           (miny + maxy) / 2, dx, dy, dz, flags);
    }

    // ---- process + offset resolution ---------------------------------------------------------------

    private bool EnsureProcess()
    {
        if (processHandle != IntPtr.Zero && gameProcess != null)
        {
            try
            {
                gameProcess.Refresh();
                if (!gameProcess.HasExited)
                    return true;
            }
            catch { /* fall through to re-find */ }
            ResetProcess();
        }

        foreach (string name in GameProcessNames)
        {
            Process? candidate = Process.GetProcessesByName(name).FirstOrDefault();
            if (candidate == null)
                continue;

            ProcessModule? module = null;
            try { module = candidate.MainModule; } catch { }
            if (module == null)
                continue;

            IntPtr handle = OpenProcess(0x0410 /* PROCESS_QUERY_INFORMATION | PROCESS_VM_READ */, false, candidate.Id);
            if (handle == IntPtr.Zero)
                continue;

            gameProcess = candidate;
            processHandle = handle;
            imageBase = module.BaseAddress.ToInt64();
            imageSize = (int)module.ModuleMemorySize;
            image = null;          // force a re-scan for the new process
            lastInstance = 0;
            fullListOffset = -1;
            nearbyListOffset = -1;
            itemCache.Clear();
            return true;
        }

        return false;
    }

    private void ResetProcess()
    {
        if (processHandle != IntPtr.Zero)
            CloseHandle(processHandle);
        processHandle = IntPtr.Zero;
        gameProcess = null;
        image = null;
        lastInstance = 0;
        fullListOffset = -1;
        nearbyListOffset = -1;
        itemCache.Clear();
    }

    /// <summary>
    ///  One-time scan: module image → instance pointer → struct offsets. The sweep does not trust
    ///  any single offset: every array_dyn_t candidate inside base_ctrl_u is validated by sampling
    ///  its elements (vptr inside the game module + k-DOP floats that look like world coordinates),
    ///  and the biggest passing list is taken as the full item list. The pattern-resolved offset is
    ///  kept as a second list because the fresh runtime rows can live only there (design doc §41).
    /// </summary>
    private void ResolveOffsets()
    {
        image = ReadBytes(processHandle, imageBase, imageSize);

        nearbyListOffset = -1;
        long kdopInsn = FindPattern(image!, imageBase, NearbyKdopPattern);
        if (kdopInsn != 0)
            nearbyListOffset = BitConverter.ToInt32(image!, (int)(kdopInsn + 3 - imageBase));

        fullListOffset = -1;
        int bestSize = 0;
        foreach (long insn in FindAllPattern(image!, imageBase, BaseCtrlPattern))
        {
            byte modrm = image![(int)(insn + 2 - imageBase)];
            if ((modrm & 0xC7) != 0x05)
                continue; // first instruction must be mov r64, [rip+disp32]
            long ptrAddress = insn + 3 + BitConverter.ToInt32(image!, (int)(insn + 3 - imageBase)) + 4;
            instancePtrAddress = ptrAddress; // all validated hits agree; keep the last
        }

        if (instancePtrAddress == 0)
            return;

        for (long off = 0x00; off <= SweepMaxOffset; off += 8)
        {
            byte[] head;
            try { head = ReadBytes(processHandle, (long)lastInstanceUnchecked() + off, 0x28); }
            catch { break; }

            ulong value = BitConverter.ToUInt64(head, 0x08);
            ulong size = BitConverter.ToUInt64(head, 0x10);
            ulong capacity = BitConverter.ToUInt64(head, 0x18);
            if (value == 0 || size == 0 || size > 200_000 || capacity < size || capacity > 2_000_000)
                continue;

            int sample = (int)Math.Min(size, 32);
            byte[] pointers;
            try { pointers = ReadBytes(processHandle, (long)value, sample * 8); }
            catch { continue; }

            int pass = 0;
            for (int i = 0; i < sample; i++)
            {
                ulong elem = BitConverter.ToUInt64(pointers, i * 8);
                if (LooksLikeKdopItem(elem))
                    pass++;
            }

            if (sample > 0 && pass >= sample * 4 / 5 && (int)size > bestSize)
            {
                bestSize = (int)size;
                fullListOffset = (int)off;
            }
        }
    }

    private ulong lastInstanceUnchecked() => ReadU64(processHandle, instancePtrAddress);

    private bool LooksLikeKdopItem(ulong elem)
    {
        try
        {
            byte[] it = ReadBytes(processHandle, (long)elem, 0x38);
            ulong vptr = BitConverter.ToUInt64(it, 0);
            if (vptr < (ulong)imageBase || vptr >= (ulong)(imageBase + imageSize))
                return false;
            float minx = BitConverter.ToSingle(it, 0x0C), miny = BitConverter.ToSingle(it, 0x10), minz = BitConverter.ToSingle(it, 0x14);
            float maxx = BitConverter.ToSingle(it, 0x20), maxy = BitConverter.ToSingle(it, 0x24), maxz = BitConverter.ToSingle(it, 0x28);
            return IsFinite(minx) && IsFinite(miny) && IsFinite(minz) && IsFinite(maxx) && IsFinite(maxy) && IsFinite(maxz)
                && MathF.Abs(minx) < 100_000 && MathF.Abs(minz) < 100_000 && miny > -50 && miny < 500
                && maxx >= minx && maxy >= miny && maxz >= minz;
        }
        catch
        {
            return false;
        }
    }

    // ---- byte helpers ------------------------------------------------------------------------------

    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    private static long FindPattern(byte[] data, long baseAddress, string pattern)
    {
        string[] parts = pattern.Split(' ');
        byte?[] bytes = parts.Select(p => p.Contains('?') ? (byte?)null : Convert.ToByte(p, 16)).ToArray();
        byte first = bytes[0]!.Value;
        int max = data.Length - bytes.Length;
        int i = 0;
        while (i <= max)
        {
            if (data[i] != first)
            {
                int next = Array.IndexOf(data, first, i + 1);
                if (next < 0 || next > max)
                    return 0;
                i = next;
            }

            bool ok = true;
            for (int j = 1; j < bytes.Length; j++)
            {
                byte? expected = bytes[j];
                if (expected.HasValue && data[i + j] != expected.Value)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
                return baseAddress + i;
            i++;
        }

        return 0;
    }

    private static List<long> FindAllPattern(byte[] data, long baseAddress, string pattern)
    {
        var hits = new List<long>();
        string[] parts = pattern.Split(' ');
        byte?[] bytes = parts.Select(p => p.Contains('?') ? (byte?)null : Convert.ToByte(p, 16)).ToArray();
        byte first = bytes[0]!.Value;
        int max = data.Length - bytes.Length;
        int i = 0;
        while (i <= max)
        {
            if (data[i] != first)
            {
                int next = Array.IndexOf(data, first, i + 1);
                if (next < 0 || next > max)
                    break;
                i = next;
            }

            bool ok = true;
            for (int j = 1; j < bytes.Length; j++)
            {
                byte? expected = bytes[j];
                if (expected.HasValue && data[i + j] != expected.Value)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
                hits.Add(baseAddress + i);
            i++;
        }

        return hits;
    }

    private static long ElapsedMs(long startTimestamp) =>
        (Stopwatch.GetTimestamp() - startTimestamp) * 1000 / Stopwatch.Frequency;

    // ---- win32 -------------------------------------------------------------------------------------

    private static byte[] ReadBytes(IntPtr handle, long address, int size)
    {
        byte[] buffer = new byte[size];
        if (!ReadProcessMemory(handle, address, buffer, size, out nint read) || read != size)
            throw new Exception($"ReadProcessMemory 0x{address:X} got {read}/{size}");
        return buffer;
    }

    private static ulong ReadU64(IntPtr handle, long address)
    {
        byte[] b = new byte[8];
        if (!ReadProcessMemory(handle, address, b, 8, out nint read) || read != 8)
            return 0;
        return BitConverter.ToUInt64(b, 0);
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr handle, long address, byte[] buffer, nint size, out nint read);

    [DllImport("kernel32")]
    private static extern bool CloseHandle(IntPtr handle);
}
