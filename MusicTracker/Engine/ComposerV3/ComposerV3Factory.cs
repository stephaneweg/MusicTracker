namespace MusicTracker.Engine.ComposerV3
{
    /// <summary>Picks the ComposerV3 emitter for a corpus model by its file name. Replaces the old
    /// ComposerV2Runtime.CreateFor. A model "without a particular style" falls back to <see cref="BaseComposerV3"/>.</summary>
    public static class ComposerV3Factory
    {
        public static BaseComposerV3 For(string modelFile)
        {
            string low = (modelFile ?? "").ToLowerInvariant();
            if (low.Contains("math")) return new MathComposerV3();   // model-free: melody from formulas/fractals
            if (low.Contains("vivaldi")) return new VivaldiComposerV3();
            if (low.Contains("clavier")) return new BachClavierComposerV3();
            if (low.Contains("bach")) return new BachComposerV3();
            if (low.Contains("ghibli") || low.Contains("hisaishi")) return new GhibliComposerV3();
            return new BaseComposerV3();   // generic model → the base melodic engine
        }
    }
}
