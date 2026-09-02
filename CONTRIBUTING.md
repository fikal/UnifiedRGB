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

- **Device drivers** are the most valuable contribution. A new driver should:
  - live in `src/UnifiedRgb.Core/Devices/`, opened via `HidNative.OpenFirst`
    (or its own transport) with failures degrading to "not present";
  - **dedupe identical frames** (see `IRgbDevice` — the write path runs at
    up to 60 fps forever) and reuse its wire buffers;
  - come with protocol notes in comments: where each magic number came from
    (USB capture, vendor tool behavior). No decompiled code, ever —
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
