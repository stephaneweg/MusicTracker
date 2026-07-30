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
            public double Attack, Decay, Sustain, Release;
            public double Volume;
            public double LfoRate, LfoDepth;
        }

        internal static readonly IReadOnlyList<Preset> All = new List<Preset>
        {
            // Cloche = ratio inharmonique (3.5) + attack très court + release long + index moyen (partiels riches).
            new Preset { Name = "Bell",
                Ratio = 3.5, Index = 4.0,
                Attack = 0.001, Decay = 1.400, Sustain = 0.05, Release = 3.000,
                Volume = 0.75, LfoRate = 0.5, LfoDepth = 0.05 },

            // Electric Piano = ratio 1:1 (harmonique — corps de note), attack court + decay moyen (percussion),
            // sustain assez tenu (le e-piano garde du corps), index modéré.
            new Preset { Name = "Electric Piano",
                Ratio = 1.0, Index = 3.2,
                Attack = 0.003, Decay = 0.800, Sustain = 0.35, Release = 0.600,
                Volume = 0.75, LfoRate = 4.5, LfoDepth = 0.1 },

            // Bass = ratio 1:1 + index bas (fondamentale claire), attack rapide, release court (punchy).
            new Preset { Name = "Bass",
                Ratio = 1.0, Index = 1.8,
                Attack = 0.002, Decay = 0.300, Sustain = 0.6, Release = 0.150,
                Volume = 0.85, LfoRate = 3.0, LfoDepth = 0.0 },

            // Lead Square = ratio 2:1 + index élevé (approche un carré), sustain élevé (lead tenu).
            new Preset { Name = "Lead Square",
                Ratio = 2.0, Index = 6.5,
                Attack = 0.005, Decay = 0.200, Sustain = 0.85, Release = 0.200,
                Volume = 0.65, LfoRate = 5.0, LfoDepth = 0.15 },

            // Pluck = attack immédiat + decay court + sustain 0 (mono-shot type harpe/guitare).
            new Preset { Name = "Pluck",
                Ratio = 2.5, Index = 3.0,
                Attack = 0.001, Decay = 0.350, Sustain = 0.0, Release = 0.500,
                Volume = 0.75, LfoRate = 3.0, LfoDepth = 0.0 },

            // Brass = attack modéré (le brass a un swell), index élevé (partiels riches), sustain tenu.
            new Preset { Name = "Brass",
                Ratio = 1.0, Index = 5.0,
                Attack = 0.080, Decay = 0.300, Sustain = 0.7, Release = 0.350,
                Volume = 0.7, LfoRate = 5.5, LfoDepth = 0.08 },

            // Sci-Fi Pad = attack très long (nappe), ratio inharmonique, LFO fort (modulation évolutive).
            new Preset { Name = "Sci-Fi Pad",
                Ratio = 1.5, Index = 5.5,
                Attack = 0.900, Decay = 1.000, Sustain = 0.7, Release = 2.500,
                Volume = 0.6, LfoRate = 0.8, LfoDepth = 0.35 },

            // Wood Block = ratio inharmonique élevé, index très élevé (partiels aigus), decay ultra-court.
            new Preset { Name = "Wood Block",
                Ratio = 7.0, Index = 8.0,
                Attack = 0.001, Decay = 0.120, Sustain = 0.0, Release = 0.080,
                Volume = 0.85, LfoRate = 3.0, LfoDepth = 0.0 },
        };
    }
}
