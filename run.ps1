$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$project = Join-Path $projectRoot 'src\EnemyIntel.App\EnemyIntel.App.csproj'
dotnet run --project $project
