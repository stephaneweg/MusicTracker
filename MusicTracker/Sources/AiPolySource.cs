using System.Windows;
using MusicTracker.Dialogs;
using MusicTracker.Engine.AI;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Sources
{
    /// <summary>
    /// Source « Composer en polyrythmique (IA) ». Ouvre <see cref="AiPolyDialog"/> (dispatch multi-fournisseur via
    /// <see cref="AiProviders"/>) et, si l'utilisateur confirme, transforme la réponse en <see cref="TimelineDocument"/>
    /// autonome via <see cref="AiPolyPlacer.BuildFresh"/>. Le shell (MainWindow) l'ouvre alors sur un nouvel onglet,
    /// comme n'importe quelle autre source de projet.
    /// </summary>
    public sealed class AiPolySource : IProjectSource
    {
        public string Title => "IA (poly)";

        public TimelineDocument Produce(Window owner)
        {
            var dlg = new AiPolyDialog { Owner = owner };
            if (dlg.ShowDialog() != true || dlg.Result == null) return null;
            return AiPolyPlacer.BuildFresh(dlg.Result);
        }
    }
}
