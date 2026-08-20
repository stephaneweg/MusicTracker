using System.Collections.Generic;

namespace KotonPluginFmSynth
{
    /// <summary>
    /// Table de presets intégrés au FM synth. Chaque preset = un jeu de valeurs pour les 9 paramètres
    /// du plugin. Les valeurs sont réglées à l'oreille pour donner un caractère reconnaissable — pas
    /// une reproduction fidèle (le FM 2-op est très limité vs le DX7 6-op) mais des sons utilisables.
    ///
    /// **Rôle** : sélectionnable via le combo en haut de l'éditeur. Le plugin pose <c>_currentPreset</c>
    /// puis applique les valeurs sur ses <see cref="KotonStudio.Library.KotonParameter"/> (l'éditeur
    /// suit via <c>Changed</c>). La sauvegarde d'état inclut à la fois l'index du preset ET les
    /// valeurs actuelles — un preset modifié se recharge fidèlement.
    /// </summary>
    internal static class FmPresets
    {
        internal sealed class Preset
        {
            public string Name;
            public double Ratio;
            public double Index;
            /// <summary>ADSR times in MILLISECONDS (unité côté KotonParameter). Sustain reste sur 0..1.</summary>
            public double Attack, Decay, Sustain, Release;
            public double Volume;
            public double LfoRate, LfoDepth;

            /// <summary>Forme d'onde du modulator (défaut Sine — preserve le son des presets historiques).</summary>
            public FmWaveform ModWave = FmWaveform.Sine;

            /// <summary>Forme d'onde du carrier (défaut Sine — preserve le son des presets historiques).</summary>
            public FmWaveform CarWave = FmWaveform.Sine;

            /// <summary>Feedback 0..1 sur le modulator (auto-modulation de phase). Défaut 0 — les
            /// presets historiques n'en avaient pas.</summary>
            public double Feedback = 0.0;

            /// <summary>Algorithme : false = FM (modulator module carrier — le classique) ; true =
            /// Additif (les 2 opérateurs sortent en parallèle, sommés).</summary>
            public bool Additive = false;
        }

