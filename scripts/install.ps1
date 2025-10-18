<#  podliner Windows installer (per-user)
    Usage examples:
      pwsh -NoLogo -NoProfile -c "irm https://github.com/timkicker/podliner/releases/latest/download/install.ps1 | iex"
      (optional) ... -Version 0.0.1-rc1
      (optional) ... -Uninstall
      (optional) ... -Prune
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
  if (-not (Test-Path $path)) { New-Item -ItemType Directory -Path $path | Out-Null }
}

$LocalApp = $env:LOCALAPPDATA
if (-not $LocalApp) { throw "LOCALAPPDATA not set." }

$Root    = Join-Path $LocalApp "podliner"
$BinDir  = Join-Path $Root "bin"               # optional user bin (unused by default)
$BaseOpt = $Root                                # store versions beneath here
$WinApps = Join-Path $LocalApp "Microsoft\WindowsApps"  # usually in PATH
$Shim    = Join-Path $WinApps "podliner.cmd"

# Uninstall flow
if ($Uninstall) {
  Write-Host "Uninstalling podliner (per-user)..." -ForegroundColor Cyan
  if (Test-Path $Shim) { Remove-Item $Shim -Force -ErrorAction SilentlyContinue }
  if ($Prune -and (Test-Path $BaseOpt)) {
    Write-Host "Removing all installed versions under $BaseOpt"
    Remove-Item -Recurse -Force $BaseOpt
  } else {
    Write-Host "Keeping installed versions under $BaseOpt (use -Prune to remove)."
  }
  Write-Host "Done."
  exit 0
}

# Resolve version
if ([string]::IsNullOrWhiteSpace($Version)) {
  $Version = Get-LatestVersion
}
$Tag = "v$Version"

$Rid = "win-x64"  # single RID for now
$AssetName = "podliner-$Rid.zip"
$BaseUrl = "https://github.com/timkicker/podliner/releases/download/$Tag"

# Destination
$Dest = Join-Path $BaseOpt $Version
$Stage = Join-Path $env:TEMP ("podliner_" + [Guid]::NewGuid().ToString("N"))
Ensure-Dir $Stage
Ensure-Dir $BaseOpt

try {
  Write-Host "Installing podliner $Version for $Rid → $Dest" -ForegroundColor Cyan

  # Download archive + checksums
  $zipPath = Join-Path $Stage $AssetName
  $sumPath = Join-Path $Stage "SHA256SUMS"

  Invoke-WebRequest -UseBasicParsing -Uri "$BaseUrl/$AssetName" -OutFile $zipPath
  Invoke-WebRequest -UseBasicParsing -Uri "$BaseUrl/SHA256SUMS" -OutFile $sumPath

  # Verify SHA256
  $expected = (Select-String -Path $sumPath -Pattern [regex]::Escape($AssetName)).ToString().Split()[0]
  if (-not $expected) { throw "No checksum entry for $AssetName in SHA256SUMS." }
  $actual = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $expected.ToLowerInvariant()) {
    throw "Checksum mismatch for $AssetName. expected=$expected actual=$actual"
  }
  Write-Host "Checksum OK." -ForegroundColor Green

  # Unpack
  if (Test-Path $Dest) {
    Write-Host "Removing existing version folder: $Dest"
    Remove-Item -Recurse -Force $Dest
  }
  Ensure-Dir $Dest

  # Use built-in unzip
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $Dest)

  # Expected layout: $Dest\podliner\podliner.exe
  $Exe = Join-Path $Dest "podliner\podliner.exe"
  if (-not (Test-Path $Exe)) {
    throw "Unexpected archive layout. Not found: $Exe"
  }

  # Create shim in WindowsApps
  Ensure-Dir $WinApps
  $shimContent = "@echo off`r`n""$Exe"" %*`r`n"
  Set-Content -Path $Shim -Value $shimContent -Encoding ASCII
  Write-Host "Installed shim: $Shim" -ForegroundColor Green

  # PATH info (usually WindowsApps is already in PATH)
  $inPath = ($env:PATH -split ';') -contains $WinApps
  if (-not $inPath) {
    Write-Warning "$WinApps is not in PATH. Add it or call the full path to podliner.exe."
  }

  Write-Host "Done. Try:  podliner --version" -ForegroundColor Cyan
}
finally {
  if (Test-Path $Stage) { Remove-Item -Recurse -Force $Stage -ErrorAction SilentlyContinue }
}

# Optional prune after install
if ($Prune -and (Test-Path $BaseOpt)) {
  Write-Host "Pruning old versions..." -ForegroundColor DarkCyan
  # Determine current target (exe we just installed)
  $current = $Exe
  Get-ChildItem -Directory $BaseOpt | ForEach-Object {
    $vdir = $_.FullName
    $candidate = Join-Path $vdir "podliner\podliner.exe"
    if ($candidate -and (Test-Path $candidate) -and ($candidate -ne $current)) {
      Write-Host "Removing $vdir"
      Remove-Item -Recurse -Force $vdir -ErrorAction SilentlyContinue
    }
  }
}

