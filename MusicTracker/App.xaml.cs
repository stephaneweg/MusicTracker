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

            // Apply the saved UI language BEFORE the StartupUri window is created, so its first render is
            // already in the right language (the {loc:Tr} bindings read LocalizationManager at load time).
            try { Localization.LocalizationManager.Instance.SetLanguageCode(AppSettings.Instance.Language); }
            catch { /* localization is best-effort; never block startup */ }
            base.OnStartup(e);
        }
    }
}
