param(
    [Parameter(Mandatory = $true)]
    [string]$BaseRegulation,

    [Parameter(Mandatory = $true)]
    [string]$CustomRegulation,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$WitchyBnd = 'C:\Users\sethm\Downloads\WitchyBND-v3.0.0.1-win-x64\WitchyBND.exe'
)

$ErrorActionPreference = 'Stop'

function Invoke-Witchy([string]$Path, [ValidateSet('Unpack', 'Repack')][string]$Mode) {
    $modeSwitch = if ($Mode -eq 'Unpack') { '-u' } else { '-r' }
    & $WitchyBnd -s $modeSwitch $Path
    if ($LASTEXITCODE -ne 0) {
        throw "WitchyBND failed for $Path with exit code $LASTEXITCODE"
    }
}

function Import-ExactRow([string]$TargetXmlPath, [string]$SourceXmlPath, [string]$RowId) {
    $target = [System.Xml.XmlDocument]::new()
    $target.PreserveWhitespace = $true
    $target.Load($TargetXmlPath)

    $source = [System.Xml.XmlDocument]::new()
    $source.PreserveWhitespace = $true
    $source.Load($SourceXmlPath)

    $sourceRow = $source.SelectSingleNode("//row[@id='$RowId']")
    if ($null -eq $sourceRow) {
        throw "Row $RowId was not found in $SourceXmlPath"
    }

    $existing = $target.SelectSingleNode("//row[@id='$RowId']")
    if ($null -ne $existing) {
        $null = $existing.ParentNode.RemoveChild($existing)
    }

    $rows = $target.SelectSingleNode('//rows')
    if ($null -eq $rows) {
        throw "ROWS node was not found in $TargetXmlPath"
    }

    $null = $rows.AppendChild($target.ImportNode($sourceRow, $true))
    $target.Save($TargetXmlPath)
}

$base = (Resolve-Path -LiteralPath $BaseRegulation).Path
$custom = (Resolve-Path -LiteralPath $CustomRegulation).Path
$witchy = (Resolve-Path -LiteralPath $WitchyBnd).Path

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$workingRegulation = Join-Path $output 'regulation.bin'
Copy-Item -LiteralPath $base -Destination $workingRegulation -Force

Invoke-Witchy $workingRegulation 'Unpack'
$workingBinder = Join-Path $output 'regulation-bin'
$manifest = Join-Path $workingBinder '_witchy-bnd4.xml'
if (-not (Test-Path -LiteralPath $manifest)) {
    throw "WitchyBND did not unpack the base regulation at $workingBinder"
}

$customWork = Join-Path $output 'custom-source'
New-Item -ItemType Directory -Path $customWork -Force | Out-Null
$customCopy = Join-Path $customWork 'regulation.bin'
Copy-Item -LiteralPath $custom -Destination $customCopy -Force
Invoke-Witchy $customCopy 'Unpack'
$customBinder = Join-Path $customWork 'regulation-bin'

foreach ($paramName in @('NpcParam', 'NpcThinkParam')) {
    $targetXml = Join-Path $workingBinder "$paramName.param.xml"
    $sourceXml = Join-Path $customBinder "$paramName.param.xml"
    Import-ExactRow $targetXml $sourceXml '200000011'
    Invoke-Witchy $targetXml 'Repack'
}

Invoke-Witchy $workingBinder 'Repack'

if (-not (Test-Path -LiteralPath $workingRegulation)) {
    throw 'Repacked regulation.bin was not produced.'
}

Get-Item -LiteralPath $workingRegulation
Get-FileHash -LiteralPath $workingRegulation -Algorithm SHA256
