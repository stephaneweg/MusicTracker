using System;
using System.Collections.Generic;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Snapshot-based undo/redo for the timeline editor. Each entry stores a full serialized project state (the same
    /// <c>.sq</c> JSON used by save/load), so restoring is just a deserialize + apply — no per-field command objects.
    ///
    /// Two consolidation rules (per the user's spec):
    ///  • <b>Coalescing</b> — consecutive pushes with the SAME <c>opKey</c> collapse into one entry (e.g. a drag emits
    ///    many "move:X" steps but a single Ctrl+Z reverts the whole move). The FIRST pre-state is the one kept.
    ///  • <b>Neutralization</b> — an <c>insert:X</c> immediately followed by a <c>delete:X</c> (same object) cancels out:
    ///    both are dropped from history, since the net effect on the document is nil.
    ///
    /// The caller pushes the state captured BEFORE a mutation. Undo swaps current↔previous across the two stacks.
    /// </summary>
    public sealed class UndoManager
    {
        sealed class Entry { public string State; public string OpKey; }

        readonly List<Entry> undo = new List<Entry>();
        readonly List<Entry> redo = new List<Entry>();
        readonly int cap;

        public UndoManager(int cap = 50) { this.cap = Math.Max(1, cap); }

        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;

        /// <summary>Raised whenever the stacks change (so the UI can enable/disable the ↶ ↷ buttons).</summary>
        public event Action Changed;

        /// <summary>Record <paramref name="preState"/> (the serialized state BEFORE the mutation just applied), keyed by
        /// <paramref name="opKey"/> ("move:ID" / "insert:ID" / "delete:ID" / "edit:ID" / …). Applies the coalescing and
        /// neutralization rules. Any pending redo is discarded (a new action forks the timeline).</summary>
        public void Push(string preState, string opKey)
        {
            redo.Clear();

            // Neutralize: delete right after the matching insert -> drop both (nothing net changed).
            if (opKey != null && opKey.StartsWith("delete:") && undo.Count > 0)
            {
                string insertKey = "insert:" + opKey.Substring("delete:".Length);
                if (undo[undo.Count - 1].OpKey == insertKey)
                {
                    undo.RemoveAt(undo.Count - 1);
                    Changed?.Invoke();
                    return;
                }
            }

            // Coalesce: a run of same-key ops keeps only the first pre-state.
            if (opKey != null && IsCoalescable(opKey) && undo.Count > 0 && undo[undo.Count - 1].OpKey == opKey)
            {
                Changed?.Invoke();
                return;
            }

            undo.Add(new Entry { State = preState, OpKey = opKey });
            while (undo.Count > cap) undo.RemoveAt(0);
            Changed?.Invoke();
        }

        // Moves and edits collapse when repeated on the same target; inserts/deletes stay distinct (each is a real step).
        static bool IsCoalescable(string opKey) =>
            opKey.StartsWith("move:") || opKey.StartsWith("edit:") || opKey.StartsWith("vol:");

        /// <summary>Undo: hand in the CURRENT serialized state (stored for redo); returns the state to restore, or null
        /// when there is nothing to undo.</summary>
        public string Undo(string currentState)
        {
            if (undo.Count == 0) return null;
            var e = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            redo.Add(new Entry { State = currentState, OpKey = e.OpKey });
            Changed?.Invoke();
            return e.State;
        }

        /// <summary>Redo: hand in the CURRENT serialized state (stored back for undo); returns the state to restore, or
        /// null when there is nothing to redo.</summary>
        public string Redo(string currentState)
        {
            if (redo.Count == 0) return null;
            var e = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            undo.Add(new Entry { State = currentState, OpKey = e.OpKey });
            Changed?.Invoke();
            return e.State;
        }

        public void Clear()
        {
            if (undo.Count == 0 && redo.Count == 0) return;
            undo.Clear();
            redo.Clear();
            Changed?.Invoke();
        }
    }
}
