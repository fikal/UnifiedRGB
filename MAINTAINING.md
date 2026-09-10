# Maintaining UnifiedRGB

The short version, for six-months-from-now you.

## One distribution channel

Everything goes through **GitHub Releases**:

- **Updates**: the app checks this repo's latest release at startup
  (`UpdateClient.cs`) and installs it in one click — SHA-256 and the
  binary's embedded version are verified before the swap. Users can turn
  the check off in Settings.
- **Bug reports**: the in-app support button collects a diagnostic bundle,
  saves it to the user's Desktop, and opens a prefilled GitHub issue for
  them to drag it into. Nothing is uploaded automatically.

## Cutting a release

```powershell
.\release.ps1 -Version 1.0.19
```

That stamps the csproj, runs the tests, builds the self-contained exe,
verifies the built binary really is that version, commits + pushes the
stamp, and creates the GitHub release with the exe and a `.sha256` asset
(the sha also goes in the notes — the updater reads either). Edit the
auto-generated notes on GitHub afterwards if you want prose.

Rules the script enforces: run from `main`, clean tree, tests green,
built FileVersion == the version being released.

## Docs and the website

- **`docs/` is the GitHub Pages root.** Everything in it is live at
  unifiedrgb.com the moment it is pushed - a working note, a review, a
  security scan. Internal documents go at the repo root or nowhere.
- `release.ps1` stamps `docs/index.html` and must read it **as UTF-8**
  (`Get-Content -Encoding utf8`). Windows PowerShell reads a BOM-less file
  as the ANSI code page; the first stamp run double-encoded every non-ASCII
  character on the site and it stayed that way for a release. Parse-check
  the script after any edit:
  `[System.Management.Automation.Language.Parser]::ParseFile($p,[ref]$t,[ref]$e)`.
- DNS lives at Namecheap (BasicDNS, not custom nameservers): four `A`
  records for `@` on GitHub's Pages addresses and a `www` CNAME to
  `fikal.github.io`. Enforce HTTPS is a repo setting under Pages.

## Building and testing

- The test harness references the App project, so **stop the running app
  first** (`schtasks /End /TN UnifiedRgb`) or the build cannot replace its
  DLLs. It also refuses to run at all if its config redirect is not in
  effect, so it can never touch the real profile - do not weaken that.
- HID drivers hold an `IHidTransport`; `FakeHid` in the harness drives a
  real driver without hardware. See `ADDING_A_DEVICE.md`.

## Things that look vestigial but are intentional

- `UpdateClient` still contains a private-feed path selected by build-time
  props / `%APPDATA%\UnifiedRgb\backend.json`. Official builds pass
  neither, so it's inert — it exists so a fork can run its own feed.
- Tests are a console harness: `dotnet run --project src/UnifiedRgb.Tests`
  (what `release.ps1` and CI run; the exit code is the failure count). The
  Tests csproj also hooks `AfterTargets="VSTest"` so a plain `dotnet test`
  runs the same harness and fails on any failure - that Target is not dead
  wiring, keep it. Suites live one per file under `Suites/` and are listed in
  `Suites.cs`; `Program.cs` is only the gate, the loop and the summary, and
  `-- <name>` runs a single suite.
- The swap script inside the updater is version-frozen in each shipped
  build; its retry/taskkill quirks encode real field failures — see the
  comments in `UpdateService.cs` before "simplifying" it.
