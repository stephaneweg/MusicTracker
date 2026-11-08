---
name: kanban-backlog
description: Project backlog lives in KANBAN.md at the repo root (À faire / En cours / Fait)
metadata: 
  node_type: memory
  type: project
  originSessionId: 146ef62c-1122-45dc-af55-2d32b4a88818
---

The project backlog is kept in `KANBAN.md` at the repo root — check/update it for pending work.

Key "À faire" idea (user's, for later): **Timeline ↔ Graph conversion + possibly a unified file format**.
- Timeline → Graph is easy (a track's modules become a node chain; tracks = parallel branches).
- Graph → Timeline is harder (a node splitting into 2 branches needs a new track + leading silence, or a "track that starts at a point referencing the previous track").

Also in the backlog: Phase 2 = Timeline player, Phase 3 = drag & drop. See [[timeline-mode]]. (User has NOT saved any file yet, so no `.sq` compatibility constraints to worry about for format changes.)
