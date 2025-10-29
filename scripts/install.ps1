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

# --- HttpClient capability (PS 5.1 may not auto-load the assembly) ---
function Test-HttpClientAvailable {
  try { $null = [System.Net.Http.HttpClient]; return $true } catch {
    try {
      Add-Type -AssemblyName System.Net.Http
      $null = [System.Net.Http.HttpClient]
      return $true
    } catch { return $false }
  }
}

# Minimal HttpClient with progress helper (preferred path if available)
function Download-FileHttpClient {
  param(
    [Parameter(Mandatory)][string]$Uri,
    [Parameter(Mandatory)][string]$OutFile,
    [string]$Activity = "Downloading",
    [int]$Retries = 2
  )

  $attempt = 0
  do {
    $attempt++
    try {
      $h = New-Object System.Net.Http.HttpClient
      $h.Timeout = [TimeSpan]::FromMinutes(30)
      $h.DefaultRequestHeaders.UserAgent.ParseAdd("podliner-installer")
      $resp = $h.GetAsync($Uri, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
      if (-not $resp.IsSuccessStatusCode) { throw "HTTP $($resp.StatusCode) for $Uri" }

      $total = $resp.Content.Headers.ContentLength
      $inStream  = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
      $dir = [System.IO.Path]::GetDirectoryName($OutFile)
      if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
      $outStream = [System.IO.File]::Open($OutFile, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)

      try {
        $buffer = New-Object byte[] (1MB)
        $readTotal = 0L
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while (($read = $inStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
          $outStream.Write($buffer, 0, $read)
          $readTotal += $read
          if ($total) {
            $pct = [int](100 * $readTotal / $total)
            $speed = if ($sw.Elapsed.TotalSeconds -gt 0) { "{0:n1} MB/s" -f (($readTotal/1MB)/$sw.Elapsed.TotalSeconds) } else { "" }
            Write-Progress -Activity $Activity -Status "$pct%  $speed" -PercentComplete $pct
          } else {
            Write-Progress -Activity $Activity -Status ("{0:n1} MB" -f ($readTotal/1MB)) -PercentComplete -1
          }
        }
      }
      finally {
        $outStream.Dispose()
        $inStream.Dispose()
        $h.Dispose()
        Write-Progress -Activity $Activity -Completed
      }

      if (-not (Test-Path -LiteralPath $OutFile) -or ((Get-Item -LiteralPath $OutFile).Length -eq 0)) {
        throw "Empty file after download"
      }
      return  # success
    }
    catch {
      if ($attempt -le $Retries) {
        Write-Host "Download failed (attempt $attempt of $Retries): $($_.Exception.Message). Retrying..." -ForegroundColor Yellow
        Start-Sleep -Seconds ([math]::Min(5 * $attempt, 15))
      } else {
        throw
      }
    }
  } while ($true)
}

# Fallback using Invoke-WebRequest (PowerShell shows its own progress bar in interactive sessions)
function Download-FileIwr {
  param(
    [Parameter(Mandatory)][string]$Uri,
    [Parameter(Mandatory)][string]$OutFile
  )
  $dir = [System.IO.Path]::GetDirectoryName($OutFile)
  if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
  Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $OutFile
  if (-not (Test-Path -LiteralPath $OutFile) -or ((Get-Item -LiteralPath $OutFile).Length -eq 0)) {
    throw "Empty file after download (IWR)"
  }
}

function Download-File {
  param(
    [Parameter(Mandatory)][string]$Uri,
    [Parameter(Mandatory)][string]$OutFile,
    [string]$Activity = "Downloading"
  )
  if (Test-HttpClientAvailable) {
    Download-FileHttpClient -Uri $Uri -OutFile $OutFile -Activity $Activity
  } else {
    Write-Host "HttpClient not available. Falling back to Invoke-WebRequest with built-in progress..." -ForegroundColor DarkYellow
    Download-FileIwr -Uri $Uri -OutFile $OutFile
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
  $Version = Get-LatestVersion  # Note: latest = stable only
} else {
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

  $exeItem = Get-ChildItem -LiteralPath $root -Recurse -File -Filter "podliner.exe" -ErrorAction SilentlyContinue |
             Select-Object -First 1
  return $exeItem
}

try {
  Write-Host "Installing podliner $Version for $Rid -> $Dest" -ForegroundColor Cyan

  # Download archive + checksums (with progress)
  $zipPath = Join-Path $Stage $AssetName
  $sumPath = Join-Path $Stage "SHA256SUMS"

  Download-File -Uri "$BaseUrl/$AssetName" -OutFile $zipPath -Activity "Downloading $AssetName"
  Download-File -Uri "$BaseUrl/SHA256SUMS" -OutFile $sumPath -Activity "Downloading SHA256SUMS"

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

  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $Dest)

  # Locate exe
  $exeItem = Find-PodlinerExe -root $Dest
  if (-not $exeItem) {
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

  # PATH hint
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
