---
name: violin-audio-capture
description: "The app's flagship real-world use — live violin via the audio input as a near-zero-latency note source (a \"virtual MIDI violin\")"
metadata: 
  node_type: memory
  type: project
  originSessionId: 146ef62c-1122-45dc-af55-2d32b4a88818
---

The user plays **violin** into the **audio input** and uses the app's pitch→note detection as a substitute for a (not-bought) MIDI violin. This is a flagship use case, so **low detection latency matters a lot** — the goal is near-instant, real-time note feedback.

**Why:** they capture distinct notes (often détaché), so the from-silence fast path and a short analysis window are what help most.

**How to apply:** for violin, the analysis window can be small (≥ ~86 Hz coverage) — recommend **"Fenêtre d'analyse" = 512** (~12 ms) in settings; the inherent floor is ~2 periods of the lowest played pitch. Tuning lives in `WaveNoteSourceProvider` (see [[riff-note-list-model]]): `hopLen=FrameSize/4`, from-silence `need=1`, `MaxHoldSeconds` debounce cap, WaveIn 10 ms buffer, capture thread ThreadPriority.Highest. If pushing further: a per-onset adaptive sub-window, or MMCSS "Pro Audio" thread (Win32, risky) — not yet done.
