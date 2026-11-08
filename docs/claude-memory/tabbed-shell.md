---
name: tabbed-shell
description: MainWindow = custom tab-STRIP of buttons (Accueil + one closable button per open music) over one content host; open/new/AI-compose each spawn a tab
metadata: 
  node_type: memory
  type: project
  originSessionId: f952e927-6dc4-4278-9d24-2ab81936122d
---

**Tabbed shell (2026-07-19).** First tried a WPF `TabControl` but its themed headers rendered badly → replaced with a CUSTOM tab strip: a `StackPanel x:Name="tabStrip"` of Buttons (styled `TabButton` in Window.Resources: blue `comboBoxBackGround` when `Tag=="sel"`, grey `#2E2E38` otherwise, hover border) in the topBar, over a single `ContentControl x:Name="host"`. First button = **Accueil** (`homeScreen`), non-closable. Each open music = a closable Button (content = StackPanel[name `TextBlock`, ✕ `TextBlock`]) whose Click `Select(editor)`; the ✕ = `PreviewMouseLeftButtonDown → CloseEditor` (Handled so it doesn't also select). `TimelineScreen` implements `IMusicEditor`; several stay open side by side.

- State: `homeScreen`, `homeBtn`, `List<(IMusicEditor editor, Button btn)> editorTabs`, `object current`. `Select(content)` sets `host.Content`, marks the matching button `Tag="sel"`, STOPS audio on every other editor. `CloseEditor` stops audio, removes button+entry, selects a neighbour or Accueil. `SetEditorTitle`/`SetTabButtonContent` rebuild a button's content on save/load.
- Entry points → new tab: Home `NewSequencerRequested`/`OpenRequested`/`OpenRecentRequested` + file arg + **`ComposeAiRequested`** (Home button "Composer avec l'IA…") + a TimelineScreen's **`ComposeInNewTabRequested`** (its Piste▸Composer menu RAISES this, no longer applies in-place). `ComposeWithAiNewTab()` opens `AiComposeDialog` → `OpenEditor(new TimelineScreen(), null)` + `Defer(editor.ComposeFresh(result, fix))`.
- **SAVE moved into TimelineScreen's toolbar** (button `btnSaveMusic`, left of ▶) → raises `SaveRequested` → shell `SaveEditor(editor)` (file dialog if no path + recent + tab-button title). TopBar keeps only the settings gear.
- `TimelineScreen.ComposeFresh(a, fixNotes)` = `ApplyAiArrangement(a, fixNotes, append:false)` (public). "Varier le thème" (context menu on a riff) still appends in-place on the current tab.
