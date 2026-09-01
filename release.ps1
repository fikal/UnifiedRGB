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

# Stamp the version
$csproj = "src/UnifiedRgb.App/UnifiedRgb.App.csproj"
(Get-Content $csproj) -replace '<Version>.*</Version>', "<Version>$Version</Version>" |
    Set-Content -Encoding utf8 $csproj

# Tests (console harness - `dotnet test` does not run these)
dotnet run --project src/UnifiedRgb.Tests -c Release
if ($LASTEXITCODE -ne 0) { git checkout -- $csproj; throw "Tests failed - version stamp reverted" }

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

$asset = "UnifiedRGB-v$Version.exe"
Copy-Item $exe.FullName $asset -Force
$sha = (Get-FileHash $asset -Algorithm SHA256).Hash.ToLower()
"$sha  $asset" | Set-Content -Encoding ascii "$asset.sha256"

git add $csproj
git commit -m "v$Version"
git push

$notes = @"
## Verify your download
``````
sha256: $sha
``````
Unsigned binary - SmartScreen will warn on first run (More info -> Run anyway).

In-app updates: existing installs offer this version automatically at next launch.
"@
gh release create "v$Version" --target main --title "UnifiedRGB v$Version" `
    --notes $notes --generate-notes $asset "$asset.sha256"
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }

Remove-Item $asset, "$asset.sha256"
Write-Host "`nReleased v$Version  (sha256 $sha)" -ForegroundColor Green
Write-Host "https://github.com/fikal/UnifiedRGB/releases/tag/v$Version"
