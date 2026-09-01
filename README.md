# UnifiedRGB

One tray app for RGB lighting, fan curves, and the pump LCD — fast enough to
forget it's running. Native Windows (WPF, .NET 10), no vendor bloatware, no
accounts, no telemetry.

## What it does

- **Lighting** across keyboards, mice, motherboard ARGB headers, GPUs, DRAM,
  and Lian Li fans (wireless SL-INF and the wired SL-Infinity hub) — with an
  opt-in bridge to a bundled [OpenRGB](https://openrgb.org) instance for
  everything else.
- **~65 effects**: rainbows, meteors, audio-reactive (event-driven WASAPI
  loopback), key-reactive ripple, screen ambient (whole-screen ambilight),
  Wallpaper Engine capture, Razer Chroma game sync (no Razer software
  required), circadian Time Warmth, multi-color palette effects with a
  built-in palette library (imports coolors.co URLs).
- **Cooling**: temperature-driven fan curves (CPU/GPU/hottest source) via
  LibreHardwareMonitor + PawnIO, live gauges, a drag-to-edit curve editor,
  and a thermal failsafe that hands fans back to the BIOS.
- **Pump LCD designer** (Thermalright 240×320): drag-and-drop clock/temps/
  network/weather widgets, GIF backgrounds, scenes with a sequencer.
- **Quality of life**: profiles, per-app auto-switching, night mode with an
  idle gate, global hotkeys, a first-run wizard.

Performance is a feature: the whole app idles under 5% of one core with the
window open and allocates almost nothing in steady state (the journey there is
documented in [PERFORMANCE_REVIEW.md](PERFORMANCE_REVIEW.md)).

## Supported hardware (native drivers)

| Device | Transport |
|---|---|
| Corsair Strafe MK.2 | HID |
| SteelSeries Apex keyboards | HID |
| Gigabyte RGB Fusion mobos (IT5711) + ARGB headers | HID |
| Logitech G403 HERO (HID++ 2.0) | HID |
| MSI GeForce GPUs | I²C via NvAPI |
| ENE DRAM (G.Skill and friends) | SMBus via PawnIO |
| Lian Li SL-INF wireless | WinUSB + RF |
| Lian Li SL-Infinity wired hub | HID |
| Sayo devices | HID |
| Thermalright pump LCD (240×320) | HID |
| Everything OpenRGB supports | opt-in bundled OpenRGB over its SDK socket |

## ⚠️ Read before running

- The app **requires administrator** and installs
  [PawnIO](https://pawnio.eu/) — a signed, module-constrained kernel driver —
  for CPU temperature, SMBus (DRAM RGB), and fan control. That is real kernel
  access; understand it before you run it.
- DRAM lighting talks **raw SMBus**. The implementation follows OpenRGB's
  proven ENE detection (remap-then-probe at known-safe addresses), but as
  with every tool in this category: writing to the wrong SMBus device can
  permanently damage hardware. Nothing here writes outside the detected ENE
  controllers.
- Protocols were reverse-engineered for interoperability (USB captures and
  behavioral analysis), and several drivers began as ports of
  [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB)'s GPLv2 device code —
  one of the reasons this project is GPLv2. All product names belong to their
  owners; this project is affiliated with none of them.

## Privacy

Open-source builds make **one network request**: a startup check of this
repo's GitHub Releases (`api.github.com`) so the app can offer one-click
updates — turn it off in Settings and it makes none. Everything else is
only what you invoke (installing the OpenRGB bundle, fetching weather for
an LCD widget you added). There is no telemetry. The "support bundle"
button writes a diagnostic file to your Desktop for you to attach to an
issue — nothing is uploaded; the button also opens a prefilled GitHub
issue for you to drop the file into. (The source also supports a private
update feed selected by build-time props; official builds don't use one —
it exists so forks can run their own.)

## Building

Requirements: Windows 10/11 · [.NET 10 SDK](https://dotnet.microsoft.com) ·
Windows 11 SDK (via Visual Studio 2022+ workloads).

```
dotnet build src/UnifiedRgb.App -c Release
dotnet run --project src/UnifiedRgb.Tests        # protocol round-trip tests
```

The CLI harness (`dotnet run --project src/UnifiedRgb.Cli`) lists detected
devices and can set colors headlessly — useful when bringing up a new driver.

Optional: the Razer Chroma interop shim (`native/chroma-shim`) builds with
`build.bat` (needs the VS C++ toolset). The app runs fine without it; the
Chroma-via-DLL path is opt-in from Settings.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Device support PRs are especially
welcome — bring protocol notes with your driver.

## License

[GPLv2](LICENSE) — required by the OpenRGB-derived drivers, and chosen
gladly: it keeps reverse-engineered protocol work open for everyone.
