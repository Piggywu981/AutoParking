# AGENTS.md — AutoParking

Working notes for anyone (human or agent) editing this plugin. Every rule below exists because
breaking it caused a real bug or a lost day; full provenance in
`docs\2026-09-30-autoparking-design.md` §17–§25.

## Commands

Run these from this repo root (the directory holding `AGENTS.md`), on Windows — Git Bash or
PowerShell. No absolute paths here: `..\..\` is the workspace that contains this repo under
`ThirdPartyPlugins\`, and it holds the ETS2LA host install and the host source as siblings of
`ThirdPartyPlugins\`. If you are an agent, resolve it once from the repo location you were given
rather than copying a path from a previous session. A bare clone of this repo is enough to build
and to run the offline check; only the deploy row needs a full ETS2LA workspace.

| Purpose | Command |
|---|---|
| Build | `dotnet build -c Release` → `bin\Release\AutoParking.dll` |
| Deploy | put `bin\Release\AutoParking.dll` into `..\..\ETS2LA-win-release-Portable\current\Plugins\` — that is the `current\` tree the `.csproj` HintPaths already read host DLLs from, so the deploy target is wherever this checkout's host install is. Then **restart ETS2LA**: plugins are shadow-copy loaded and a running host keeps the old DLL |
| Offline check | `dotnet run --project Tools/PlannerHarness -c Release` (planner self-test + closed-loop sim; pure math, no game and no host DLLs) |

No CI, no linter, no `dotnet test` project. The harness plus the in-game probes on the settings
page are the entire verification story.

The harness' process exit code is a gate: `0` means every assertion passed. **Never lower a
threshold in the harness to get a green run** — its bars are deliberately tighter than the
plugin's own tolerances, which is how it catches regressions the game would still accept.

## Repository boundaries

- **The plugin `.csproj` globs everything under the repo, so a stray `.cs` becomes part of the
  shipped DLL.** Developer-only code lives in `Tools\` — `Tools\**` is carved out by
  `DefaultItemExcludes` in the plugin csproj, and `Tools\PlannerHarness` (the offline harness)
  links the pure-math sources it needs instead of referencing the plugin. Put any new dev project
  under `Tools\` as well: the batch script `ThirdPartyPlugins\build_all.ps1` builds every csproj it
  finds one level down and copies the result into `current\Plugins\`, so a console app placed there
  would ship as a broken plugin.
- **No NuGet dependencies.** `NuGet.Config` clears every package source, so restore has nowhere to
  fetch from. Host assemblies are referenced by `HintPath` into the install tree with
  `Private=false` — the plugin only ever loads from beside the host.
- `..\..\SourceCode` (the ETS2LA host) is a **read-only reference**: upstream rejects
  agent changes to it. Read it to learn the contract; never fix a plugin problem there.

## Project policy (owner-set, not negotiable in code)

- **Only the hotkeys may end a maneuver.** No observation — telemetry gap, blocker, replan cap,
  unexpected state — may auto-pause or auto-abort. Degrade, keep driving, keep reporting instead.
  A "safety" check that stops the truck on its own is a requirements violation here, not a feature.
- **Tractor unit only.** Trailer handling is out of scope; keep `RefuseWithTrailer` default on.
- **Evidence before fixes.** Do not reason a fix into existence. Add a log line, an in-game probe,
  or a failing harness case, read the measurement, then change the code. Control work on a real
  vehicle model cannot be validated by reading — several plausible fixes here were wrong until a
  trace said so.
- **Update the design doc with the change**: a §17-style log entry (symptom, the tell that found
  it, the measurement that confirmed the fix), *including hypotheses that turned out false*.
- **Commits: English only, never Chinese** (mirrors `.trae\rules\git-commit-message.md`, which is
  gitignored, so the rule lives here too). Commit inside this repo only — the workspace root is not
  a git repo. **Do not push unless explicitly asked.**
- **`README.md` and `README.zh-CN.md` are mirrors.** Edit both in the same change; the English one
  is the source.

## Three host behaviors that have each cost a debugging session

- `aforward` and `abackward` are folded into a **single `acceleration` bucket that is
  weighted-averaged across every publishing channel**, then split by sign. Writing both fields
  turns a brake demand into half throttle; a low `ControlWeight` means another plugin's request
  wins. Send one signed value. And read `air=` before concluding a brake did nothing — the service
  brake is air-circuit based, so at 0 bar the pedal value is physically inert.
- **The averaging happens before the transport branch, the echoes happen after.** Where our pedals
  physically go depends on the host's `EnableModernOutputForPedals` (Experiments page): off = the
  legacy virtual-gamepad surface, on = written straight to memory, which also keeps working while
  the game is unfocused. So `user_brake` proving a command landed is a **legacy-transport**
  measurement, not a universal law — which is why the brake probe prints `transport=memory|legacy`.
  Gear and handbrake pulses always take the legacy path. See design doc §24.
- `truckFloat.user_*` is the virtual gamepad's echo: SDK-injected commands show up there as
  "player input". Never use `user_*` to decide whether a human is driving. Also, `ETS2LA.Logging`
  renders Spectre.Console markup, so a bare `[Tag]` inside a message is silently swallowed —
  write `[[Tag]]`.

## Definition of done

Build with no new errors or warnings (the project currently carries a handful of pre-existing
ones), harness verdicts unchanged from before the edit, both READMEs and the design doc
current, and every claim about real-truck behavior labeled as measured or not yet measured.
