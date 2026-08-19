<#
.SYNOPSIS
    Install or update Supermarket Tweaks (and BepInEx) from the private GitHub repo.

.DESCRIPTION
    Safe to run over and over - that is the point. It finds the game, makes sure BepInEx is
    present, then downloads the newest build and only replaces the plugin if it actually differs.

    The repo is PRIVATE, so downloading the plugin needs credentials. In order of preference:

      1. the GitHub CLI, already signed in as someone with access   (gh auth login)
      2. a personal access token in $env:GH_TOKEN or -Token

    BepInEx itself comes from its own public release, so that part never needs auth.

.EXAMPLE
    .\Update-SupermarketTweaks.ps1

.EXAMPLE
    .\Update-SupermarketTweaks.ps1 -Token ghp_xxx -GameDir "D:\Steam\steamapps\common\Supermarket Together"
#>
[CmdletBinding()]
param(
    [string] $GameDir,
    [string] $Token = $env:GH_TOKEN,
    [string] $Repo  = 'REPO_PLACEHOLDER',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$GameExe     = 'Supermarket Together.exe'
$GameFolder  = 'Supermarket Together'
$PluginName  = 'SupermarketTweaks.dll'
$BepInExUrl  = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.3/BepInEx_win_x64_5.4.23.3.zip'

function Write-Step($msg) { Write-Host "`n$msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "    $msg" -ForegroundColor Yellow }
function Fail($msg)       { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------- find the game

function Get-SteamRoots {
    $roots = @()
    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
        try {
            $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
            if ($item -ne $null) {
                if ($item.SteamPath)   { $roots += $item.SteamPath }
                if ($item.InstallPath) { $roots += $item.InstallPath }
            }
        } catch { }
    }
    $roots += 'C:\Program Files (x86)\Steam'
    return $roots | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
}

# Steam spreads games over several drives; the library list lives in libraryfolders.vdf.
function Get-LibraryPaths($steamRoot) {
    $paths = @($steamRoot)
    $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($line in (Get-Content $vdf)) {
            if ($line -match '"path"\s*"(.+?)"') { $paths += $matches[1].Replace('\\', '\') }
        }
    }
    return $paths | Select-Object -Unique
}

function Find-Game {
    foreach ($root in (Get-SteamRoots)) {
        foreach ($lib in (Get-LibraryPaths $root)) {
            $candidate = Join-Path $lib ('steamapps\common\' + $GameFolder)
            if (Test-Path (Join-Path $candidate $GameExe)) { return $candidate }
        }
    }
    return $null
}

if (-not $GameDir) {
    Write-Step "Looking for $GameFolder..."
    $GameDir = Find-Game
    if (-not $GameDir) {
        Fail ("Could not find the game. Pass the folder manually, e.g.`n" +
              "  .\Update-SupermarketTweaks.ps1 -GameDir `"D:\Steam\steamapps\common\$GameFolder`"")
    }
}
if (-not (Test-Path (Join-Path $GameDir $GameExe))) { Fail "That folder has no $GameExe : $GameDir" }
Write-Ok $GameDir

# The plugin file is locked while the game runs, and a half-written DLL is worse than a stale one.
$running = Get-Process -Name 'Supermarket Together' -ErrorAction SilentlyContinue
if ($running) { Fail 'Supermarket Together is running. Close it and run this again.' }

# ---------------------------------------------------------------- BepInEx

$bepDir = Join-Path $GameDir 'BepInEx'
if (-not (Test-Path (Join-Path $GameDir 'winhttp.dll')) -or -not (Test-Path (Join-Path $bepDir 'core'))) {
    Write-Step 'Installing BepInEx...'
    $tmp = Join-Path $env:TEMP ('BepInEx5_' + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        Invoke-WebRequest -Uri $BepInExUrl -OutFile $tmp -UseBasicParsing
        Expand-Archive -Path $tmp -DestinationPath $GameDir -Force
        Write-Ok 'BepInEx installed.'
    } catch {
        Fail "Could not install BepInEx: $($_.Exception.Message)"
    } finally {
        if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
    }
} else {
    Write-Step 'BepInEx already present.'
    Write-Ok 'Skipping.'
}

$pluginDir = Join-Path $bepDir 'plugins'
if (-not (Test-Path $pluginDir)) { New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null }

# ---------------------------------------------------------------- fetch the plugin

Write-Step 'Downloading the latest build...'
$dl = Join-Path $env:TEMP ('SupermarketTweaks_' + [Guid]::NewGuid().ToString('N') + '.dll')
$gh = Get-Command gh -ErrorAction SilentlyContinue

if ($gh) {
    try {
        # Writes into a temp folder because gh picks the filename from the asset.
        $stage = Join-Path $env:TEMP ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $stage | Out-Null
        & gh release download latest --repo $Repo --pattern $PluginName --dir $stage --clobber
        if ($LASTEXITCODE -ne 0) { throw "gh exited with $LASTEXITCODE" }
        Move-Item (Join-Path $stage $PluginName) $dl -Force
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
        Write-Ok 'Downloaded with the GitHub CLI.'
    } catch {
        Write-Warn2 "GitHub CLI failed: $($_.Exception.Message)"
        $gh = $null
    }
}

if (-not (Test-Path $dl)) {
    if (-not $Token) {
        Fail ("Need access to the private repo. Either:`n" +
              "  - install the GitHub CLI and run:  gh auth login`n" +
              "  - or pass a token:  .\Update-SupermarketTweaks.ps1 -Token ghp_xxxx")
    }
    try {
        # The releases API gives the asset id; the asset itself needs an octet-stream Accept
        # header or GitHub hands back JSON metadata instead of the file.
        $headers = @{ Authorization = "Bearer $Token"; 'User-Agent' = 'SupermarketTweaks-Updater' }
        $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/tags/latest" -Headers $headers
        $asset = $rel.assets | Where-Object { $_.name -eq $PluginName } | Select-Object -First 1
        if (-not $asset) { Fail "The 'latest' release has no $PluginName asset yet. Has the build run?" }

        $headers['Accept'] = 'application/octet-stream'
        Invoke-WebRequest -Uri "https://api.github.com/repos/$Repo/releases/assets/$($asset.id)" `
                          -Headers $headers -OutFile $dl -UseBasicParsing
        Write-Ok 'Downloaded with token.'
    } catch {
        Fail "Download failed: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------- install if changed

$target = Join-Path $pluginDir $PluginName
$new    = (Get-FileHash $dl -Algorithm SHA256).Hash
$old    = if (Test-Path $target) { (Get-FileHash $target -Algorithm SHA256).Hash } else { '' }

if ($new -eq $old -and -not $Force) {
    Write-Step 'Already up to date.'
    Write-Ok "$PluginName is the current build."
    Remove-Item $dl -Force -ErrorAction SilentlyContinue
} else {
    Write-Step 'Installing...'
    Copy-Item $dl $target -Force
    Remove-Item $dl -Force -ErrorAction SilentlyContinue

    # Trust the file on disk rather than the copy succeeding quietly.
    $check = (Get-FileHash $target -Algorithm SHA256).Hash
    if ($check -ne $new) { Fail 'The file did not copy correctly.' }
    Write-Ok "$PluginName updated."
}

# A previously installed standalone Time Acceleration mod fights this one - both bind F5 and both
# write Time.timeScale - so retire it rather than leaving two mods arguing.
$old_ta = Join-Path $pluginDir 'TimeAcceleration.dll'
if (Test-Path $old_ta) {
    Move-Item $old_ta "$old_ta.disabled" -Force
    Write-Warn2 'Disabled the old TimeAcceleration.dll (superseded; renamed to .disabled).'
}

Write-Host "`nDone. Launch the game - F1 opens settings, F5 toggles game speed." -ForegroundColor Green
