using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace AutoParking;

/// <summary>
///  The sidecar identity channel: what the runtime items the memory layer already sees actually ARE,
///  fetched from a child process instead of from this one.
///
///  Why a child process at all: <c>KdopReader</c> gets every object's geometry out of the game cheaply,
///  but a runtime <c>kdop_item_t</c> carries no name — the identity strings (<c>academy.parked.truck</c>,
///  <c>state park_trigger_on_stopped</c>) only appear in the bytes its own fields point at, and
///  collecting them takes the 0.3–0.6 s a full <c>Tools\MemoryProbe --deep-tsv</c> walk needs (design doc
///  §38/§41, Task 4) — measured end to end at 0.88 s of wall clock for a 90 m window on 2026-10-20, once
///  the child's own process start is included. That is 25–50 ticks of a 30 Hz follow loop, so the scan
///  runs out of process, its machine-readable stdout is cached here, and <see cref="TryGet"/> hands the
///  resolver a snapshot in microseconds.
///
///  What that costs, and the rule it buys: <b>TryGet never waits on a process.</b> A cold cache says
///  "warming up" and kicks a background refresh; a stale cache serves the previous result and refreshes
///  behind it. Identity is an ENHANCEMENT (spec §4.3) — the marker-following path is supposed to work on
///  geometry alone — so a window with no identity data is a degraded result, not a failure, and nothing
///  here may stall, throw into, or abort the caller's thread.
///
///  Deployment reality for this feature branch: <c>MemoryProbe.exe</c> is not copied next to the plugin
///  until Task 9's csproj target exists, so in game this channel currently runs on its
///  "exe not found" path and the resolver stays on geometry. That is the intended sequence, not a bug
///  this file hides.
///
///  Hard rules:
///    - no host, TruckLib or SCS types, no NuGet: this file is compiled into the plugin AND into
///      <c>Tools\PlannerHarness</c>, so it is BCL only and <see cref="ParseTsv"/> is pure.
///    - one probe per cache window at a time, with a TTL-length back-off after a failed one, so a tick
///      calling this 30 times a second cannot spawn 30 children.
///    - <see cref="Dispose"/> kills the in-flight child instead of leaving an orphan holding a handle on
///      the game.
///
///  Consumer note, learned from the capture rather than guessed: the STR channel is a MULTISET (the same
///  <c>(uid, run)</c> pair can arrive twice) and pointer-window runs carry their provenance inside the
///  text (<c>@+0x78 state park_trigger_on_stopped</c>), so <see cref="ItemIdentity.Strings"/> is
///  substring-matched. Any test against a whole string would silently find nothing.
/// </summary>
internal sealed class IdentityChannel : IDisposable
{
    /// <summary>
    ///  One runtime item plus the identity text the sidecar found for it. <paramref name="Pos"/> is the
    ///  k-DOP centre on the plane the plugin plans on — (SCS X, SCS Z), the same convention as
    ///  <c>KdopReader.MemItem.PlanePos</c> — so the third coordinate the record prints (the height) is
    ///  parsed and dropped. <paramref name="Dx"/>/<paramref name="Dz"/> are the k-DOP extents along the
    ///  same two axes. <paramref name="Strings"/> is every STR run for that uid joined with "; ", empty
    ///  when the probe found no identity for the item.
    /// </summary>
    internal sealed record ItemIdentity(ulong Uid, int Type, uint Flags, Vector2 Pos,
                                        float Dx, float Dz, string Strings);

    /// <summary>A result younger than this is served straight from the cache. Five seconds is the
    ///  balance the walk itself sets: a probe costs 0.3–0.6 s, a parking maneuver takes tens of
    ///  seconds, and the identity of an item sitting still does not change underneath us.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    /// <summary>How long a child gets to answer before it is killed. Measured end to end on 2026-10-20,
    ///  <c>eurotrucks2 --deep-tsv 2194 1804 90</c> (395 ITEM + 820 STR rows) cost 0.88 s of wall clock,
    ///  .NET process start included, and the walk itself is the 0.3-0.6 s the comment above is about —
    ///  so 3 s is already 3.4× the slowest real sample and past that the child is hung, not slow.</summary>
    private const int ProcessTimeoutMs = 3000;

    private readonly string _exePath;
    private readonly string _gameProcessName;

    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, CacheEntry> _cache = new();
    private readonly HashSet<CacheKey> _refreshing = new();
    private readonly List<Process> _children = new();

    private bool _disposed;
    private bool _exeExists;
    private DateTime _exeCheckedUtc = DateTime.MinValue;

