---
name: app-data-paths
description: "App data files & the SoundFont folder must resolve relative to the assembly dir, not the working dir"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 146ef62c-1122-45dc-af55-2d32b4a88818
  modified: 2026-07-24T23:31:31.477Z
---

App data files and bundled resources must be resolved relative to the **assembly directory**, NOT the current working directory.

**Why:** when the app is launched via a file association / "Open with", the working directory is the opened file's folder (or System32), so plain relative paths like `"recent.json"`, `"settings.json"`, `"SoundFont\\roland.sf2"` silently miss — the SoundFont list shows empty and recents/settings save to the wrong place.

**How to apply:** use `MusicTracker.AppPaths.Local(relative)` (added 2026-06-14; `AppPaths.cs`, returns `Path.Combine(AppContext.BaseDirectory, relative)`, passes absolute paths through unchanged) for BUNDLED read-only assets.

**UPDATE 2026-07-25 (installer compat, [[release-update-infra]]):** once installed under Program Files the app can't write next to its exe, so WRITABLE files moved to `%AppData%\MusicTracker` via new `AppPaths.Roaming(rel)` + `AppPaths.UserFile(rel)` (migrates a legacy next-to-exe copy on first run). Now roaming: settings.json, recent.json, userdata.json, cache `userdata\changelog.md`. The **Data** folder is an OVERLAY: bundled assets stay next to the exe (read-only), user-created files (AI/imported templates via `TemplateSpec.Dir`, re-analyzed models via `ComposerV2Runtime.ModelsWriteDir`) go to roaming; reads merge both (user shadows bundled) via `AppPaths.DataRead`/`DataFiles`, writes via `DataWrite`/roaming. SoundFont (RESOLVED same day): the 206 MB font is NOT roamed — new `AppPaths.LocalData(rel)` = `%LocalAppData%\MusicTracker`. Auto-download target + Settings "…" browse button (copies a picked .sf2 there) write to `LocalData\SoundFont`; `AppSettings.ResolveSoundFontPath`/`AvailableSoundFonts` check BOTH the writable local folder (wins) and the bundled next-to-exe one. Bundled Data assets stay on `AppPaths.Local`.
