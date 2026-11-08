---
name: sequencer-photoshop-layers
description: "The sequencer is designed like Photoshop — one instrument = one layer, with transparency to see other layers while editing"
metadata: 
  node_type: memory
  type: project
  originSessionId: 146ef62c-1122-45dc-af55-2d32b4a88818
---

Design intent (from the author): the sequencer follows a Photoshop metaphor — **one instrument = one layer** (`SequencerLayer` class in `Screens/SequencerScreen.xaml.cs`). When editing one instrument's notes, the other layers stay visible by transparency.

Implementation: each layer has its own color (from `layerColors`), its own `WaveFunction`, and its own overlaid `WriteableBitmap` stacked in `gridContent`. On layer selection (`listLayers_SelectionChanged`), all layers get `img.Opacity = 0.25` and the active one gets `Opacity = 1` and is moved to the top of the z-order.

**How to apply:** preserve this metaphor when touching the sequencer rendering/layer code — the per-layer color + dimmed-other-layers transparency is intentional, not incidental. Note: an `alphaRatio` computed in `Redraw` is currently dead code; dimming is done via `img.Opacity`.

**Layer mixing (`SequencerLayerWaveProvider.Read`)**: layers are now SUMMED + clamped to [-1,1] (was `total / layerCount`, an average that made multi-track imports far too quiet vs graph mode which sums). Matches the graph mixer (`FlowPlayer.Read`). Verified: 3-layer chord RMS ~2× a single note (was ~equal under averaging). Per-layer `Volume` still applied before the sum.

**Audio export (`Engine/WaveExporter.cs`)** — rewritten. The old version hand-wrote the WAV/RIFF header through a `BinaryWriter` with `int` sizes (overflow on big music), wrote a WAV header even for `.mp3`, and had no try/catch → an unhandled `ObjectDisposedException` ("closed BinaryWriter") on large exports. Now: `Export()` (dialog + count) delegates to a testable `Render(path, count, mp3, provider, sampleRate)` that uses NAudio `WaveFileWriter` (WAV, auto-finalizes header sizes, `long`-safe) or `LameMP3FileWriter` (MP3), chunked (64K mono samples) and interleaved to stereo, wrapped in try/catch + `finally provider.Stop()`. The export handler now `stopPlay()` + resets `NoteIndex=0` first (no concurrent Read on the shared provider, exports from the start). Verified headless: 2,000,000 samples → valid 8 MB stereo 16-bit WAV.

**Piano-roll rendering is VIRTUALIZED + shared.** `SequencerScreen` is used BOTH as the main track editor (`MainWindow` `trackEditor`, plus `new SequencerScreen()` per imported song) AND inside `RiffEditorWindow` (riff mode via `EnterRiffMode`/`LoadRiff`/`CommitRiff`). It never paints the whole grid — `Redraw`/`ReCreateImage` paint only a **viewport-sized** `WriteableBitmap` (`imgLines` + one per layer) and pan by a scroll offset. So a full natural-size bitmap is NOT an option (a long imported song = 10k+ slices × 20px × 96 rows = hundreds of MB). Don't "render the full grid" here.
**Scroll = real `ScrollBar`s (not Sliders).** The pan offset comes from `slHorizontalScroll`/`slVerticalScroll`, which are now `ScrollBar` (was `Slider`) — same `RangeBase` API (`.Value`/`.Maximum`/`ValueChanged`) so the render logic is untouched, but `ScrollBar.ViewportSize` (set in `ResetScroll` = `borderContent.ActualWidth/Height`) gives a proportionally-sized draggable thumb a Slider can't. `Maximum = max(0, content - viewport)`, plus `LargeChange`/`SmallChange`. Vertical bar is NOT direction-reversed (ScrollBar vertical: top = Minimum = 0 = high notes = scroll 0). Themed automatically by the implicit `{x:Type ScrollBar}` style in `Theme/ScrollBar2.xaml` (merged in App.xaml). Mouse wheel scrolls vertically (Shift = horizontal) via `borderContent_MouseWheel`. The user preferred this minimal swap over a ScrollViewer rewrite, to keep the working virtualized logic.

**Export UI**: `Export()` opens a themed `Screens/ExportProgressDialog` (same flat style as ImportModeDialog — borderless rounded Border, CommonBackground) with a **ProgressBar** + percent + Cancel. The dialog runs `Render(...)` on a background `Task.Run`, reporting via `IProgress<double>` (created on the UI thread so updates marshal back), and sets `DialogResult` on completion. `Render` now takes `IProgress<double> progress` + `CancellationToken token`; it reports ~every 0.5%, and on cancel breaks and DELETES the partial file. Verified headless: normal render writes all samples; pre-cancelled token → 0 samples + file deleted.
