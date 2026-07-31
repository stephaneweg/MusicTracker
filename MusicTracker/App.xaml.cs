using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace MusicTracker
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Filet de sécurité en PREMIER : à partir d'ici toute exception non gérée est journalisée et
            // proposée en rapport, y compris celles levées par le reste de ce démarrage.
            Engine.BugReport.CrashGuard.Install();

            // Nettoyage du dossier de staging portable (.update/ à côté de l'exe) : présent = on vient d'être
            // relancés par KotonStudioUpdater après une MàJ portable, donc ce dossier peut partir. Fait ICI parce
            // qu'à ce stade rien n'a encore ouvert de fichier dans .update/, et parce que c'est le seul moment sûr
            // (l'updater a nécessairement quitté puisqu'il nous a lancés).
            try { Engine.Update.UpdateChecker.CleanupPortableStaging(); }
            catch { /* best-effort ; on retente au prochain boot */ }

            // Apply the saved UI language BEFORE the StartupUri window is created, so its first render is
            // already in the right language (the {loc:Tr} bindings read LocalizationManager at load time).
            try { Localization.LocalizationManager.Instance.SetLanguageCode(AppSettings.Instance.Language); }
            catch { /* localization is best-effort; never block startup */ }

            // Câble le callback ReportException du SDK vers CrashGuard — les plugins peuvent
            // signaler une exception attrapée défensivement (thread propriétaire, callback qui
            // n'atteint pas les gestionnaires globaux). Journal + dialogue de report, l'app continue.
            KotonStudio.Library.KotonHost.ReportException = (ex, source) =>
            {
                try { Engine.BugReport.CrashGuard.Report(ex, source); } catch { }
            };

            // Scanne TOUS les plugins Koton (.ksl) au démarrage → listes statiques prêtes AVANT
            // que la 1re fenêtre s'affiche. Le menu Insérer > Générateur Koton (et les sélecteurs
            // d'instrument) lisent ces listes directement, sans scan lazy. Un plugin ajouté après
            // le démarrage nécessite un Rescan explicite (bouton dans le menu) — c'est le compromis
            // pour éviter les effets de bord d'une instanciation-au-scan.
            try
            {
                // TODO : quand le settings dialog exposera KotonPluginFolders (3 listes par type),
                // passer AppSettings.Instance.KotonPluginFolders ici. Pour l'instant, dossiers défaut
                // uniquement (bundle + %LocalAppData%\MusicTracker\plugins).
                Engine.Timeline.Effects.KotonPluginRegistry.Initialize(null);
            }
            catch { /* le scan est protégé en interne ; ce catch = filet ultime */ }

            base.OnStartup(e);
        }
    }
}
