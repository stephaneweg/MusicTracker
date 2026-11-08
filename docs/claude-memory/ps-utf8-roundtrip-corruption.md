---
name: ps-utf8-roundtrip-corruption
description: Never use PowerShell Get-Content/Set-Content to edit UTF-8 source files — it mojibakes non-ASCII via cp1252
metadata: 
  node_type: memory
  type: feedback
  originSessionId: cb8dab4e-7c02-483e-bb82-37f2c48a3d1c
  modified: 2026-07-24T10:05:58.985Z
---

Editing this repo's source files with Windows PowerShell 5.1 `Get-Content`/`Set-Content` (or `-replace` round-trips) **corrupts every non-ASCII character**: it reads UTF-8-without-BOM as Windows-1252, so `✕`→`âœ•`, `…`→`â€¦`, `—`→`â€"`, `→`→`â†'`, `é`→`Ã©`. Hit on 2026-07-24 fixing MainWindow.xaml.cs glyphs — corrupted the tab-close `✕` and all French accents.

**Why:** PS 5.1 defaults to the system ANSI codepage (cp1252) for files with no BOM; `Set-Content -Encoding utf8` then re-saves the mojibake as real UTF-8. Source files here are UTF-8 **without BOM** with CRLF.

**How to apply:**
- Prefer the **Edit/Write tools** for source edits (UTF-8-safe). Reserve PowerShell for git/build/inspection.
- To type Segoe MDL2 private-use glyphs (e.g. U+E923) that the Edit tool can't represent, build the string in .NET with `[char]0xE923` and write via `[IO.File]::WriteAllText($f, $text, (New-Object System.Text.UTF8Encoding($false)))` (no BOM) — never `Set-Content`. cp1252 turns those glyphs into `?`.
- Recovery if already mojibake'd: read bytes, strip BOM, `UTF8.GetString` → `GetEncoding(1252).GetBytes` → write bytes (reverses one round-trip; but this step itself destroys any real U+E9xx glyphs, so reinsert them afterward in pure .NET).

Related: custom window chrome lives in [[ui-redesign-ntrack]].
