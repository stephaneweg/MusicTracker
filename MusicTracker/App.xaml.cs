using System;
using System.Windows;

namespace MusicTracker
{
    /// <summary>
    /// Point d'entrée de Koton Studio (le séquenceur). Le démarrage lui-même — garde-crash, langue, scan des
    /// plugins Koton — vit dans <see cref="AppBootstrap"/>, partagé avec l'exécutable Koton Live ; ne reste
    /// ici que ce qui est propre au séquenceur.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppBootstrap.Initialize();

            // Nettoyage du dossier de staging portable (.update/ à côté de l'exe) : présent = on vient d'être
            // relancés par KotonStudioUpdater après une MàJ portable, donc ce dossier peut partir. Fait ICI parce
            // qu'à ce stade rien n'a encore ouvert de fichier dans .update/, et parce que c'est le seul moment sûr
            // (l'updater a nécessairement quitté puisqu'il nous a lancés). Propre au séquenceur : c'est lui que
            // l'updater relance.
            try { Engine.Update.UpdateChecker.CleanupPortableStaging(); }
            catch { /* best-effort ; on retente au prochain boot */ }

            base.OnStartup(e);
        }
    }
}