        internal static readonly IReadOnlyList<Preset> All = new List<Preset>
        {
            // Cloche = ratio inharmonique (3.5) + attack très court + release long + index moyen (partiels riches).
            new Preset { Name = "Bell",
                Ratio = 3.5, Index = 4.0,
                Attack = 1, Decay = 1400, Sustain = 0.05, Release = 3000,
                Volume = 0.75, LfoRate = 0.5, LfoDepth = 0.05 },

            // Electric Piano = ratio 1:1 (harmonique — corps de note), attack court + decay moyen (percussion),
            // sustain assez tenu (le e-piano garde du corps), index modéré.
            new Preset { Name = "Electric Piano",
                Ratio = 1.0, Index = 3.2,
                Attack = 3, Decay = 800, Sustain = 0.35, Release = 600,
                Volume = 0.75, LfoRate = 4.5, LfoDepth = 0.1 },

            // Bass = ratio 1:1 + index bas (fondamentale claire), attack rapide, release court (punchy).
            new Preset { Name = "Bass",
                Ratio = 1.0, Index = 1.8,
                Attack = 2, Decay = 300, Sustain = 0.6, Release = 150,
                Volume = 0.85, LfoRate = 3.0, LfoDepth = 0.0 },

            // Lead Square = ratio 2:1 + index élevé (approche un carré), sustain élevé (lead tenu).
            new Preset { Name = "Lead Square",
                Ratio = 2.0, Index = 6.5,
                Attack = 5, Decay = 200, Sustain = 0.85, Release = 200,
                Volume = 0.65, LfoRate = 5.0, LfoDepth = 0.15 },

            // Pluck = attack immédiat + decay court + sustain 0 (mono-shot type harpe/guitare).
            new Preset { Name = "Pluck",
                Ratio = 2.5, Index = 3.0,
                Attack = 1, Decay = 350, Sustain = 0.0, Release = 500,
                Volume = 0.75, LfoRate = 3.0, LfoDepth = 0.0 },

            // Brass = attack modéré (le brass a un swell), index élevé (partiels riches), sustain tenu.
            new Preset { Name = "Brass",
                Ratio = 1.0, Index = 5.0,
                Attack = 80, Decay = 300, Sustain = 0.7, Release = 350,
                Volume = 0.7, LfoRate = 5.5, LfoDepth = 0.08 },

            // Sci-Fi Pad = attack très long (nappe), ratio inharmonique, LFO fort (modulation évolutive).
            new Preset { Name = "Sci-Fi Pad",
                Ratio = 1.5, Index = 5.5,
                Attack = 900, Decay = 1000, Sustain = 0.7, Release = 2500,
                Volume = 0.6, LfoRate = 0.8, LfoDepth = 0.35 },

            // Wood Block = ratio inharmonique élevé, index très élevé (partiels aigus), decay ultra-court.
            new Preset { Name = "Wood Block",
                Ratio = 7.0, Index = 8.0,
                Attack = 1, Decay = 120, Sustain = 0.0, Release = 80,
                Volume = 0.85, LfoRate = 3.0, LfoDepth = 0.0 },

            // =============================================================================================
            // Presets qui exploitent les 6 formes d'onde + feedback + algorithme (introduits v1.1)
            // =============================================================================================

            // Square Lead = carrier square + attack court + index élevé sur mod sinus → lead brillant
            // agressif, façon synthé chip 80s. Volume un peu abaissé (le carré est très fort en RMS).
            new Preset { Name = "Square Lead",
                Ratio = 1.0, Index = 4.5,
                Attack = 2, Decay = 200, Sustain = 0.8, Release = 180,
                Volume = 0.55, LfoRate = 5.5, LfoDepth = 0.1,
                ModWave = FmWaveform.Sine, CarWave = FmWaveform.Square },

            // Triangle Pad = carrier triangle + mod sinus + attack long + release long → nappe douce,
            // moins brillante qu'un pad sinus mais avec plus de corps grâce au triangle.
            new Preset { Name = "Triangle Pad",
                Ratio = 2.0, Index = 2.5,
                Attack = 800, Decay = 800, Sustain = 0.75, Release = 2200,
                Volume = 0.6, LfoRate = 0.7, LfoDepth = 0.25,
                ModWave = FmWaveform.Sine, CarWave = FmWaveform.Triangle },

            // Saw Bass = carrier sawtooth + mod sinus + ratio 1:1 → basse riche façon synthé analo.
            // Feedback modéré ajoute du grain. Attack rapide, release moyen (basse "musclée").
            new Preset { Name = "Saw Bass",
                Ratio = 1.0, Index = 1.5,
                Attack = 2, Decay = 400, Sustain = 0.55, Release = 250,
                Volume = 0.7, LfoRate = 3.0, LfoDepth = 0.0,
                ModWave = FmWaveform.Sine, CarWave = FmWaveform.Sawtooth,
                Feedback = 0.35 },

            // Pulse Bell = carrier AbsSine (OPL waveform 2, doublement fréquentiel) + mod sinus,
            // index modéré et release long → cloche métallique brillante, très OPL2/AdLib.
            new Preset { Name = "Pulse Bell",
                Ratio = 2.5, Index = 3.5,
                Attack = 1, Decay = 900, Sustain = 0.15, Release = 2400,
                Volume = 0.7, LfoRate = 0.6, LfoDepth = 0.08,
                ModWave = FmWaveform.Sine, CarWave = FmWaveform.AbsSine,
                Feedback = 0.15 },

            // Retro Blip = carrier square + mod square + index faible → double-carré, harmoniques
            // intermodulées → son 8-bit "jeu vidéo" typique. Envelope courte pour un "blip".
            new Preset { Name = "Retro Blip",
                Ratio = 2.0, Index = 1.8,
                Attack = 1, Decay = 180, Sustain = 0.0, Release = 100,
                Volume = 0.5, LfoRate = 3.0, LfoDepth = 0.0,
                ModWave = FmWaveform.Square, CarWave = FmWaveform.Square },

            // Chip Arp = carrier AbsSine + mod triangle + release très court → petites notes
            // percussives idéales pour un arpège rapide. Feedback léger = grain "chip".
            new Preset { Name = "Chip Arp",
                Ratio = 3.0, Index = 2.2,
                Attack = 1, Decay = 100, Sustain = 0.2, Release = 90,
                Volume = 0.6, LfoRate = 4.0, LfoDepth = 0.0,
                ModWave = FmWaveform.Triangle, CarWave = FmWaveform.AbsSine,
                Feedback = 0.25 },

            // Additive Organ = 2 opérateurs sinus en parallèle (mode additif) + ratio 2:1 → orgue
            // simple à 2 harmoniques. Sustain plein, release moyen. Le mode additif fait apparaître
            // le modulator comme un vrai 2e oscillateur audible.
            new Preset { Name = "Additive Organ",
                Ratio = 2.0, Index = 5.0,
                Attack = 20, Decay = 100, Sustain = 0.9, Release = 300,
                Volume = 0.7, LfoRate = 4.5, LfoDepth = 0.05,
                ModWave = FmWaveform.Sine, CarWave = FmWaveform.Sine,
                Additive = true },

            // =============================================================================================
            // Banque instruments acoustiques (v1.3) — 44 presets recréés from scratch à partir des
            // recettes FM 2-op classiques (DX7/TX81Z-style), remplacent l'import brut .FMI qui
            // produisait du bruit blanc (mapping OPL2 → notre synth trop lossy : feedback OPL 7 mappé
            // à 1.0 auto-oscillait, index 8 aliaisait). Les noms sont conservés pour la compatibilité
            // des projets sauvegardés. Recettes appliquées :
            //   - Feedback max 0.2 (plus, ça part en distortion/bruit sur notre implémentation).
            //   - Index max 5.0 (au-delà, partiels au-dessus de Nyquist → bruit haut du spectre).
            //   - Ratios entiers (1, 2, 3, 5) pour timbres harmoniques ; inharmoniques (3.5, 7) pour
            //     percussions métalliques (cloches, marimba, xylo, gamelan).
            //   - ADSR par famille : attack court pour pincés (~2ms) / long pour archets (~150ms) /
            //     moyen pour cuivres (~80ms), sustain plein pour tenus / bas pour percussifs.
            //   - LFO léger (rate 5Hz, depth 0.06-0.12) pour instruments qui "vibrent" (vents, archets).
            // =============================================================================================

            // --- Bois (sustained, attack rapide, harmoniques impaires ou nasillard) ---
            new Preset { Name = "Flute",
                Ratio = 1.0, Index = 1.5, Attack = 80, Decay = 100, Sustain = 0.85, Release = 200,
                Volume = 0.7, LfoRate = 5.5, LfoDepth = 0.06 },

            new Preset { Name = "Piccolo",
                Ratio = 1.0, Index = 2.0, Attack = 60, Decay = 100, Sustain = 0.85, Release = 200,
                Volume = 0.65, LfoRate = 5.5, LfoDepth = 0.06 },

            new Preset { Name = "Clarinet",
                Ratio = 3.0, Index = 3.5, Attack = 40, Decay = 100, Sustain = 0.85, Release = 200,
                Volume = 0.7, LfoRate = 5.0, LfoDepth = 0.05 },

            new Preset { Name = "Oboe",
                Ratio = 3.0, Index = 4.5, Attack = 40, Decay = 100, Sustain = 0.85, Release = 200,
                Volume = 0.7, LfoRate = 5.0, LfoDepth = 0.05, Feedback = 0.1 },

            new Preset { Name = "English Horn",
                Ratio = 3.0, Index = 4.0, Attack = 60, Decay = 150, Sustain = 0.8, Release = 250,
                Volume = 0.7, LfoRate = 5.0, LfoDepth = 0.08, Feedback = 0.1 },

            new Preset { Name = "Bassoon",
                Ratio = 1.0, Index = 4.0, Attack = 60, Decay = 200, Sustain = 0.8, Release = 250,
                Volume = 0.7, LfoRate = 5.0, LfoDepth = 0.08, Feedback = 0.15 },

            new Preset { Name = "Saxophone",
                Ratio = 2.0, Index = 4.0, Attack = 30, Decay = 150, Sustain = 0.85, Release = 250,
                Volume = 0.7, LfoRate = 5.0, LfoDepth = 0.08, Feedback = 0.15 },

            new Preset { Name = "Tenor Sax",
                Ratio = 2.0, Index = 4.0, Attack = 40, Decay = 150, Sustain = 0.85, Release = 250,
                Volume = 0.75, LfoRate = 5.0, LfoDepth = 0.08, Feedback = 0.15 },

            new Preset { Name = "Harmonica",
                Ratio = 1.0, Index = 3.0, Attack = 60, Decay = 150, Sustain = 0.85, Release = 250,
                Volume = 0.7, LfoRate = 5.0, LfoDepth = 0.1, Feedback = 0.1 },

            // --- Cuivres (attack modéré, index élevé, sustain plein) ---
            new Preset { Name = "Trumpet",
                Ratio = 1.0, Index = 5.0, Attack = 60, Decay = 150, Sustain = 0.75, Release = 200,
                Volume = 0.7, LfoRate = 5.5, LfoDepth = 0.06, Feedback = 0.15 },

            new Preset { Name = "Trombone",
                Ratio = 1.0, Index = 4.0, Attack = 100, Decay = 200, Sustain = 0.8, Release = 250,
                Volume = 0.75, LfoRate = 5.0, LfoDepth = 0.06, Feedback = 0.1 },

            new Preset { Name = "French Horn",
                Ratio = 1.0, Index = 3.5, Attack = 100, Decay = 200, Sustain = 0.85, Release = 300,
                Volume = 0.7, LfoRate = 5.0, LfoDepth = 0.06, Feedback = 0.05 },

            new Preset { Name = "Tuba",
                Ratio = 0.5, Index = 3.0, Attack = 100, Decay = 200, Sustain = 0.85, Release = 300,
                Volume = 0.8, LfoRate = 4.5, LfoDepth = 0.05, Feedback = 0.1 },

            new Preset { Name = "OPL Brass",
                Ratio = 1.0, Index = 4.0, Attack = 80, Decay = 200, Sustain = 0.75, Release = 250,
                Volume = 0.7, LfoRate = 5.5, LfoDepth = 0.08, Feedback = 0.15 },

            new Preset { Name = "Soft Brass",
                Ratio = 1.0, Index = 3.0, Attack = 120, Decay = 200, Sustain = 0.8, Release = 300,
                Volume = 0.7, LfoRate = 5.0, LfoDepth = 0.06, Feedback = 0.1 },

            // --- Cordes frottées (attack lente pour swell, sustain plein, LFO = vibrato) ---
            new Preset { Name = "Violin",
                Ratio = 1.0, Index = 3.5, Attack = 200, Decay = 200, Sustain = 0.9, Release = 300,
                Volume = 0.75, LfoRate = 5.5, LfoDepth = 0.12, Feedback = 0.1 },

            new Preset { Name = "Alto Viola",
                Ratio = 1.0, Index = 3.0, Attack = 150, Decay = 200, Sustain = 0.85, Release = 350,
                Volume = 0.7, LfoRate = 5.0, LfoDepth = 0.12, Feedback = 0.1 },

            new Preset { Name = "Cello",
                Ratio = 1.0, Index = 3.0, Attack = 120, Decay = 200, Sustain = 0.85, Release = 400,
                Volume = 0.75, LfoRate = 5.0, LfoDepth = 0.1, Feedback = 0.1 },

            new Preset { Name = "Contrabass",
                Ratio = 0.5, Index = 3.0, Attack = 80, Decay = 200, Sustain = 0.85, Release = 350,
                Volume = 0.8, LfoRate = 4.5, LfoDepth = 0.08, Feedback = 0.1 },

            new Preset { Name = "Strings",
                Ratio = 1.0, Index = 3.0, Attack = 400, Decay = 500, Sustain = 0.85, Release = 600,
                Volume = 0.7, LfoRate = 4.5, LfoDepth = 0.08, Feedback = 0.05 },

            // --- Cordes pincées/frappées (attack immédiate, decay long, sustain bas) ---
            new Preset { Name = "OPL Piano",
                Ratio = 1.0, Index = 3.5, Attack = 3, Decay = 900, Sustain = 0.25, Release = 500,
                Volume = 0.75, LfoRate = 3.0, LfoDepth = 0.0, Feedback = 0.05 },

            new Preset { Name = "OPL Electric Piano",
                Ratio = 1.0, Index = 3.0, Attack = 3, Decay = 800, Sustain = 0.3, Release = 600,
                Volume = 0.75, LfoRate = 4.5, LfoDepth = 0.08, Feedback = 0.1 },

            new Preset { Name = "Harpsichord",
                Ratio = 2.0, Index = 4.0, Attack = 2, Decay = 400, Sustain = 0.15, Release = 300,
                Volume = 0.75, LfoRate = 3.0, LfoDepth = 0.0, Feedback = 0.15 },

            new Preset { Name = "Guitar",
                Ratio = 1.0, Index = 3.0, Attack = 2, Decay = 500, Sustain = 0.2, Release = 400,
                Volume = 0.75, LfoRate = 3.0, LfoDepth = 0.0, Feedback = 0.1 },

            new Preset { Name = "Electric Guitar",
                Ratio = 1.0, Index = 4.0, Attack = 2, Decay = 400, Sustain = 0.3, Release = 400,
                Volume = 0.7, LfoRate = 3.0, LfoDepth = 0.0, Feedback = 0.2 },

            new Preset { Name = "Banjo",
                Ratio = 2.0, Index = 4.0, Attack = 1, Decay = 250, Sustain = 0.05, Release = 300,
                Volume = 0.75, LfoRate = 3.0, LfoDepth = 0.0, Feedback = 0.2 },

            new Preset { Name = "Sitar",
                Ratio = 1.5, Index = 4.5, Attack = 3, Decay = 800, Sustain = 0.15, Release = 1000,
                Volume = 0.7, LfoRate = 5.0, LfoDepth = 0.15, Feedback = 0.15 },

            new Preset { Name = "Harp",
                Ratio = 1.0, Index = 2.5, Attack = 3, Decay = 1200, Sustain = 0.1, Release = 1500,
                Volume = 0.75, LfoRate = 3.0, LfoDepth = 0.0 },

            new Preset { Name = "Bass Harp",
                Ratio = 0.5, Index = 2.5, Attack = 3, Decay = 800, Sustain = 0.15, Release = 1200,
                Volume = 0.75, LfoRate = 3.0, LfoDepth = 0.0 },

            // --- Basses (ratio 0.5-1, attack rapide, punch) ---
            new Preset { Name = "Pop Bass",
                Ratio = 0.5, Index = 3.0, Attack = 3, Decay = 500, Sustain = 0.4, Release = 200,
                Volume = 0.8, LfoRate = 3.0, LfoDepth = 0.0, Feedback = 0.15 },

            new Preset { Name = "Slap Bass",
                Ratio = 1.0, Index = 5.0, Attack = 2, Decay = 200, Sustain = 0.4, Release = 200,
                Volume = 0.8, LfoRate = 3.0, LfoDepth = 0.0, Feedback = 0.2 },

            // --- Percussion mélodique (ratio inharmonique, attack immédiat, sustain bas) ---
            new Preset { Name = "Bells",
                Ratio = 3.5, Index = 4.0, Attack = 1, Decay = 1500, Sustain = 0.05, Release = 3000,
                Volume = 0.7, LfoRate = 0.5, LfoDepth = 0.05 },

            new Preset { Name = "Bell Piano",
                Ratio = 2.0, Index = 3.5, Attack = 2, Decay = 1000, Sustain = 0.1, Release = 1500,
                Volume = 0.75, LfoRate = 1.0, LfoDepth = 0.05, Feedback = 0.05 },

            new Preset { Name = "Celesta",
                Ratio = 3.0, Index = 3.0, Attack = 1, Decay = 500, Sustain = 0.1, Release = 800,
                Volume = 0.7, LfoRate = 3.0, LfoDepth = 0.0, Additive = true },

            new Preset { Name = "Marimba",
                Ratio = 5.0, Index = 4.0, Attack = 1, Decay = 300, Sustain = 0.1, Release = 500,
                Volume = 0.75, LfoRate = 3.0, LfoDepth = 0.0 },

            new Preset { Name = "Xylophone",
                Ratio = 7.0, Index = 4.0, Attack = 1, Decay = 150, Sustain = 0.0, Release = 200,
                Volume = 0.75, LfoRate = 3.0, LfoDepth = 0.0 },

            new Preset { Name = "Vibraphone",
                Ratio = 3.0, Index = 3.0, Attack = 1, Decay = 1500, Sustain = 0.15, Release = 1500,
                Volume = 0.7, LfoRate = 6.0, LfoDepth = 0.15 },

            new Preset { Name = "Javanese Gamelan",
                Ratio = 3.5, Index = 4.5, Attack = 1, Decay = 800, Sustain = 0.1, Release = 1200,
                Volume = 0.7, LfoRate = 3.0, LfoDepth = 0.0, Feedback = 0.1 },

            // --- Orgues (attack immédiat, sustain plein) ---
            new Preset { Name = "OPL Organ",
                Ratio = 2.0, Index = 3.0, Attack = 5, Decay = 100, Sustain = 0.9, Release = 150,
                Volume = 0.7, LfoRate = 4.5, LfoDepth = 0.03, Additive = true },

            new Preset { Name = "Pipes",
                Ratio = 3.0, Index = 3.0, Attack = 80, Decay = 200, Sustain = 0.85, Release = 300,
                Volume = 0.7, LfoRate = 4.5, LfoDepth = 0.05 },

            new Preset { Name = "Accordion",
                Ratio = 1.0, Index = 3.5, Attack = 30, Decay = 150, Sustain = 0.85, Release = 150,
                Volume = 0.7, LfoRate = 5.5, LfoDepth = 0.1, Feedback = 0.1 },

            new Preset { Name = "Bagpipes",
                Ratio = 3.0, Index = 4.5, Attack = 80, Decay = 100, Sustain = 0.9, Release = 100,
                Volume = 0.6, LfoRate = 6.0, LfoDepth = 0.08, Feedback = 0.15 },

            // --- Synthés polyvalents ---
            new Preset { Name = "Synth 1",
                Ratio = 1.0, Index = 4.0, Attack = 5, Decay = 400, Sustain = 0.7, Release = 300,
                Volume = 0.65, LfoRate = 4.0, LfoDepth = 0.1, Feedback = 0.15 },

            new Preset { Name = "Synth 2",
                Ratio = 2.0, Index = 4.5, Attack = 5, Decay = 300, Sustain = 0.6, Release = 400,
                Volume = 0.65, LfoRate = 5.0, LfoDepth = 0.15, Feedback = 0.2 },
        };
    }
}
