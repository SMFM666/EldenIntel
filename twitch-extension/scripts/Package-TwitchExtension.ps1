param(
    [string]$Version = "0.0.1"
)

$ErrorActionPreference = "Stop"
$extensionRoot = Split-Path -Parent $PSScriptRoot
$webRoot = Join-Path $extensionRoot "wwwroot"
$distRoot = Join-Path $extensionRoot "dist"
$stageRoot = Join-Path $distRoot "stage-$Version"
$zipPath = Join-Path $distRoot "EldenIntel-Interact-$Version.zip"

$requiredFiles = @(
    "viewer.html",
    "config.html",
    "live-config.html",
    "overlay.css",
    "overlay.js",
    "live-control.js",
    "relay-config.js",
    "privacy.html",
    "terms.html"
)
$packagedFiles = @($requiredFiles) + "mobile.html"

New-Item -ItemType Directory -Force -Path $distRoot | Out-Null
if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stageRoot | Out-Null

foreach ($file in $requiredFiles) {
    $source = Join-Path $webRoot $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Required extension asset is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $stageRoot $file)
}

# Twitch's dedicated Mobile Path is mobile.html. Keep it byte-for-byte aligned
# with the responsive fullscreen viewer so web and mobile cannot drift apart.
Copy-Item -LiteralPath (Join-Path $webRoot "viewer.html") -Destination (Join-Path $stageRoot "mobile.html")

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $stageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal
Remove-Item -LiteralPath $stageRoot -Recurse -Force

$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $actual = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') } | ForEach-Object FullName)
    $missing = @($packagedFiles | Where-Object { $_ -notin $actual })
    if ($missing.Count -gt 0) {
        throw "Package validation failed. Missing: $($missing -join ', ')"
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Created $zipPath"
Write-Host "Included $($packagedFiles.Count) validated files."
