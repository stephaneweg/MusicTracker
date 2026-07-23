using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MusicTracker.Engine
{
    /// <summary>
    /// A reusable musical phrase, stored as a list of NOTES (one polyphonic layer). Notes distinguish two
    /// adjacent same-pitch notes (no "détaché" hack). The INSTRUMENT is NOT stored here -- it comes from the
    /// graph/track context, so the same phrase can be played with different instruments. Tempo is contextual.
    /// </summary>
    public class Riff : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public Guid Id { get; set; } = Guid.NewGuid();

        string name = "Riff";
        public string Name
        {
            get { return name; }
            set { if (name != value) { name = value; OnChanged(nameof(Name)); } }
        }

        /// <summary>Canonical content: the notes (Start/Length in slices at <see cref="SlicesPerQuarter"/>).</summary>
        public List<RiffNote> Notes { get; set; } = new List<RiffNote>();

        /// <summary>Total length in slices (preserves trailing silence / measure padding beyond the last note).</summary>
        public int LengthSlices { get; set; } = 96;

        /// <summary>
        /// Grid resolution: how many slices make up one quarter note. 24 is the standard grid
        /// (matches imported scores and keeps triplets/fine timing).
        /// </summary>
        public int SlicesPerQuarter { get; set; } = 24;

        /// <summary>
        /// Bridge to the binary slice grid, for the parts still working in slices (timeline OR-merge, thumbnails,
        /// importers). Derived from <see cref="Notes"/>; NOT serialized (the notes are the stored form).
        /// </summary>
        [JsonIgnore]
        public SequencerSlice[] Slices
        {
            get => RiffNotes.ToSlices(Notes, LengthSlices);
            set { Notes = RiffNotes.FromSlices(value); LengthSlices = value?.Length ?? 0; }
        }

        public int Length => LengthSlices;

        /// <summary>Back-compat: old files serialized "Slices" (no "Notes"). Read-only (no getter -> not re-saved):
        /// when an old riff is loaded, convert its slice grid into the note list.</summary>
        [JsonPropertyName("Slices")]
        public SequencerSlice[] LegacySlices
        {
            set
            {
                if (value != null && value.Length > 0 && (Notes == null || Notes.Count == 0))
                {
                    Notes = RiffNotes.FromSlices(value);
                    LengthSlices = value.Length;
                }
            }
        }

        public Riff Clone()
        {
            return System.Text.Json.JsonSerializer.Deserialize<Riff>(System.Text.Json.JsonSerializer.Serialize(this));
        }
    }
}
