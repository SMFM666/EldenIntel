# EldenIntel

EldenIntel is a portable Elden Ring enemy-intelligence and cinematic camera application.

## What works in this milestone

- Modern WPF/XAML shell with MVVM state management.
- Detects Elden Ring and uses the proven V2 lock-on reader through an isolated compatibility adapter.
- Displays live enemy identity, HP, stance, portrait, and resolved equipment.
- Imports the maintained TSV/JSON source data into a local, indexed SQLite database.
- Displays database-backed enemy names and drop rates.
- Shows connection and database diagnostics in the interface.
- Keeps UI, domain contracts, persistence, and game integration in separate projects.

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

The self-contained Windows release is generated in `release\EldenIntelV1-portable`.
It includes the .NET runtime, native freecam payload, portraits, item icons, and the
trimmed database required by the application.
