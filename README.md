# EldenIntel

EldenIntel is a portable Windows application for Elden Ring enemy intelligence,
cinematic camera control, local stream interactions, and offline creator tools.

## Current capabilities

- Detects Elden Ring and follows the current lock-on target.
- Displays live enemy identity, HP, stance, portraits, equipment, and localized drops.
- Imports the maintained TSV/JSON source data into a local, indexed SQLite database.
- Provides freecam, camera-track, capture, spawner, clone, outfit, and offline
  practice controls through the bundled native bridge.
- Includes the local Twitch relay and hosted-extension frontend used by the
  broadcaster workflow.
- Includes selectable 32:9 UI fixes that target the active Elden Ring profile
  reported by Mod Engine 3 Manager.

EldenIntel is designed for offline/modded play. Do not use its memory-changing
features with Easy Anti-Cheat or online play enabled.

The generated local database is stored at:

`%LOCALAPPDATA%\EldenIntel\V1\enemy-intel.db`

It is rebuilt automatically when the source TSV/JSON timestamps or sizes change.

## Solution structure

- `src/EnemyIntel.Core` — stable models and interfaces.
- `src/EnemyIntel.Data` — SQLite schema, import pipeline, and queries.
- `src/EnemyIntel.Game.Legacy` — temporary adapter around the working V2 reader.
- `src/EnemyIntel.App` — WPF views and view models.
- `tests/EnemyIntel.Tests` — database/import verification.

## Mandatory capture invariant

Equipment and drop hover cards must always be visible in OBS Window Capture.
They must be rendered as opaque, stable elements inside the EldenIntel main
window. Do not implement these cards with WPF `ToolTip`, `Popup`, a secondary
window, transparency, or a forced redraw timer: those approaches can disappear
from OBS or visibly flash. Hover handlers must remain attached directly to the
equipment and drop item-row templates.

## Commands

```powershell
dotnet build EnemyIntel.slnx
dotnet test EnemyIntel.slnx
dotnet run --project src\EnemyIntel.App\EnemyIntel.App.csproj
```

Or run `run.ps1` from this folder.

## Portable release

The self-contained Windows x64 release is distributed as a ZIP. Extract the
entire archive, then run `EldenIntel.exe` from the extracted folder. The archive
includes the .NET runtime, native bridge, portraits, item icons, UI fixes,
database sources, and local Twitch relay; no separate .NET installation is
required.

Do not run the executable from inside the ZIP. Keep the `assets`, `native`, and
`TwitchRelay` folders beside `EldenIntel.exe`.

Clone summons additionally require the matching locally built Mod Engine
package. Elden Ring's regulation file is not redistributed in the public ZIP.

## License

EldenIntel is source-visible but not open source. Official compiled releases
may be run for personal, non-commercial use under [LICENSE.md](LICENSE.md).
