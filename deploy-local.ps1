# Put this working tree on THIS PC's logon copy and restart it.
#
# The logon task runs the protected install under C:\Program Files\UnifiedRGB
# (AppInstall), not a bin\Debug exe: a task that runs as administrator must not
# run a file every process of the user can replace. So a code change reaches the
# running app by publishing it the way a release is built and copying it over
# the installed exe - an administrator step, hence one UAC prompt.
#
#   .\deploy-local.ps1            tests, publish, install, restart
#   .\deploy-local.ps1 -NoTests   skip the 90 s test run
param([switch]$NoTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $NoTests) {
    dotnet run --project src/UnifiedRgb.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE failure(s)); nothing deployed" }
}

$out = Join-Path $env:TEMP 'UnifiedRGB-local-publish'
dotnet publish src/UnifiedRgb.App -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed; nothing deployed" }
$exe  = Join-Path $out 'UnifiedRgb.App.exe'
$dest = Join-Path $env:ProgramFiles 'UnifiedRGB\UnifiedRGB.exe'
if (-not (Test-Path $dest)) { throw "$dest is not installed: tick Start with Windows in a published build once first" }

# The elevated half: stop the app, replace the installed exe, start it through
# its logon task (elevated, no prompt, its runtime under Program Files).
$elevated = @"
Get-Process -Name UnifiedRGB, UnifiedRgb.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Copy-Item -LiteralPath '$exe' -Destination '$dest' -Force
Start-ScheduledTask -TaskName UnifiedRGB
"@
$p = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Verb RunAs -Wait -PassThru `
    -ArgumentList '-NoProfile', '-Command', $elevated
if ($p.ExitCode -ne 0) { throw "The elevated step failed (exit $($p.ExitCode))" }

$v = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dest).FileVersion
Write-Host "deployed $v to $dest and restarted it" -ForegroundColor Green
