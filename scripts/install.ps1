<#  podliner Windows installer (per-user)
    Usage examples:
      # Stable latest (not RC/beta):
      pwsh -NoLogo -NoProfile -c "irm https://github.com/timkicker/podliner/releases/latest/download/install.ps1 | iex"

      # Specific version (e.g. RC):
      $tag = 'v0.0.1-rc10'
      irm https://github.com/timkicker/podliner/releases/download/$tag/install.ps1 | iex

      # Uninstall / Prune
      # powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
      # powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall -Prune
#>

param(
  [string]$Version = "",
  [switch]$Uninstall = $false,
  [switch]$Prune = $false
)

$ErrorActionPreference = "Stop"

function Get-LatestVersion {
  try {
    $resp = Invoke-RestMethod -UseBasicParsing -Uri "https://api.github.com/repos/timkicker/podliner/releases/latest"
    # tag_name like v0.0.1
    return ($resp.tag_name -replace '^v','')
  } catch {
    throw "Could not determine latest version from GitHub API."
  }
}

function Ensure-Dir([string]$path) {
  if (-not (Test-Path -LiteralPath $path)) {
    New-Item -ItemType Directory -Path $path | Out-Null
  }
}

$LocalApp = $env:LOCALAPPDATA
if (-not $LocalApp) { throw "LOCALAPPDATA not set." }

$Root    = Join-Path $LocalApp "podliner"
$BinDir  = Join-Path $Root "bin"                         # optional user bin (unused by default)
$BaseOpt = $Root                                         # store versions beneath here
$WinApps = Join-Path $LocalApp "Microsoft\WindowsApps"   # usually in PATH
$Shim    = Join-Path $WinApps "podliner.cmd"

# Uninstall flow
if ($Uninstall) {
  Write-Host "Uninstalling podliner (per-user)..." -ForegroundColor Cyan
  if (Test-Path -LiteralPath $Shim) {
    Remove-Item -LiteralPath $Shim -Force -ErrorAction SilentlyContinue
  }
  if ($Prune -and (Test-Path -LiteralPath $BaseOpt)) {
    Write-Host "Removing all installed versions under $BaseOpt"
    Remove-Item -LiteralPath $BaseOpt -Recurse -Force
  } else {
    Write-Host "Keeping installed versions under $BaseOpt (use -Prune to remove)."
  }
  Write-Host "Done."
  exit 0
}

# Resolve version
if ([string]::IsNullOrWhiteSpace($Version)) {
  $Version = Get-LatestVersion  # Note: latest = stable only (RC/beta are not 'latest')
}
$Tag = "v$Version"

$Rid = "win-x64"  # single RID for now
$AssetName = "podliner-$Rid.zip"
$BaseUrl = "https://github.com/timkicker/podliner/releases/download/$Tag"

# Destination
$Dest  = Join-Path $BaseOpt $Version
$Stage = Join-Path $env:TEMP ("podliner_" + [Guid]::NewGuid().ToString("N"))
Ensure-Dir $Stage
Ensure-Dir $BaseOpt

try {
  Write-Host "Installing podliner $Version for $Rid -> $Dest" -ForegroundColor Cyan

  # Download archive + checksums
  $zipPath = Join-Path $Stage $AssetName
  $sumPath = Join-Path $Stage "SHA256SUMS"

  Invoke-WebRequest -UseBasicParsing -Uri "$BaseUrl/$AssetName" -OutFile $zipPath
  Invoke-WebRequest -UseBasicParsing -Uri "$BaseUrl/SHA256SUMS" -OutFile $sumPath

  if (-not (Test-Path -LiteralPath $zipPath)) { throw "Download failed: $AssetName not found." }
  if (-not (Test-Path -LiteralPath $sumPath)) { throw "Download failed: SHA256SUMS not found." }

  # Verify SHA256 (robust for PS 5.1 and 7+)
  $expected = (Get-Content -LiteralPath $sumPath |
    Where-Object { $_ -match ('\b' + [regex]::Escape($AssetName) + '$') } |
    Select-Object -First 1 |
    ForEach-Object { ($_ -split '\s+')[0] })

  if (-not $expected) { throw "No checksum entry for $AssetName in SHA256SUMS." }

  $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $expected.ToLowerInvariant()) {
    throw "Checksum mismatch for $AssetName. expected=$expected actual=$actual"
  }
  Write-Host "Checksum OK." -ForegroundColor Green

  # Unpack
  if (Test-Path -LiteralPath $Dest) {
    Write-Host "Removing existing version folder: $Dest"
    Remove-Item -LiteralPath $Dest -Recurse -Force
  }
  Ensure-Dir $Dest

  # Use built-in unzip
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $Dest)

  # Expected layout: $Dest\podliner\podliner.exe
  $Exe = Join-Path $Dest "podliner\podliner.exe"
  if (-not (Test-Path -LiteralPath $Exe)) {
    throw "Unexpected archive layout. Not found: $Exe"
  }

  # Create shim in WindowsApps
  Ensure-Dir $WinApps
  $shimContent = "@echo off`r`n""$Exe"" %*`r`n"
  Set-Content -LiteralPath $Shim -Value $shimContent -Encoding ASCII
  Write-Host "Installed shim: $Shim" -ForegroundColor Green

  # PATH info (usually WindowsApps is already in PATH)
  $pathParts = ($env:PATH -split ';') | Where-Object { $_ -and $_.Trim() -ne "" }
  $inPath = $pathParts | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ -eq $WinApps.ToLowerInvariant() } | Measure-Object | Select-Object -ExpandProperty Count
  if ($inPath -eq 0) {
    Write-Warning "$WinApps is not in PATH. Add it or call the full path to podliner.exe."
  }

  Write-Host "Done. Try:  podliner --version" -ForegroundColor Cyan
}
finally {
  if (Test-Path -LiteralPath $Stage) {
    Remove-Item -LiteralPath $Stage -Recurse -Force -ErrorAction SilentlyContinue
  }
}

# Optional prune after install
if ($Prune -and (Test-Path -LiteralPath $BaseOpt)) {
  Write-Host "Pruning old versions..." -ForegroundColor DarkCyan
  # Determine current target (exe we just installed)
  $current = $Exe
  Get-ChildItem -LiteralPath $BaseOpt -Directory | ForEach-Object {
    $vdir = $_.FullName
    $candidate = Join-Path $vdir "podliner\podliner.exe"
    if ($candidate -and (Test-Path -LiteralPath $candidate) -and ($candidate -ne $current)) {
      Write-Host "Removing $vdir"
      Remove-Item -LiteralPath $vdir -Recurse -Force -ErrorAction SilentlyContinue
    }
  }
}