    /// <param name="exePath">Path to <c>MemoryProbe.exe</c>. A path that does not exist is NOT an error
    ///  here: the constructor stores it and <see cref="TryGet"/> reports "exe not found", because the
    ///  channel has to be constructible before Task 9 deploys the probe.</param>
    /// <param name="gameProcessName">The running game's process name (<c>eurotrucks2</c> /
    ///  <c>amtrucks</c>), passed straight to the probe. Empty means "let the probe use its own
    ///  default", so no empty argument is ever sent.</param>
    public IdentityChannel(string exePath, string gameProcessName)
    {
        _exePath = exePath ?? string.Empty;
        _gameProcessName = gameProcessName ?? string.Empty;
    }

    /// <summary>
    ///  Read the cached identity of the items inside <paramref name="radiusM"/> metres of
    ///  <paramref name="center"/>. Returns true with items whenever a result — fresh or stale — can be
    ///  served; false with an <paramref name="error"/> describing why the channel cannot answer yet.
    ///  Whatever it returns, it returns without touching a process on this thread.
    /// </summary>
    public bool TryGet(Vector2 center, double radiusM, out IReadOnlyList<ItemIdentity>? items, out string? error)
    {
        items = null;
        error = null;
        CacheKey key = CacheKey.Of(center, radiusM);

        lock (_gate)
        {
            if (_disposed)
            {
                error = "identity channel disposed";
                return false;
            }

            if (!ExeAvailableLocked())
            {
                // The one case the channel cannot even warm up in: no sidecar to ask.
                error = $"exe not found: {_exePath}";
                return false;
            }

            _cache.TryGetValue(key, out CacheEntry? entry);

            // --- Branch 1: fresh cache → hand back real data, launch nothing.
            if (entry is { Items: { Count: > 0 } fresh } && DateTime.UtcNow - entry.FetchedUtc < CacheTtl)
            {
                items = fresh;
                return true;
            }

            // --- Branch 2: we have data, but it is older than the TTL → serve the OLD data anyway and
            // refresh in the background. This is the branch that keeps a tick honest: the alternative,
            // waiting for the probe, is the measured 0.88 s stall landing on the thread that claims the
            // controls. The caller is not told anything is wrong, because the geometry it receives is
            // real — at worst one bay behind, and the resolver treats identity as an enhancement anyway.
            if (entry is { Items: { Count: > 0 } stale })
            {
                items = stale;
                KickRefreshLocked(key);
                return true;
            }

            // --- Branch 3: no cached result at all (cold window, or every probe for it has failed) →
            // report "warming up" / the last failure, kick a refresh, and let the next tick get the real
            // thing. Degraded, not fatal: the caller falls back to geometry-only resolution.
            error = entry?.Error ?? "warming up";
            KickRefreshLocked(key);
            return false;
        }
    }

    /// <summary>
    ///  The probe's record format → items, with identity text merged in. Pure, so the wire format is
    ///  what <c>Tools\PlannerHarness</c> can assert without a game process:
    ///    ITEM\t&lt;type:int&gt;\t0x&lt;uid&gt;\t0x&lt;flags&gt;\t&lt;x&gt;\t&lt;y&gt;\t&lt;z&gt;\t&lt;dx&gt;\t&lt;dy&gt;\t&lt;dz&gt;
    ///    STR\t0x&lt;uid&gt;\t&lt;ascii-run&gt;
    ///  Lines end with CRLF, because that is what <c>Console.Out.NewLine</c> writes on Windows and
    ///  <c>ReadToEndAsync</c> hands the bytes over untouched, so the per-line <c>'\r'</c> trim is owned
    ///  here rather than assumed away. Measured, not assumed: the numeric fields survive an untrimmed
    ///  <c>'\r'</c> anyway, since <see cref="NumberStyles.Float"/> and
    ///  <see cref="NumberStyles.HexNumber"/> both allow trailing whitespace — what it does corrupt is the
    ///  LAST field of a STR line, an identity run, which would be stored into
    ///  <see cref="ItemIdentity.Strings"/> with a stray carriage return inside it. The harness pins that
    ///  (a mutation that drops this line turns its ✗, so the trim is not decorative).
    ///  Anything that does not parse is skipped rather than thrown: a half-written stdout is a degraded
    ///  window, not a crash. An STR whose uid has no ITEM row is kept in the aggregation table and
    ///  dropped from the result, since an identity with no geometry cannot be placed.
    /// </summary>
    public static List<ItemIdentity> ParseTsv(string text)
    {
        List<ItemIdentity> items = new();
        if (string.IsNullOrEmpty(text))
            return items;

        CultureInfo inv = CultureInfo.InvariantCulture;
        List<(ulong Uid, int Type, uint Flags, float X, float Z, float Dx, float Dz)> rows = new();
        Dictionary<ulong, List<string>> runsByUid = new();

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0)
                continue;

