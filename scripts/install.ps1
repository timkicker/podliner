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

# Ensure TLS 1.2 for GitHub on older Windows/PS
try {
  [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]::Tls12
} catch { }

function Get-LatestVersion {
  try {
    $headers = @{ 'User-Agent' = 'podliner-installer' }
    $resp = Invoke-RestMethod -UseBasicParsing -Headers $headers -Uri "https://api.github.com/repos/timkicker/podliner/releases/latest"
    # tag_name like v0.0.2
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
    Remove-Item -LiteralPath $BaseOpt -Recurse -Force -ErrorAction SilentlyContinue
  } else {
    Write-Host "Keeping installed versions under $BaseOpt (use -Prune to remove)."
  }
  Write-Host "Done."
  exit 0
}

# Resolve version
if ([string]::IsNullOrWhiteSpace($Version)) {
  $Version = Get-LatestVersion  # Note: latest = stable only (RC/beta are not 'latest')
} else {
  # allow passing tags with or without leading 'v'
  $Version = ($Version -replace '^v','')
}
$Tag = "v$Version"

$Rid = "win-x64"
$AssetName = "podliner-$Rid.zip"
$BaseUrl = "https://github.com/timkicker/podliner/releases/download/$Tag"

# Destination
$Dest  = Join-Path $BaseOpt $Version
$Stage = Join-Path $env:TEMP ("podliner_" + [Guid]::NewGuid().ToString("N"))
Ensure-Dir $Stage
Ensure-Dir $BaseOpt

# Helpers
function Find-PodlinerExe([string]$root) {
  # If there's exactly one subfolder and no files, flatten it for convenience
  $topFiles   = Get-ChildItem -LiteralPath $root -File -ErrorAction SilentlyContinue
  $topFolders = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue
  if (-not $topFiles -and $topFolders.Count -eq 1) {
    $inner = $topFolders[0].FullName
    Write-Host "Flattening inner folder: $inner"
    Get-ChildItem -LiteralPath $inner -Force | ForEach-Object {
      Move-Item -LiteralPath $_.FullName -Destination $root -Force
    }
    Remove-Item -LiteralPath $inner -Recurse -Force -ErrorAction SilentlyContinue
  }

  # Now search recursively for podliner.exe
  $exeItem = Get-ChildItem -LiteralPath $root -Recurse -File -Filter "podliner.exe" -ErrorAction SilentlyContinue |
             Select-Object -First 1
  return $exeItem
}

try {
  Write-Host "Installing podliner $Version for $Rid -> $Dest" -ForegroundColor Cyan

  # Download archive + checksums
  $zipPath = Join-Path $Stage $AssetName
  $sumPath = Join-Path $Stage "SHA256SUMS"

  Invoke-WebRequest -UseBasicParsing -Uri "$BaseUrl/$AssetName" -OutFile $zipPath
  Invoke-WebRequest -UseBasicParsing -Uri "$BaseUrl/SHA256SUMS" -OutFile $sumPath

  if (-not (Test-Path -LiteralPath $zipPath)) { throw "Download failed: $AssetName not found." }
  if (-not (Test-Path -LiteralPath $sumPath)) { throw "Download failed: SHA256SUMS not found." }

  # Verify SHA256
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

  # Unpack fresh
  if (Test-Path -LiteralPath $Dest) {
    Write-Host "Removing existing version folder: $Dest"
    Remove-Item -LiteralPath $Dest -Recurse -Force -ErrorAction SilentlyContinue
  }
  Ensure-Dir $Dest

  # Use built-in unzip
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $Dest)

  # Robustly locate the executable (supports flat ZIPs and one-folder ZIPs)
  $exeItem = Find-PodlinerExe -root $Dest
  if (-not $exeItem) {
    # Show a short tree to help debug unexpected archives
    $sample = (Get-ChildItem -LiteralPath $Dest -Recurse -ErrorAction SilentlyContinue |
               Select-Object -First 30 | ForEach-Object { $_.FullName }) -join "`n"
    throw "Unexpected archive layout. podliner.exe not found under $Dest.`nSample:`n$sample"
  }

  $Exe = $exeItem.FullName
  Write-Host "Found executable: $Exe" -ForegroundColor Green

  # Create shim in WindowsApps
  Ensure-Dir $WinApps
  $shimContent = "@echo off`r`n""$Exe"" %*`r`n"
  Set-Content -LiteralPath $Shim -Value $shimContent -Encoding ASCII
  Write-Host "Installed shim: $Shim" -ForegroundColor Green

  # PATH info (usually WindowsApps is already in PATH)
  $pathParts = ($env:PATH -split ';') | Where-Object { $_ -and $_.Trim() -ne "" }
  $inPath = $pathParts | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ -eq $WinApps.ToLowerInvariant() } | Measure-Object | Select-Object -ExpandProperty Count
  if ($inPath -eq 0) {
    Write-Warning "$WinApps is not in PATH. Add it or call the full path to podliner.cmd."
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
  if (-not (Get-Variable -Name Exe -Scope Script -ErrorAction SilentlyContinue) -or -not $Exe -or -not (Test-Path -LiteralPath $Exe)) {
    Write-Host "Skip prune: no current executable resolved." -ForegroundColor Yellow
  } else {
    Write-Host "Pruning old versions..." -ForegroundColor DarkCyan
    $current = $Exe
    Get-ChildItem -LiteralPath $BaseOpt -Directory -ErrorAction SilentlyContinue | ForEach-Object {
      $vdir = $_.FullName
      # Try to find any podliner.exe in that version directory
      $candidateItem = Get-ChildItem -LiteralPath $vdir -Recurse -File -Filter "podliner.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
      if ($candidateItem) {
        $candidate = $candidateItem.FullName
        if ($candidate -ne $current) {
          Write-Host "Removing $vdir"
          Remove-Item -LiteralPath $vdir -Recurse -Force -ErrorAction SilentlyContinue
        }
      }
    }
  }
}
