# Contributing to UnifiedRGB

## Building & testing

```
dotnet build src/UnifiedRgb.App -c Debug
dotnet run --project src/UnifiedRgb.Tests
```

The tests round-trip the wire codecs (e.g. the Lian Li tinyuz compressor) —
they must stay green. UI changes should be exercised by hand; say what you
clicked in the PR.

## What makes a good PR here

- **Device drivers** are the most valuable contribution. Read
  [`docs/ADDING_A_DEVICE.md`](docs/ADDING_A_DEVICE.md) first: it is the
  contract a driver has to meet (threading, dedup, failure handling,
  detection, registration), the checklist a PR is reviewed against, and how
  to test a driver against the harness's fake HID transport without owning
  the hardware. In short, a new driver:
  - lives in `src/UnifiedRgb.Core/Devices/`, takes an `IHidTransport` and
    opens it via `HidNative.OpenFirst`, degrading to "not present" (or a
    `DetectionNotes` report when the hardware is there but unusable);
  - **dedupes identical frames**, committing the cache only after a write
    landed, and reuses its wire buffers;
  - comes with a `FakeHid` fixture pinning its wire format and its
    refused-write behaviour;
  - comes with protocol notes in comments: where each magic number came from
    (USB capture, vendor tool behavior). No decompiled code, ever -
    behavioral reimplementation only.
- **Effects** are stateless and shared across channels: derive everything
  from the clock, the per-LED positions, and `Fx`/`Geo` helpers. Position-
  only math belongs in the `Geo` cache, not in the per-frame loop.
- Performance is a feature. Nothing on a per-frame path may allocate in
  steady state; `PERFORMANCE_REVIEW.md` documents the standards (and the
  measurements that enforce them).
- Match the style around you — comment density, naming, the banner comments.

## Reporting bugs

Use the bug template and attach the diagnostic bundle (Settings → Support →
**Report a problem** writes it to your Desktop). It contains your hardware
survey and the session log — skim it before attaching if that concerns you.

## Licensing of contributions

By submitting a contribution you agree it is licensed under GPLv2 like the
rest of the project.
