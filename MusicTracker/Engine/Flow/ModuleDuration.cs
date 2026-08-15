using System;

namespace MusicTracker.Engine.Flow
{
    /// <summary>Duration of a flow module in BEATS (quarter notes) — used by the timeline layout
    /// (box width/position). 0 for non-timed modules.</summary>
    public static class ModuleDuration
    {
        public static double Beats(FlowModule m, Func<Guid, Riff> resolveRiff)
        {
            switch (m)
            {
                case PlayRiffModule pr:
                    {
                        var r = resolveRiff?.Invoke(pr.RiffId);
                        return (r != null && r.SlicesPerQuarter > 0 && r.Slices != null)
                            ? (double)r.Slices.Length / r.SlicesPerQuarter : 4;
                    }
                case PatternGeneratorModule pg:
                    if (pg.Style == PatternGenerator.CustomStyle && pg.CustomSlices != null && pg.CustomSlices.Length > 0 && pg.CustomSlicesPerQuarter > 0)
                        return (double)pg.CustomSlices.Length / pg.CustomSlicesPerQuarter * pg.Repeats;
                    return (double)pg.BeatsPerBar * pg.Repeats;
                case DrumPatternModule dp:
                    if (dp.Style == DrumPattern.CustomStyle && dp.CustomSlices != null && dp.CustomSlices.Length > 0 && dp.CustomSlicesPerQuarter > 0)
                        return (double)dp.CustomSlices.Length / dp.CustomSlicesPerQuarter * dp.Repeats;
                    return (double)dp.BeatsPerBar * dp.Repeats;
                case PolyDrumModule pd:
                    return PolyDrum.TotalBeats(pd);        // dérivé du cycle des couches × Repeats
                case CadenceModule cm:
                    return (double)cm.BeatsPerBar * Math.Max(1, cm.Chords?.Count ?? 0); // one cell per chord
                case MelodicLineModule ml:
                    return Math.Max(1, ml.BeatsPerBar);                                  // total beats of the line
                case MelodicPolyModule mp:
                    return MelodicEuclid.TotalBeats(mp);
                case PolyChordModule pc:
                    return PolyChord.TotalBeats(pc);
                case ChordArticulationModule ca:
                    return Timeline.ChordArticulation.TotalBeats(ca);   // longueur LIBRE, indépendante des accords couverts
                case KotonGeneratorModule kg:
                    // La durée est PORTÉE par le module (pas par le plugin) — les redimensionnements
                    // depuis la timeline et l'éditeur passent tous par KotonGeneratorModule.DurationBeats.
                    // Un floor à 0.25 est déjà appliqué côté setter.
                    return kg.DurationBeats;
                default:
                    return 4;
            }
        }
    }
}
