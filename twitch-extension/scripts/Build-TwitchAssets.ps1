param(
    [string]$BrandingRoot = "$PSScriptRoot\..\..\src\EnemyIntel.App\assets\Branding",
    [string]$OutputRoot = "$PSScriptRoot\..\review\assets"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
[System.IO.Directory]::CreateDirectory($OutputRoot) | Out-Null

function New-Canvas([int]$Width, [int]$Height) {
    New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
}

function Set-Quality([System.Drawing.Graphics]$Graphics) {
    $Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $Graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $Graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $Graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
}

$iconSource = [System.Drawing.Image]::FromFile((Join-Path $BrandingRoot 'EldenIntelIcon.png'))
try {
    $logo = New-Canvas 100 100
    $graphics = [System.Drawing.Graphics]::FromImage($logo)
    try {
        Set-Quality $graphics
        $graphics.Clear([System.Drawing.Color]::FromArgb(8, 9, 11))
        $graphics.DrawImage($iconSource, (New-Object System.Drawing.Rectangle(2, 2, 96, 96)))
    } finally { $graphics.Dispose() }
    $logo.Save((Join-Path $OutputRoot 'logo-100x100.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $logo.Dispose()

    $discovery = New-Canvas 300 200
    $graphics = [System.Drawing.Graphics]::FromImage($discovery)
    try {
        Set-Quality $graphics
        $bounds = New-Object System.Drawing.Rectangle(0, 0, 300, 200)
        $background = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bounds,
            [System.Drawing.Color]::FromArgb(5, 6, 8),
            [System.Drawing.Color]::FromArgb(61, 0, 25), 18)
        $graphics.FillRectangle($background, $bounds)
        $background.Dispose()
        $graphics.DrawImage($iconSource, (New-Object System.Drawing.Rectangle(18, 25, 150, 150)))
        $accent = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 60, 130))
        $graphics.FillRectangle($accent, 175, 31, 5, 138)
        $accent.Dispose()
        $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $muted = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(196, 198, 205))
        $title = New-Object System.Drawing.Font('Arial', 22, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $small = New-Object System.Drawing.Font('Arial', 10, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $graphics.DrawString("ELDEN`nINTEL", $title, $white, 189, 45)
        $graphics.DrawString('INTERACT', $small, $muted, 191, 118)
        $graphics.DrawString('VIEWER POWERED', $small, $muted, 191, 140)
        $title.Dispose(); $small.Dispose(); $white.Dispose(); $muted.Dispose()
    } finally { $graphics.Dispose() }
    $discovery.Save((Join-Path $OutputRoot 'discovery-300x200.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $discovery.Dispose()
} finally { $iconSource.Dispose() }

Get-ChildItem $OutputRoot -Filter '*.png' | Select-Object FullName, Length
