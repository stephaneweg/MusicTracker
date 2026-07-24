using System.Windows;
using MusicTracker.Dialogs;
using MusicTracker.Engine.AI;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Sources
{
    /// <summary>
    /// Source: "Composer avec l'IA". Shows the AI compose dialog (which does the provider round-trip), then builds a
    /// fresh arrangement document from the parsed result via <see cref="AiArrangementPlacer.BuildFresh"/>.
    /// </summary>
    public sealed class AiComposeSource : IProjectSource
    {
        public string Title => "IA";

        public TimelineDocument Produce(Window owner)
        {
            var dlg = new AiComposeDialog { Owner = owner };
            if (dlg.ShowDialog() != true || dlg.Result == null) return null;
            return AiArrangementPlacer.BuildFresh(dlg.Result, dlg.FixNotes, dlg.ChordVoice);
        }
    }
}