            string[] field = line.Split('\t');
            if (field[0] == "ITEM")
            {
                if (field.Length != 10
                    || !int.TryParse(field[1], NumberStyles.Integer, inv, out int type)
                    || !TryHex(field[2], out ulong uid)
                    || !TryHex(field[3], out uint flags)
                    || !TryNum(field[4], inv, out float x)
                    || !TryNum(field[5], inv, out _)      // height: parsed so a broken row is dropped
                    || !TryNum(field[6], inv, out float z) // ... but the resolver plans on a plane
                    || !TryNum(field[7], inv, out float dx)
                    || !TryNum(field[8], inv, out _)
                    || !TryNum(field[9], inv, out float dz))
                    continue;

                rows.Add((uid, type, flags, x, z, dx, dz));
            }
            else if (field[0] == "STR" && field.Length >= 3 && TryHex(field[1], out ulong uid))
            {
                // Rejoin rather than truncate if a future probe ever emits a tab inside a run (the
                // current format cannot: runs are 0x20-0x7E and tab is 0x09).
                string run = string.Join("\t", field, 2, field.Length - 2);
                if (!runsByUid.TryGetValue(uid, out List<string>? runs))
                    runsByUid[uid] = runs = new List<string>();
                runs.Add(run); // multiset: a repeated (uid, run) stays repeated, matching is Contains
            }
        }

        foreach ((ulong uid, int type, uint flags, float x, float z, float dx, float dz) in rows)
        {
            string strings = runsByUid.TryGetValue(uid, out List<string>? runs)
                ? string.Join("; ", runs)
                : string.Empty;
            items.Add(new ItemIdentity(uid, type, flags, new Vector2(x, z), dx, dz, strings));
        }

        return items;
    }

    public void Dispose()
    {
        List<Process> inFlight;
        lock (_gate)
        {
            _disposed = true;
            inFlight = new List<Process>(_children);
            _children.Clear();
        }

        // Disposing a Process does NOT terminate the child, so without this an in-flight walk would
        // keep running as an orphan: it holds a handle on the game, burns 0.3-0.6 s of scan nobody
        // asked for, and then writes into a cache whose owner is gone. Kill it — and killing it closes
        // its stdout, which is exactly how the refresh thread's bounded read finishes early instead of
        // sitting out its 3 s timeout. Nothing here waits for that thread.
        foreach (Process child in inFlight)
            TryKill(child);
    }

    /// <summary>Launches nothing and waits on nothing: it only decides, under the lock, whether a
    ///  background probe is allowed for this window right now. Called by branches 2 and 3.</summary>
    private void KickRefreshLocked(CacheKey key)
    {
        if (_disposed)
            return;

        if (!_refreshing.Add(key))
            return; // one probe per window: a second would read the same memory twice for one tick

        CacheEntry entry = GetOrAddLocked(key);
        if (entry.AttemptedUtc != DateTime.MinValue && DateTime.UtcNow - entry.AttemptedUtc < CacheTtl)
        {
            // A probe for this window just failed (or is only seconds old): back off for a TTL instead
            // of re-launching on every tick, which at 30 ticks a second would be a process storm on the
            // machine that is also trying to run the game.
            _refreshing.Remove(key);
            return;
        }

        entry.AttemptedUtc = DateTime.UtcNow;
        Task.Run(() => Refresh(key)); // the caller's thread stops here
    }

    /// <summary>Background half: probe → parse → publish. Runs on a threadpool thread, never throws out
    ///  of itself, and leaves the previous cached result in place when it fails.</summary>
    private void Refresh(CacheKey key)
    {
        List<ItemIdentity>? items = null;
        string? failure = null;
        try
        {
            items = Probe(key, out failure);
            if (items.Count == 0 && failure is null)
                failure = "deep-tsv parsed no ITEM records"; // degraded: an empty lot is a real answer, an empty probe is not
        }
        catch (Exception e)
        {
            items = null;
            failure = "probe failed: " + e.Message;
        }

        lock (_gate)
        {
            _refreshing.Remove(key);
            CacheEntry entry = GetOrAddLocked(key);
            if (items is { Count: > 0 })
            {
                entry.Items = items;
                entry.FetchedUtc = DateTime.UtcNow;
                entry.Error = null;
            }
            else
            {
                // Failed walk: the previous result is NOT cleared (branch 2 keeps serving it, aging),
                // and the reason is remembered so branch 3 can report it instead of "warming up".
                entry.Error = failure;
            }
        }
    }

    /// <summary>Run one <c>--deep-tsv</c> walk and parse it. Only ever reached from
    ///  <see cref="Refresh"/>, i.e. off the caller's thread.</summary>
    private List<ItemIdentity> Probe(CacheKey key, out string? error)
    {
        error = null;
        CultureInfo inv = CultureInfo.InvariantCulture;

        ProcessStartInfo startInfo = new()
        {
            FileName = _exePath,
            UseShellExecute = false,        // required for redirection, and the only way to avoid a shell
            RedirectStandardOutput = true,  // --deep-tsv puts records on stdout and NOTHING else (Task 4)
            RedirectStandardError = true,   // a probe that cannot find the game dies to stderr; an unread pipe would block it
            CreateNoWindow = true,          // no console window flashing over the game
            StandardOutputEncoding = Encoding.ASCII // the record format is 0x20-0x7E by construction
        };

        if (!string.IsNullOrEmpty(_gameProcessName))
            startInfo.ArgumentList.Add(_gameProcessName);
        startInfo.ArgumentList.Add("--deep-tsv");
        startInfo.ArgumentList.Add(key.CenterX.ToString("0.00", inv));
        startInfo.ArgumentList.Add(key.CenterZ.ToString("0.00", inv));
        startInfo.ArgumentList.Add((key.RadiusTenths / 10.0).ToString("0.0", inv));

        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            error = $"probe did not start: {_exePath}";
            return new List<ItemIdentity>();
        }

        lock (_gate)
        {
            if (_disposed)
            {
                TryKill(process);
                error = "channel disposed before the probe ran";
                return new List<ItemIdentity>();
            }

            _children.Add(process); // so Dispose can reach it; removed below whatever happens
        }

        try
        {
            Task<string> reading = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync(); // drained and discarded — narration is not the product

            // One wait covers the deadline and the end of the data: stdout reaches EOF when the child
            // exits and flushes, so there is no separate "did it finish" poll, and no deadlock risk
            // from reading after WaitForExit. This blocks FOR UP TO 3 s, on this thread only.
            if (!reading.Wait(ProcessTimeoutMs))
            {
                TryKill(process); // a hung walk must not outlive the window that asked for it
                error = $"probe timeout after {ProcessTimeoutMs} ms: {_exePath}";
                return new List<ItemIdentity>();
            }

            // If the stream closed, the walk is over: a child that died mid-write leaves the bytes up to
            // the cut, which ParseTsv reads as far as it can and reports as a short window. No exit-code
            // gate here on purpose — the probe's own exceptions go to stderr, and the records we asked
            // for are either complete enough to use or they are not.
            return ParseTsv(reading.Result);
        }
        finally
        {
            lock (_gate)
                _children.Remove(process);
        }
    }

    private bool ExeAvailableLocked()
    {
        // File.Exists is a syscall and TryGet is called from a tick, so the answer is re-read at most
        // once per TTL — which also means the probe deployed by Task 9 is picked up within 5 s without
        // restarting the plugin.
        DateTime now = DateTime.UtcNow;
        if (now - _exeCheckedUtc >= CacheTtl)
        {
            _exeCheckedUtc = now;
            _exeExists = File.Exists(_exePath);
        }

        return _exeExists;
    }

    private CacheEntry GetOrAddLocked(CacheKey key)
    {
        if (!_cache.TryGetValue(key, out CacheEntry? entry))
            _cache[key] = entry = new CacheEntry();
        return entry;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already exited, or gone: killing a child we do not own any more is never worth throwing
            // over, and the channel has nothing to clean up besides the process itself.
        }
    }

    private static bool TryHex(string text, out ulong value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryHex(string text, out uint value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryNum(string text, CultureInfo inv, out float value)
        => float.TryParse(text, NumberStyles.Float, inv, out value);

    /// <summary>Cache window key: the centre rounded to whole metres and the radius to tenths, so that
    ///  the centimetre-per-frame jitter of the truck while it approaches cannot miss the cache and start
    ///  a probe per frame. Rounding is also what the probe is asked for — at most 0.5 m off, on a
    ///  window measured in tens of metres.</summary>
    private readonly record struct CacheKey(long CenterX, long CenterZ, long RadiusTenths)
    {
        public static CacheKey Of(Vector2 center, double radiusM)
            => new((long)Math.Round(center.X), (long)Math.Round(center.Y),
                   (long)Math.Round(Math.Max(0.0, radiusM) * 10.0));
    }

    /// <summary>One window's cached answer. Items staying null is what makes TryGet's third branch say
    ///  "warming up" rather than hand back an empty list, which would read as "this lot has no identity
    ///  data" instead of "the sidecar has not answered yet".</summary>
    private sealed class CacheEntry
    {
        public List<ItemIdentity>? Items;
        public DateTime FetchedUtc;
        public DateTime AttemptedUtc = DateTime.MinValue;
        public string? Error;
    }
}
