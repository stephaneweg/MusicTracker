using System;
using System.Collections.Generic;
using MusicTracker.Engine;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Emits the timeline tracks for a composed arrangement in the NEW "melodic-line" model: melody / counter / bass are
    /// <see cref="MelodicLineModule"/> rhythm skeletons (pitches derived by <see cref="MelodicLineEngine"/> from the chord
    /// grid), and chords + pad are custom-articulated <see cref="PatternGeneratorModule"/>s. All materials (rhythm cells,
    /// articulations, register, voices) come from the <see cref="MotifCatalogue"/>. The generator only composes harmony.
    /// </summary>
    public static class OrchestrateurLines
    {
        /// <param name="prog">one (root,quality) per bar.</param>
        public static void Emit(
            List<TimelineTrack> tracks, List<(int root, int quality)> prog, List<ArrSection> sections,
            CatalogueVariant v, int beatsPerBar,
            int leadInst, int counterInst, int accompInst, int bassInst, int padInst,
            bool counterSameStaff, bool includeCounter, bool includePad, bool includeBass, int seed)
        {
            if (v == null || prog == null || sections == null) return;

            // ---- 1) MELODY (+ counter same-staff) : one MelodicLine per section ----
            var melTrack = new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = leadInst, Name = "Mélodie", Volume = 1.0 };
            TimelineTrack counterTrack = (!counterSameStaff && includeCounter)
                ? new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = counterInst, Name = "Contre-chant", Volume = 0.75 } : null;
            var bassTrack = includeBass ? new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = bassInst, Name = "Basse", Volume = 0.7 } : null;
            int devUnit = 0;
            foreach (var sec in sections)
            {
                int bars = Math.Max(1, sec.Bars);
                var role = DevVary(v.RoleFor(sec.Role, sec.Role == "dev" ? seed + devUnit : seed), sec.Role, ref devUnit, out int devLift);
                melTrack.Items.Add(new TimelineItem { Module = v.BuildLine(role, bars, beatsPerBar, sec.Name ?? sec.Role, counterSameStaff && includeCounter) });
                if (counterTrack != null)
                {
                    var cl = v.BuildCounterLine(role, bars, beatsPerBar, "Contre " + (sec.Name ?? sec.Role));
                    counterTrack.Items.Add(new TimelineItem { Module = (FlowModule)cl ?? new MelodicLineModule { BeatsPerBar = bars * beatsPerBar, VoiceCount = 1 } });
                }
                if (bassTrack != null)
                    bassTrack.Items.Add(new TimelineItem { Module = v.BuildBass(bars, beatsPerBar, "Basse " + (sec.Name ?? sec.Role)) });
            }
            tracks.Add(melTrack);
            if (counterTrack != null) tracks.Add(counterTrack);

            // ---- 2) CHORDS + NAPPE : one custom Pattern per bar ----
            // Accompaniment sits nearly as loud as the melody; the pad is a very faint "velvet carpet" underneath.
            var chordTrack = new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = accompInst, Name = "Accompagnement", Volume = 0.9 };
            var nappeTrack = includePad ? new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = padInst, Name = "Cordes (nappe)", Volume = 0.15 } : null;
            int nappeSpq; var nappeArtic = v.NappeArticNotes(out nappeSpq);
            int bar = 0;
            int prevBassPc = -1, prevNappePc = -1;
            devUnit = 0;
            foreach (var sec in sections)
            {
                int bars = Math.Max(1, sec.Bars);
                var role = DevVary(v.RoleFor(sec.Role, sec.Role == "dev" ? seed + devUnit : seed), sec.Role, ref devUnit, out _);
                int articSpq; var artic = v.ChordArticNotes(role.Chord, out articSpq);
                for (int b = 0; b < bars && bar < prog.Count; b++, bar++)
                {
                    var c = prog[bar];
                    int inv = VoiceLeadInv(c.root, c.quality, ref prevBassPc);
                    chordTrack.Items.Add(new TimelineItem { Module = MakeChord(c.root, c.quality, inv, artic, articSpq, beatsPerBar, 4, false, (sec.Name ?? sec.Role) + " (acc)") });
                    if (nappeTrack != null)
                    {
                        int ninv = VoiceLeadInv(c.root, c.quality, ref prevNappePc);
                        nappeTrack.Items.Add(new TimelineItem { Module = MakeChord(c.root, c.quality, ninv, nappeArtic, nappeSpq, beatsPerBar, 5, true, "Nappe") });
                    }
                }
            }
            tracks.Add(chordTrack);
            if (nappeTrack != null) tracks.Add(nappeTrack);
            if (bassTrack != null) tracks.Add(bassTrack);
        }

        // Development variation: successive dev sections lift the register and (later) add a voice / thicken the chord.
        static RoleMotif DevVary(RoleMotif role, string secRole, ref int devUnit, out int lift)
        {
            lift = 0;
            if (role == null || secRole != "dev") return role;
            int u = devUnit++;
            lift = Math.Min(12, u * 4);                 // rise ~a 3rd per dev unit
            var r = new RoleMotif { Role = role.Role, Line = role.Line, Counter = role.Counter, Chord = role.Chord, Register = role.Register + lift, Voices = role.Voices };
            if (u >= 2) r.Voices = 2;                    // thicken toward the climax
            return r;
        }

        static readonly int CustomStyle = PatternGenerator.CustomStyle;

        static PatternGeneratorModule MakeChord(int root, int quality, int inv, List<RiffNote> artic, int articSpq, int beatsPerBar, int octave, bool open, string userStyle)
        {
            int total = beatsPerBar * Math.Max(1, articSpq);
            var notes = artic != null ? new List<RiffNote>(artic) : new List<RiffNote>();
            var pg = new PatternGeneratorModule
            {
                Root = ((root % 12) + 12) % 12, Quality = quality, Degree = -1, Inversion = inv,
                Octave = octave, OpenVoicing = open, VoiceLeadMode = 1,
                Style = CustomStyle, BeatsPerBar = beatsPerBar, Repeats = 1,
                CustomSlicesPerQuarter = articSpq, UserStyleName = userStyle,
            };
            pg.CustomNotes = notes;
            pg.CustomSlices = RiffNotes.ToSlices(notes, total);
            return pg;
        }

        // Pick the inversion (0..2) whose lowest chord tone moves least from the previous bar (smooth bass voice-leading).
        static int VoiceLeadInv(int root, int quality, ref int prevLowPc)
        {
            int third = (quality == 1 || quality == 2 || quality == 7 || quality == 12 || quality == 14 || quality == 17) ? 3 : 4;
            int fifth = (quality == 2 || quality == 9 || quality == 10) ? 6 : (quality == 3 ? 8 : 7);
            int[] tones = { ((root % 12) + 12) % 12, (root + third) % 12, (root + fifth) % 12 };
            Array.Sort(tones);
            int best = 0, bestCost = int.MaxValue, bestLow = tones[0];
            for (int inv = 0; inv < 3; inv++)
            {
                int low = tones[inv % 3];
                int cost = prevLowPc < 0 ? 0 : Math.Min(((low - prevLowPc) % 12 + 12) % 12, ((prevLowPc - low) % 12 + 12) % 12);
                if (cost < bestCost) { bestCost = cost; best = inv; bestLow = low; }
            }
            prevLowPc = bestLow;
            return best;
        }
    }
}
