# Cuts a UnifiedRGB release: stamps the version, tests, builds the
# self-contained exe, and publishes a GitHub release with a .sha256 asset.
# The in-app updater picks it up from GitHub Releases automatically.
#
#   .\release.ps1 -Version 1.0.19
#
# Requires: dotnet SDK, gh CLI (authenticated). Run from the repo root on main.
param(
    [Parameter(Mandatory = $true)][string]$Version
)
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z (got '$Version')" }

$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'main') { throw "Release from 'main' (currently on '$branch')" }
if (git status --porcelain) { throw "Working tree not clean - commit or stash first" }

# The Chroma shim is bundled INTO the single-file exe (the csproj picks it up
# when present); a release without it silently hides the whole Chroma section.
if (-not (Test-Path "native/chroma-shim/RzChromaSDK64.dll")) { throw "native/chroma-shim/RzChromaSDK64.dll missing - run native/chroma-shim/build.bat first" }
if (-not (Test-Path "native/chroma-shim/RzChromaSDK.dll"))   { throw "native/chroma-shim/RzChromaSDK.dll (32-bit) missing - run native/chroma-shim/build.bat first" }

# Stamp the version
$csproj = "src/UnifiedRgb.App/UnifiedRgb.App.csproj"
if ((Get-Content $csproj -Raw) -notmatch '<Version>') { throw "$csproj has no <Version> element to stamp" }
(Get-Content $csproj) -replace '<Version>.*</Version>', "<Version>$Version</Version>" |
    Set-Content -Encoding utf8 $csproj

$asset = "UnifiedRGB-v$Version.exe"
try {
    # Tests (console harness; exit code = failure count. The csproj also wires the
    # same harness to `dotnet test`, so either entry point gives the real result.)
    dotnet run --project src/UnifiedRgb.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

    # Self-contained single-file build. No backend props are passed, so this is a
    # public build: it updates from GitHub Releases and uploads nothing.
    dotnet publish src/UnifiedRgb.App -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

    # Newest publish output (the TFM segment changes across SDK updates)
    $exe = Get-ChildItem "src/UnifiedRgb.App/bin/Release" -Recurse -Filter "UnifiedRgb.App.exe" |
        Where-Object { $_.FullName -match '\\win-x64\\publish\\' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { throw "Built exe not found under bin/Release" }

    # The binary must really be the version we are about to publish (a stale
    # artifact here once shipped an old build under a new version number)
    $fv = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe.FullName).FileVersion
    if ($fv -ne "$Version.0") { throw "Built exe is $fv, expected $Version.0 - stale artifact?" }

    Copy-Item $exe.FullName $asset -Force
    $sha = (Get-FileHash $asset -Algorithm SHA256).Hash.ToLower()
    "$sha  $asset" | Set-Content -Encoding ascii "$asset.sha256"
}
catch {
    # Every pre-commit failure leaves the tree exactly as it was (the old script
    # only reverted the stamp on a test failure, so the next run tripped the
    # clean-tree check on a stale stamp / stray asset).
    git checkout -- $csproj
    Remove-Item $asset, "$asset.sha256" -ErrorAction SilentlyContinue
    throw
}

# $ErrorActionPreference does not cover native commands: check each git step,
# or a rejected push would let `gh release create` tag the REMOTE tip - the
# previous commit, without the version stamp.
# -q on both: git reports progress and the push summary on STDERR, and under
# Windows PowerShell 5.1 a native command's stderr becomes a terminating error
# when the script's output is redirected (e.g. `.elease.ps1 2>&1`) - which
# aborted a run right after a SUCCESSFUL push, before the release was created.
git add $csproj
git commit -q -m "v$Version"
if ($LASTEXITCODE -ne 0) { throw "git commit failed" }
git push -q
if ($LASTEXITCODE -ne 0) { throw "git push failed - release NOT created (stamp is committed locally)" }
$head = (git rev-parse HEAD).Trim()

$notes = @"
## Verify your download
``````
sha256: $sha
``````
Unsigned binary - SmartScreen will warn on first run (More info -> Run anyway).

In-app updates: existing installs offer this version automatically at next launch.
"@
gh release create "v$Version" --target $head --title "UnifiedRGB v$Version" `
    --notes $notes --generate-notes $asset "$asset.sha256"
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }

Remove-Item $asset, "$asset.sha256"
Write-Host "`nReleased v$Version  (sha256 $sha)" -ForegroundColor Green
Write-Host "https://github.com/fikal/UnifiedRGB/releases/tag/v$Version"
