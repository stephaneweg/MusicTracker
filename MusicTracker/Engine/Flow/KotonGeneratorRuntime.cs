using System;
using System.Collections.Generic;
using KotonStudio.Library;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Engine.Flow
{
    /// <summary>
    /// Pontage entre un <see cref="KotonGeneratorModule"/> (module timeline sérialisable) et le
    /// pipeline audio interne (Riff = liste de notes à slices/quarter fixé). Regroupé ici pour que
    /// TimelinePlayer, ScoreModel et TimelineImporter passent tous par le MÊME chemin — sinon les
    /// notes rendues divergeraient entre l'audio, la partition et l'export MIDI (le classique 3-resolvers
    /// à garder synchronisés, cf. MEMORY.md « New module → 3 resolvers »).
    ///
    /// **Instanciation paresseuse** : la 1re fois qu'un renderer touche le module, on instancie le
    /// plugin via le registre et on lui applique <see cref="KotonGeneratorModule.GeneratorState"/>.
    /// Les fois suivantes, on réutilise <see cref="KotonGeneratorModule.RuntimeInstance"/> — ce qui
    /// permet aussi à l'éditeur du panneau du bas d'obtenir la MÊME instance vivante que le player
    /// (bouger un slider affecte le prochain flatten audio, sans passage par un blob intermédiaire).
    ///
    /// **Résolution silencieuse en cas d'échec** : un id inconnu, une instance nulle ou une exception
    /// pendant RenderNotes = <c>null</c> retourné — le renderer traite comme "pas de notes" (piste
    /// silencieuse sur cette portion). Un plugin cassé ne doit pas faire tomber la lecture ; l'UI
    /// affiche un badge d'avertissement quand l'id est absent du registre.
    /// </summary>
    public static class KotonGeneratorRuntime
    {
        /// <summary>Résolution canonique en slices/quarter — la même que le pipeline audio interne
        /// utilise (Spb=24 côté TimelinePlayer, DrumPattern.SlicesPerQuarter=6 côté drums). On
        /// prend 24 pour couvrir les triplets exactement (24 = 8 × 3 = 6 × 4).</summary>
        public const int SlicesPerQuarter = 24;

        /// <summary>Obtient l'instance vivante du générateur (la crée si absente + applique
        /// <see cref="KotonGeneratorModule.GeneratorState"/>). <c>null</c> si l'id est inconnu du
        /// registre — le renderer laisse la portion silencieuse.</summary>
        public static IKotonGenerator EnsureInstance(KotonGeneratorModule module)
        {
            if (module == null) return null;
            if (module.RuntimeInstance != null) return module.RuntimeInstance;
            var inst = Engine.Timeline.Effects.KotonPluginRegistry.InstantiateGenerator(module.GeneratorId);
            if (inst == null) return null;
            try
            {
                if (module.GeneratorState != null && module.GeneratorState.Length > 0)
                    inst.LoadState(module.GeneratorState);
            }
            catch { /* blob incompatible — laisser le plugin dans son état par défaut */ }
            // La durée du bloc est la source de vérité (le user peut redimensionner depuis la
            // timeline). On la pousse aussi au plugin pour qu'il puisse afficher/utiliser cette
            // valeur dans son éditeur.
            try { inst.DurationBeats = module.DurationBeats; } catch { }
            module.RuntimeInstance = inst;
            return inst;
        }

        // Pitch class de la tonique par lettre 0..6 = Do..Si — parallèle à LetterPcs dans KeySig.
        static readonly int[] LetterPcs = { 0, 2, 4, 5, 7, 9, 11 };

        /// <summary>Construit un <see cref="KotonRenderContext"/> depuis un projet Koton — passer aux
        /// générateurs qui en ont besoin (tonalité, tempo, signature).</summary>
        /// <param name="absoluteStartBeat">Position absolue du bloc dans le projet (beats). Vaut 0 pour
        /// la preview (bloc pas encore posé) — les résolveurs de KotonHost.GetChordAt retourneront
        /// alors ce qui existe au tout début du projet, ou null. Un générateur harmonique-conscient
        /// utilisera <c>ctx.BlockStartBeat + t</c> pour interroger l'accord courant.</param>
        public static KotonRenderContext ContextFor(TimelineProject project, double absoluteStartBeat = 0)
        {
            if (project == null)
                return new KotonRenderContext { Tonic = 0, IsMajor = true, Tempo = 120, TimeSigNum = 4, TimeSigDen = 4, BlockStartBeat = absoluteStartBeat };
            var key = project.Key ?? new Engine.Score.KeySignature();
            int tonicPc = ((LetterPcs[Math.Max(0, Math.Min(6, key.TonicLetter))] + key.Accidental) % 12 + 12) % 12;
            return new KotonRenderContext
            {
                Tonic = tonicPc,
                IsMajor = key.Mode == 0,
                Tempo = project.MainBpm,
                TimeSigNum = Math.Max(1, project.TimeSigNum),
                TimeSigDen = Math.Max(1, project.TimeSigDen),
                BlockStartBeat = absoluteStartBeat,
                PickupBeats = project.PickupBeats,
            };
        }

        /// <summary>Rend le module en <see cref="Riff"/> canonique (24 slices/quarter, notes indexées
        /// depuis 12 = C0 pour rester cohérent avec le reste du pipeline — cf.
        /// TimelineImporter.FlattenLeaf qui fait le décalage +12 aussi). Les notes hors [0..127]
        /// sont ignorées ; les vélocités hors [1..127] sont clampées. Un module dont l'id est inconnu
        /// ou dont RenderNotes jette renvoie <c>null</c>.</summary>
        public static Riff RenderRiff(KotonGeneratorModule module, TimelineProject project, double absoluteStartBeat = 0, bool forNotation = false)
        {
            var inst = EnsureInstance(module);
            if (inst == null) return null;

            double duration = Math.Max(0.25, module.DurationBeats);
            IEnumerable<KotonGeneratedNote> notes;
            try { notes = inst.RenderNotes(0, duration, ContextFor(project, absoluteStartBeat)); }
            catch { return null; }
            if (notes == null) return null;

            var riff = new Riff
            {
                Name = "koton",
                SlicesPerQuarter = SlicesPerQuarter,
                LengthSlices = (int)Math.Round(duration * SlicesPerQuarter),
                Notes = new List<RiffNote>(),
            };
            foreach (var n in notes)
            {
                int midi = n.MidiNote;
                if (midi < 0 || midi > 127) continue;
                // Le pipeline interne indexe les notes à partir de la clé MIDI 12 (voir
                // TimelineImporter.FlattenLeaf : n.Pitch = n.Note + 12). RiffNote.Note ∈ 0..95 = MIDI 12..107.
                int noteIdx = midi - 12;
                if (noteIdx < 0 || noteIdx > 95) continue;

                double startBeat = Math.Max(0, n.StartBeat);
                // Choix de la durée selon le contexte : audio → DurationBeats (avec articulation) ;
                // partition → NotationDurationBeats si le plugin l'a renseignée (durée logique
                // sans gate, ex. une croche entière au lieu d'un staccato de 15%). Un plugin qui ne
                // fait pas la distinction laisse NotationDurationBeats = 0 → fallback sur DurationBeats.
                double lenBeats = forNotation && n.NotationDurationBeats > 0
                    ? n.NotationDurationBeats
                    : Math.Max(0, n.DurationBeats);
                int startSlice = (int)Math.Round(startBeat * SlicesPerQuarter);
                int lenSlices = Math.Max(1, (int)Math.Round(lenBeats * SlicesPerQuarter));
                // Filtrage : on garde uniquement les notes qui commencent dans la fenêtre du module.
                // Une note qui commencerait exactement à la fin est aussi ignorée (le renderer inclurait
                // sinon un événement note-on immédiatement suivi d'un note-off au frame suivant, silence
                // audible mais inutile). Un note à peine avant la fin garde sa durée intégrale — l'engagement
                // dépasse le module (ce qui est le comportement RiffPlayer standard pour un note en fin).
                if (startSlice >= riff.LengthSlices) continue;

                // La vélocité fournie par le plugin n'est PAS encore propagée à l'audio : RiffNote n'a
                // pas de champ Velocity, et le player applique MetricVelocity (position métrique) à chaque
                // note. C'est cohérent avec le comportement des autres générateurs internes
                // (PatternGenerator, DrumPattern) qui n'attachent pas non plus de vélocité aux notes.
                // Un enrichissement futur pourrait ajouter le champ à RiffNote et le lire dans
                // TimelinePlayer.PlaceRiffNotes ; en attendant on ignore.
                riff.Notes.Add(new RiffNote(noteIdx, startSlice, lenSlices));
            }
            return riff;
        }
    }
}
