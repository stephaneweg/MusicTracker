---
name: timeline-playback-latency
description: How Séquenceur (timeline) playback achieves glitch-free real-time on heavy pieces
metadata: 
  node_type: memory
  type: project
  originSessionId: 146ef62c-1122-45dc-af55-2d32b4a88818
---

Real-time playback of the Séquenceur ([[timeline-mode]]) was glitchy on heavy pieces (many simultaneous notes / tracks) while **export was perfect** — i.e. the synthesis is correct but couldn't meet the real-time deadline. The fix is three combined layers (all in `Engine/Timeline/`):

1. **`LookaheadBuffer`** (producer/consumer): a background thread renders `TimelinePlayer.Read` AHEAD into a ring buffer; the audio device's `Read` only copies. Decouples the expensive SoundFont synthesis from the audio callback so CPU/GC spikes are absorbed. **Pre-amorçage**: the device only starts once the buffer has a head start (`Primed` event, `primeSeconds`) — otherwise it drains as fast as it fills. Cursor stays correct by mapping **consumed** samples → beat via `TimelinePlayer.BeatAtSample` (a precomputed slice→sample table), so the big look-ahead doesn't push the cursor ahead; `Ended` fires only when the buffer fully drains.
2. **`TimelinePlayer.Read` parallelised PER TRACK**: pre-pass computes per-sample slice index (sequential — slice duration varies with tempo), then `Parallel.For` over tracks (independent voices) each writing its own `float[]` scratch, then sequential mixdown. Throughput ≈ min(tracks, cores). Limit: a single very dense track isn't sped up (would need per-note-range parallelism).
3. **Voice reuse**: clone the per-(track,note) `WaveFunction` only the FIRST time a note is used, then `Reset()` on re-attack (Reset fully restores DSP state incl. biquad history). Avoids `WaveFunction.Clone()` (a JSON serialize+deserialize) per note trigger on the audio thread.

**Why:** the real-time deadline + GC from per-trigger JSON clones were the killers; export has no deadline so it was unaffected.
**How to apply:** export still uses `TimelinePlayer` directly offline (no buffer). Tunables: `LookaheadBuffer(leadSeconds, primeSeconds)`. Now that synthesis is parallel (faster than real-time), `primeSeconds` can be lowered for a quicker start. The same voice-reuse / per-track-parallel pattern could be ported to `FlowPlayer` (graph) and `RiffPlayer` if those lag.
