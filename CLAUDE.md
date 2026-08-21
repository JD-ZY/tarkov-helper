# Tarkov Helper

A Windows desktop app (WPF, .NET 8) for Escape from Tarkov: quest tracking,
hideout/ammo lookup, and a live map overlay showing extracts, loot, and
quest objective markers relative to the player's actual in-raid position.

Repo: https://github.com/JD-ZY/tarkov-helper (public). Distributed as a
self-contained zip attached to GitHub Releases; installed copies
auto-update themselves (see "Self-update" below).

## Solution layout

- `src/TarkovHelper.Core` - all logic with no UI dependency: tarkov.dev API
  clients, quest/hideout/ammo repositories (fetch + local cache + local
  progress persistence), EFT log parsing, map coordinate transforms, the
  self-updater. This is the project with test coverage.
- `src/TarkovHelper.App` - WPF UI (MainWindow, MapWindow), screen capture /
  OCR for the item-lookup hotkey feature, global input hook.
- `tests/TarkovHelper.Core.Tests` - xUnit tests for Core only. Run with
  `dotnet test tests/TarkovHelper.Core.Tests/TarkovHelper.Core.Tests.csproj`.
- `tools/` - one-off standalone diagnostic console apps used while building
  screen-capture/OCR/icon-matching features (not part of the shipped app,
  not referenced by the solution's normal build/test flow).

## Data sources

- Primary: `api.tarkov.dev/graphql` (`TarkovDevClient`).
- Fallback: `json.tarkov.dev` static mirror (`JsonTarkovDevClient`), used
  when the GraphQL API is down (it has had real outages). Fetched data is
  cached to disk in `%LocalAppData%\TarkovHelper\*.json`; a `CacheSchemaVersion`
  bump in `QuestRepository` forces a refetch when a cached shape goes stale.

## PvE vs PvP/PvP Season

These are genuinely separate in-game characters with independent quest
pools and progress - not a filter on one dataset. tarkov.dev's API models
this as a two-value `GameMode` enum (`Regular`/`Pve`; permanent PvP and
PvP Season share `Regular` since tarkov.dev doesn't distinguish them).

- `GameLogWatcher` parses EFT's own `application.log` "Session mode: ..."
  line to detect which mode is currently live, and fires `GameModeChanged`.
- `QuestRepository` persists both the task cache AND local progress
  (active/completed quest IDs) in mode-scoped files (`tasks-cache-pve.json`
  vs `tasks-cache.json`, etc.) - Regular keeps the original unsuffixed
  names for backward compatibility with existing installs.
- `MainWindow` reloads quest data whenever `GameModeChanged` fires, so the
  quest list and the map's objective markers both reflect whichever mode
  is actually live.
- `ReplayHistory` (startup quest-progress recovery from past sessions) has
  to reset its internal mode-tracking after finishing, otherwise the live
  tailer's first "Session mode: ..." line can look like "no change" and
  silently fail to fire `GameModeChanged` - see the regression test
  `ReplayHistory_ThenLiveSessionModeLine_StillFiresGameModeChangedEvenIfSameModeAsLastReplayedSession`
  in `GameLogWatcherHistoryTests.cs` for the exact failure mode this guards.

## Self-update

`UpdateChecker` (Core) polls GitHub's releases API
(`repos/JD-ZY/tarkov-helper/releases/latest`, unauthenticated - repo is
public) on every app launch, comparing the running build's
`<Version>` (in `TarkovHelper.App.csproj`) against the release tag
(`vX.Y.Z`). If newer, it downloads the release's `TarkovHelper.zip` asset
in the background and `MainWindow` shows a banner with a "Restart to
update" button.

`SelfUpdater` (Core) does the actual swap: a running .exe can't overwrite
itself on Windows, so it writes a short PowerShell script to a temp file
that waits for the current process to exit, extracts the new zip over the
install directory (`-Force`, so it overwrites in place), relaunches the
exe, then deletes itself. User data lives in `%LocalAppData%\TarkovHelper`,
separate from the install directory, so it's never touched by an update.

**Releasing a new version to everyone who has it installed**: see
`RELEASING.md` for the exact steps - version bump, publish, zip, tag,
`gh release create`. The release asset MUST be named exactly
`TarkovHelper.zip` and the tag MUST be `vX.Y.Z` matching the csproj
version, or `UpdateChecker` won't recognize it.

## Build / test / publish

```
dotnet build TarkovHelper.sln
dotnet test tests/TarkovHelper.Core.Tests/TarkovHelper.Core.Tests.csproj
dotnet publish src/TarkovHelper.App/TarkovHelper.App.csproj -c Release -r win-x64 --self-contained true -o publish/TarkovHelper
```

Publish must be `--self-contained true` - a framework-dependent publish
produces a ".NET must be installed" error dialog on machines without the
.NET 8 runtime, which is most players' machines.

If `TarkovHelper.App.exe` is currently running, `dotnet publish` fails
with a file-lock error (MSB3026/MSB3027) since publish overwrites the
running exe's own files - kill the process first (check with
`tasklist //FI "IMAGENAME eq TarkovHelper.App.exe"`, kill with
`taskkill //PID <pid> //F`).

## GitHub CLI

`gh` is installed but not on PATH by default in a fresh shell here - use:
```
export PATH="$PATH:/c/Program Files/GitHub CLI"
```
Already authenticated as `JD-ZY`.
