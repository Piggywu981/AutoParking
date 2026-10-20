using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
///    - probe walks are bounded TWO ways, and the second one is what survives a moving truck:
///      <c>_refreshing</c> allows one walk per cache window (with a TTL-length back-off after a failed
///      one), which stops a 30 Hz tick from spawning 30 children FOR THE SAME WINDOW — but the cache key
///      flips whenever the centre crosses a whole metre, so while the truck follows a marker it keeps
///      landing on cold windows, and each of those would otherwise get its own child.
///      <c>MaxConcurrentProbes</c> caps walks across the WHOLE channel (see
///      <see cref="KickRefreshLocked"/>); that pair, not the per-window rule alone, is the guarantee this
///      class offers.
///    - the cache is bounded too, and that is a contract rather than an accident: windows untouched for
///      <c>CacheMaxAge</c> (8×TTL) are dropped, and the table is hard-capped at <c>MaxCacheEntries</c>
///      entries oldest-first, pruned at the one place where it can grow. See the <c>_cache</c>
///      declaration for the arithmetic and <see cref="PruneLocked"/> for the mechanism.
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

    /// <summary>The most probe walks this channel lets run at the same time, counted across every cache
    ///  window. Two is enough: the walk is ~0.9 s and a moving truck only needs the window it is in, so a
    ///  third child would be sweeping memory the caller has already driven away from while adding another
    ///  <c>ReadProcessMemory</c> load on the game and another threadpool thread parked in
    ///  <c>reading.Wait(3000)</c>. A window refused here is retried on the next tick — nothing about it is
    ///  stamped — so the cap throttles the rate, it never strands a window.</summary>
    private const int MaxConcurrentProbes = 2;

    /// <summary>A cache window untouched (never fetched, never attempted) for this long is dropped:
    ///  eight TTL. A window the truck is still working is asked about again every TTL, so eight of them
    ///  passing in silence means the maneuver has driven away — and the uids behind those records belong
    ///  to a runtime list that has moved on anyway. See <c>_cache</c>.</summary>
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromTicks(CacheTtl.Ticks * 8);

    /// <summary>Hard ceiling on <c>_cache</c> entries, for the case the age rule cannot see (one big
    ///  radius held for a whole session, or a caller that revisits far windows). 64 × the ~40 KB a
    ///  90 m walk parses to is ~2.5 MB, which is the most an enhancement channel may hold for itself.</summary>
    private const int MaxCacheEntries = 64;

    /// <summary>Ceiling on how long the no-records path waits for the child's stderr to finish draining.
    ///  Only reached after stdout hit EOF, which means the child exited and its stderr is already closed,
    ///  so this is a guard against a pathological pipe, not a cost paid in the normal case.</summary>
    private const int StderrTailWaitMs = 200;

    /// <summary>How much stderr is carried into the error string: enough to tell the causes apart, short
    ///  enough that one line of a foreign process' stack trace never buries the plugin log.</summary>
    private const int StderrTailChars = 200;

    private readonly string _exePath;
    private readonly string _gameProcessName;

    private readonly object _gate = new();
    /// <summary>One entry per visited rounded window. BOUNDED, not an append-only log: each entry holds
    ///  the parsed records of a whole probe walk — measured on the 2026-10-20 capture, 64 KB of stdout =
    ///  395 <see cref="ItemIdentity"/> plus their identity text, about 40 KB parsed per window — and the
    ///  key flips every ~0.5 m of truck travel, so an unpruned table accretes by the kilometre across a
    ///  session. <see cref="PruneLocked"/> runs wherever this dictionary can gain an entry
    ///  (<see cref="KickRefreshLocked"/>): age out past <c>CacheMaxAge</c>, then cap at
    ///  <c>MaxCacheEntries</c> oldest-first. The caller-visible consequence is only that an aged-out
    ///  window is cold again — <see cref="TryGet"/> branch 3, never a wrong answer.</summary>
    private readonly Dictionary<CacheKey, CacheEntry> _cache = new();
    /// <summary>Exactly the windows with a walk in flight: a key is added when the task is scheduled and
    ///  removed in <see cref="Refresh"/>'s lock, so <c>Count</c> is the live child count and both guards
    ///  in <see cref="KickRefreshLocked"/> read the same set.</summary>
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
    ///  The list is a read-only snapshot published once at <see cref="Refresh"/> (a
    ///  <see cref="System.Collections.ObjectModel.ReadOnlyCollection{T}"/> wrapper, not a copy), so a
    ///  caller cannot write into the cache — including through an <c>as List&lt;&gt;</c> in some future
    ///  helper, which is the reason it is not handed out as the live list it was parsed into.
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
            // Deliberate, and kept after review: from the second call onward this hands back the probe's
            // OWN failure text instead of the literal "warming up" the brief describes, because "process
            // 'eurotrucks2' not running" tells the live test something "warming up" hides. Task 8 must
            // therefore never string-match this value — it may log it verbatim and treat any return of
            // false as "no identity yet, use geometry"; which branch it came from is told by that false,
            // not by the wording.
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
    ///  dropped from the result, since an identity with no geometry cannot be placed. A non-finite float
    ///  counts as "does not parse" too — see <see cref="TryNum"/> — so the probe's own <c>NaN</c> token
    ///  drops its row rather than publishing geometry no distance test can pass or fail against.
    ///  One output record per ITEM ROW, not per uid: the probe's dedupe key is the element pointer
    ///  (<c>!seen.Add(elem)</c>), so a uid reached through two entries arrives twice — counted in the
    ///  2026-10-20 capture: 14 repeated <c>(uid, run)</c> STR pairs and 0 repeated ITEM uids, i.e. the
    ///  repeat is structural rather than observed in that particular window. Either way every row of one
    ///  uid carries the same aggregated run list, duplicates preserved in arrival order.
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
    ///  background probe is allowed right now — for this window (per-key guard and the TTL back-off) and
    ///  in general (<see cref="MaxConcurrentProbes"/>). Called by branches 2 and 3.</summary>
    private void KickRefreshLocked(CacheKey key)
    {
        if (_disposed)
            return;

        // Global in-flight cap, checked BEFORE anything is registered or stamped, because the per-window
        // guard below cannot bound this on its own: CacheKey.Of rounds the centre to whole metres, so a
        // truck at 5 m/s enters a NEW key every 1 m of travel = every ~0.2 s, while one walk runs the
        // measured ~0.9 s. Every one of those keys is cold, every cold key takes branch 3, and each would
        // otherwise get ITS OWN child: 0.9 / 0.2 = four to five concurrent ReadProcessMemory sweeps of the
        // same game process, and the same number of threadpool threads parked in reading.Wait(3000), inside
        // a host that is rendering ImGui and driving plugins on the rest. That ratio is arithmetic on two
        // measured numbers (the 0.88 s walk, the whole-metre key), not a count of children; and
        // marker-following means the truck IS moving while Acquiring, so this is the normal case, not a
        // corner. _refreshing holds exactly the walks in flight, so its Count IS that number — and this cap
        // is what holds it at two.
        if (_refreshing.Count >= MaxConcurrentProbes)
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

        // The cache only grows on the path that reaches here, so pruning here is exactly sufficient: no
        // timer thread, and it runs under the same lock as every reader. Placed after the stamp above on
        // purpose — the window being launched right now is then the freshest entry in the table, so it
        // cannot be evicted by its own scheduling.
        PruneLocked();

        try
        {
            Task.Run(() => Refresh(key)); // the caller's thread stops here
        }
        catch
        {
            // A launch failure would otherwise leave this key in _refreshing for good, consuming one
            // of the two global slots until Dispose: two such leaks brick the channel, and the
            // exception would land on the tick thread. Refresh is the only other place that removes
            // a key, and it can never run if the task never started (R30).
            _refreshing.Remove(key);
        }
    }

    /// <summary>Keeps <see cref="_cache"/> inside the bound its declaration states. Two cheap deterministic
    ///  rules, no timer and no LRU bookkeeping:
    ///    - drop every window neither fetched nor attempted within <see cref="CacheMaxAge"/> (8×TTL);
    ///    - if the table is still over <see cref="MaxCacheEntries"/>, evict oldest-first until it fits.
    /// Both read <see cref="LastTouchedUtc"/>, so an entry that has never been fetched or attempted counts
    /// as maximally old and cannot survive either rule. Cost is one pass over a table the size of the
    /// neighbourhood (bounded at 64), on the thread that was going to schedule a task anyway.
    /// A walk that finishes just after its entry was pruned re-adds its fresh result through
    /// <see cref="Refresh"/>'s <see cref="GetOrAddLocked"/>, which is the right answer; the only thing
    /// lost in that race is a failed walk's back-off stamp, for a window nobody has asked about in 40 s.
    /// </summary>
    private void PruneLocked()
    {
        DateTime now = DateTime.UtcNow;

        List<CacheKey>? expired = null;
        foreach (KeyValuePair<CacheKey, CacheEntry> pair in _cache)
        {
            if (now - LastTouchedUtc(pair.Value) >= CacheMaxAge)
            {
                expired ??= new List<CacheKey>();
                expired.Add(pair.Key);
            }
        }

        if (expired is not null)
            foreach (CacheKey key in expired)
                _cache.Remove(key);

        int over = _cache.Count - MaxCacheEntries;
        if (over <= 0)
            return;

        List<CacheKey> oldestFirst = new(_cache.Keys);
        // Ascending by last touch: the SMALLER DateTime is the OLDER window, so index 0 is the one to
        // drop. (Getting this backwards is not hypothetical — the inverted comparator was written first
        // and the offline experiment caught it by evicting the just-launched window instead of the
        // 30-second-old one, which is the opposite of what an oldest-first cap owes a moving truck.)
        oldestFirst.Sort((a, b) => LastTouchedUtc(_cache[a]).CompareTo(LastTouchedUtc(_cache[b])));
        for (int i = 0; i < over; i++)
            _cache.Remove(oldestFirst[i]);
    }

    /// <summary>How old an entry is allowed to get: whichever of the fetch and the launch attempt landed
    ///  last. DateTime.MinValue for both means "never touched", which the prune reads as ancient.</summary>
    private static DateTime LastTouchedUtc(CacheEntry entry)
        => entry.FetchedUtc > entry.AttemptedUtc ? entry.FetchedUtc : entry.AttemptedUtc;

    /// <summary>Background half: probe → parse → publish. Runs on a threadpool thread, never throws out
    ///  of itself, and leaves the previous cached result in place when it fails.</summary>
    private void Refresh(CacheKey key)
    {
        List<ItemIdentity>? items = null;
        string? failure = null;
        try
        {
            items = Probe(key, out failure);
            // Degraded, not fatal: an empty lot is a real answer, an empty probe is not. Probe names the
            // cause itself now (exit code plus the head of stderr) on every path that hands back an empty
            // list, so this line is only the safety net for a walk that answers nothing at all.
            if (items.Count == 0 && failure is null)
                failure = "deep-tsv parsed no ITEM records";
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
                // Published behind a read-only wrapper ONCE, here, so no caller can reach the List this
                // walk parsed into: TryGet hands the same object to every frame that asks, and one
                // Sort/Add/Remove through an `as List<>` would corrupt every later frame of a maneuver.
                entry.Items = new ReadOnlyCollection<ItemIdentity>(items);
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
            // Still drained, because an unread stderr pipe fills its 4 KB buffer and blocks the child
            // mid-walk — that part is unchanged. What is new is that the text is kept: when the walk
            // prints no records it is the only thing saying WHY, and this is the one path allowed to wait
            // for it (below), off the caller's thread and with a 200 ms ceiling.
            Task<string> narration = process.StandardError.ReadToEndAsync();

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
            // gate here on purpose — a probe that exited nonzero but printed usable records gave the answer
            // we asked for. When it printed NOTHING usable, though, the exit code and stderr are the
            // diagnosis, so they go into the error text instead of being thrown away.
            List<ItemIdentity> parsed = ParseTsv(reading.Result);
            if (parsed.Count == 0)
                error = NoRecordsMessage(narration, process);
            return parsed;
        }
        finally
        {
            lock (_gate)
                _children.Remove(process);
        }
    }

    /// <summary>Builds the text Task 8 logs and the live-test protocol reads when a walk yielded nothing.
    ///  Before this, "wrong game process name", "pattern scan found nothing", "not in a driving state" and
    ///  "empty lot" all collapsed into the single <c>deep-tsv parsed no ITEM records</c> that
    ///  <see cref="Refresh"/> invents for them, because stderr was redirected (it has to be) and then
    ///  discarded with the exit code. Both are already complete when this runs — stdout reached EOF, which
    ///  is this file's exit signal (see the read order in <see cref="Probe"/>), and the child closing its
    ///  handle closes stderr with it — so the wait is a formality bounded by
    ///  <see cref="StderrTailWaitMs"/>, and it is paid on the refresh thread, never the caller's.
    ///  The result is a description, never a control signal: see branch 3 of <see cref="TryGet"/>, whose
    ///  caller must not string-match it.</summary>
    private static string NoRecordsMessage(Task<string> stderr, Process process)
    {
        string text = string.Empty;
        try
        {
            if (stderr.Wait(StderrTailWaitMs))
                text = stderr.Result;
        }
        catch (Exception)
        {
            // The stderr pipe itself faulted; the exit code still separates "did not start" from "started
            // and found nothing", which is most of the diagnosis.
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length > StderrTailChars)
            text = text[..StderrTailChars] + " ...";

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (Exception)
        {
            exitCode = -1; // handle gone or never exited: better an unknown code than an exception here
        }

        return "deep-tsv printed no ITEM records (exit " + exitCode
               + (text.Length > 0 ? ", stderr: " + text : string.Empty) + ")";
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

    /// <summary>A number, or nothing at all — a field that will not parse OR is not finite skips its whole
    ///  row. The second half is what it looks like a detail: <see cref="NumberStyles.Float"/> accepts the
    ///  probe's OWN deliberate non-finite token, because <c>Tools\MemoryProbe\Program.cs</c>'s
    ///  <c>Num()</c> prints the literal <c>NaN</c> for any field that is not finite, and
    ///  <c>float.TryParse("NaN")</c> returns true with <c>float.NaN</c> (both verified, not assumed). A
    ///  record published with NaN position or extents would reach Task 8's distance math, where every
    ///  comparison against NaN is false — the item silently stops being an obstacle instead of being
    ///  dropped as the bad row it is. Skipping it here is the honest answer, and the harness pins it:
    ///  dropping this clause turns its ✗ (see the checks file's NaN case).</summary>
    private static bool TryNum(string text, CultureInfo inv, out float value)
        => float.TryParse(text, NumberStyles.Float, inv, out value) && float.IsFinite(value);

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
    ///  data" instead of "the sidecar has not answered yet". <c>Items</c> is the published
    ///  read-only wrapper, never the list the walk parsed — the type is what stops
    ///  <see cref="TryGet"/> from handing the mutable cache object to a frame.</summary>
    private sealed class CacheEntry
    {
        public IReadOnlyList<ItemIdentity>? Items;
        public DateTime FetchedUtc;
        public DateTime AttemptedUtc = DateTime.MinValue;
        public string? Error;
    }
}
