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

## Things that look vestigial but are intentional

- `UpdateClient` still contains a private-feed path selected by build-time
  props / `%APPDATA%\UnifiedRgb\backend.json`. Official builds pass
  neither, so it's inert — it exists so a fork can run its own feed.
- Tests are a console harness: `dotnet run --project src/UnifiedRgb.Tests`.
  Plain `dotnet test` exits successfully having run nothing.
- The swap script inside the updater is version-frozen in each shipped
  build; its retry/taskkill quirks encode real field failures — see the
  comments in `UpdateService.cs` before "simplifying" it.
