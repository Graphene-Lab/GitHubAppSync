<#
.SYNOPSIS
  Produces the two release assets an application needs to self-update with GitHubAppSync.

.DESCRIPTION
  Builds "<Channel>.zip" from a publish directory and "<Channel>-manifest.json" describing it,
  ready to attach to a GitHub Release. The manifest is what a client reads first; the zip is the
  payload it downloads only when the published version is newer than the one running.

  The zip stores its entries at the archive root (no wrapping folder), because the client extracts
  it and diffs the relative paths against the installation directory.

.PARAMETER PublishDir
  Directory holding the published application (the output of "dotnet publish").

.PARAMETER Channel
  Update channel. Selects the asset names and lets one release carry several builds
  (for example "portable", "win-x64", "linux-arm64").

.PARAMETER Version
  The version being published, as a dotted number, e.g. 1.26.09.15.

.PARAMETER OutDir
  Where to write the two assets. Created if missing.

.EXAMPLE
  pwsh -File tools/New-UpdateAsset.ps1 -PublishDir ./publish -Channel portable -Version 1.26.09.15 -OutDir ./dist
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $PublishDir,
    [Parameter(Mandatory = $true)][string] $Channel,
    [Parameter(Mandatory = $true)][string] $Version,
    [string] $OutDir = "."
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    throw "PublishDir not found: $PublishDir"
}

# A version the client cannot parse is a version it cannot compare, which would silently disable
# the anti-downgrade guard. Reject it here rather than ship it.
$parsed = $null
if (-not [System.Version]::TryParse(($Version -split '-')[0], [ref] $parsed)) {
    throw "Version '$Version' is not a dotted number (e.g. 1.26.09.15)."
}

$items = Get-ChildItem -LiteralPath $PublishDir -Force
if ($null -eq $items -or $items.Count -eq 0) {
    throw "PublishDir is empty: $PublishDir"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

$zipName = "$Channel.zip"
$zipPath = Join-Path $OutDir $zipName
$manifestPath = Join-Path $OutDir "$Channel-manifest.json"

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
# includeBaseDirectory = false so entries sit at the archive root.
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Resolve-Path -LiteralPath $PublishDir).Path,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

$digest = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

$manifest = [ordered]@{
    channel        = $Channel
    version        = $Version
    asset          = $zipName
    sha256         = $digest
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}

# Written without a BOM: the client reads this as UTF-8 JSON.
[System.IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 4),
    (New-Object System.Text.UTF8Encoding($false)))

Write-Host "asset     : $zipName ($([math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)) MB)"
Write-Host "manifest  : $manifestPath"
Write-Host "version   : $Version"
Write-Host "sha256    : $digest"
