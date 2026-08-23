using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KotonStudio.Library;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Applique la chaîne de constrainers (<see cref="TimelineTrack.NoteConstrainers"/>) sur le
    /// <see cref="Riff"/> produit par un module, avant qu'il ne soit consommé par le player, la
    /// partition ou l'export MIDI. Doit être appelé de manière IDENTIQUE par les 3 résolveurs pour
    /// que l'audio, la partition et l'export MIDI restent parfaitement en phase (cf. la note
    /// mémoire « new-module-three-resolvers » — c'est le même risque de divergence ici).
    ///
    /// **Instances vivantes** : chaque piste possède un cache d'instances de constrainers,
    /// instanciées à la 1re rencontre depuis <see cref="KotonPluginRegistry"/>. Les instances
    /// sont conservées dans une <see cref="ConditionalWeakTable"/> indexée par la
    /// <see cref="TimelineTrack"/>, ce qui laisse le GC libérer si la piste disparaît.
    ///
    /// **Bypass rapide** : si la chaîne est vide, si toutes les entrées sont en Bypass ou si le
    /// Riff est nul, on renvoie le Riff d'entrée sans allocation.
    /// </summary>
    public static class NoteConstrainerChain
    {
        // Cache par-piste des instances vivantes. Reconstruit à la demande depuis les refs sérialisées.
        sealed class TrackChain
        {
            public List<IKotonGeneratorConstrainer> Instances = new List<IKotonGeneratorConstrainer>();
            public List<NoteConstrainerRef> RefsSnapshot = new List<NoteConstrainerRef>();
        }
        static readonly ConditionalWeakTable<TimelineTrack, TrackChain> _cache = new ConditionalWeakTable<TimelineTrack, TrackChain>();

        /// <summary>Applique la chaîne de constrainers d'une piste sur un <paramref name="riff"/> donné.
        /// Le riff en entrée est utilisé en lecture seule ; la sortie est un nouveau Riff (ou le même
        /// s'il n'y a rien à faire).</summary>
        /// <param name="wantsViz">true = appel depuis le pipeline audio (le constrainer peut émettre
        /// ses événements de visualisation temps réel). false = score/export (silencieux côté viz).</param>
        public static Riff Apply(TimelineTrack track, Riff riff, TimelineProject project, double absoluteStartBeat, bool wantsViz = false)
        {
            if (track == null || riff == null || riff.Notes == null || riff.Notes.Count == 0) return riff;
            if (track.NoteConstrainers == null || track.NoteConstrainers.Count == 0) return riff;

            var chain = EnsureChain(track);
            if (chain == null || chain.Instances.Count == 0) return riff;

            // Contexte de rendu — même helper que KotonGeneratorModule pour garantir la parité
            // exacte (tonic pc / mode / tempo / signature).
            var ctx = Flow.KotonGeneratorRuntime.ContextFor(project, absoluteStartBeat);
            ctx.WantsViz = wantsViz;

            int spq = riff.SlicesPerQuarter > 0 ? riff.SlicesPerQuarter : 4;
            var notes = ToGeneratedNotes(riff, spq);

            for (int i = 0; i < chain.Instances.Count; i++)
            {
                var inst = chain.Instances[i];
                if (inst == null) continue;
                // Chaîne bypass-aware : NoteConstrainers[i].Bypass = true → skip cette étape.
                var refI = i < track.NoteConstrainers.Count ? track.NoteConstrainers[i] : null;
                if (refI != null && refI.Bypass) continue;
                try
                {
                    var filtered = inst.Filter(notes, ctx);
                    // On matérialise en List pour permettre au suivant d'itérer plusieurs fois.
                    notes = filtered == null ? new List<KotonGeneratedNote>() : new List<KotonGeneratedNote>(filtered);
                }
                catch
                {
                    // Un constrainer qui jette est BYPASSED (comportement défensif) — on garde
                    // la sortie de l'étape précédente pour ne pas casser la lecture.
                }
            }

            return FromGeneratedNotes(notes, spq, riff.LengthSlices);
        }

        /// <summary>Reconstruit le cache d'instances si les refs de la piste ont changé (ajout,
        /// suppression, réordonnancement, changement d'id). Compare par valeur simple (Id + StateBlob).</summary>
        static TrackChain EnsureChain(TimelineTrack track)
        {
            if (!_cache.TryGetValue(track, out var chain))
            {
                chain = new TrackChain();
                _cache.Add(track, chain);
            }
            if (SameRefs(chain.RefsSnapshot, track.NoteConstrainers)) return chain;

            // Recreation propre : dispose les anciennes, cree les nouvelles.
            foreach (var i in chain.Instances) try { i?.Dispose(); } catch { }
            chain.Instances.Clear();
            chain.RefsSnapshot = new List<NoteConstrainerRef>(track.NoteConstrainers);
            foreach (var r in chain.RefsSnapshot)
            {
                if (r == null || string.IsNullOrEmpty(r.Id)) { chain.Instances.Add(null); continue; }
                IKotonGeneratorConstrainer inst = null;
                try { inst = KotonPluginRegistry.InstantiateConstrainer(r.Id); } catch { }
                if (inst != null && !string.IsNullOrEmpty(r.StateBlob))
                {
                    try { inst.LoadState(Convert.FromBase64String(r.StateBlob)); } catch { }
                }
                chain.Instances.Add(inst);
            }
            return chain;
        }

        static bool SameRefs(List<NoteConstrainerRef> a, List<NoteConstrainerRef> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i]?.Id != b[i]?.Id) return false;
                if (a[i]?.StateBlob != b[i]?.StateBlob) return false;
            }
            return true;
        }

        /// <summary>Récupère l'instance vivante d'un constrainer sur une piste (utile côté UI pour
        /// afficher son éditeur). L'index est celui de la ref dans <see cref="TimelineTrack.NoteConstrainers"/>.
        /// Retourne null si hors bornes ou plugin absent.</summary>
        public static IKotonGeneratorConstrainer GetInstance(TimelineTrack track, int index)
        {
            if (track == null || index < 0) return null;
            var chain = EnsureChain(track);
            if (index >= chain.Instances.Count) return null;
            return chain.Instances[index];
        }

        /// <summary>Force la sauvegarde du StateBlob de chaque instance vivante vers les refs. À
        /// appeler juste avant de sérialiser le projet.</summary>
        public static void FlushStateBlobs(TimelineTrack track)
        {
            if (track?.NoteConstrainers == null) return;
            if (!_cache.TryGetValue(track, out var chain)) return;
            int n = Math.Min(chain.Instances.Count, track.NoteConstrainers.Count);
            for (int i = 0; i < n; i++)
            {
                var inst = chain.Instances[i];
                if (inst == null) continue;
                try
                {
                    var blob = inst.SaveState();
                    track.NoteConstrainers[i].StateBlob = (blob == null || blob.Length == 0) ? null : Convert.ToBase64String(blob);
                }
                catch { }
            }
        }

        // -----------------------------------------------------------------------------------------
        // Conversions Riff ↔ KotonGeneratedNote
        // -----------------------------------------------------------------------------------------

        static List<KotonGeneratedNote> ToGeneratedNotes(Riff riff, int spq)
        {
            var list = new List<KotonGeneratedNote>(riff.Notes.Count);
            double perSlice = 1.0 / spq;
            foreach (var n in riff.Notes)
            {
                list.Add(new KotonGeneratedNote
                {
                    StartBeat = n.Start * perSlice,
                    DurationBeats = n.Length * perSlice,
                    NotationDurationBeats = n.Length * perSlice,
                    MidiNote = n.Note + 12,   // convention Riff : Note 0 = MIDI 12 (cf. RiffNote.cs)
                    Velocity = 100,
                });
            }
            return list;
        }

        static Riff FromGeneratedNotes(List<KotonGeneratedNote> notes, int spq, int lengthSlices)
        {
            var r = new Riff
            {
                Notes = new List<RiffNote>(notes.Count),
                SlicesPerQuarter = spq,
                LengthSlices = lengthSlices,
            };
            foreach (var n in notes)
            {
                int start = (int)Math.Round(n.StartBeat * spq);
                int len = Math.Max(1, (int)Math.Round(n.DurationBeats * spq));
                int note = Math.Max(0, Math.Min(95, n.MidiNote - 12));
                var rn = new RiffNote(note, start, len);
                // Glissando : convertit MidiNote/beats → RiffNote 0..95/slices.
                if (n.GlideDurationBeats > 0 && n.GlideFromMidi != n.MidiNote)
                {
                    rn.GlideFromNote = Math.Max(0, Math.Min(95, n.GlideFromMidi - 12));
                    rn.GlideDurationSlices = Math.Max(1, (int)Math.Round(n.GlideDurationBeats * spq));
                }
                r.Notes.Add(rn);
            }
            return r;
        }
    }
}
