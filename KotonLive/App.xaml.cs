using System.Windows;

namespace KotonLive
{
    /// <summary>
    /// Point d'entrée de Koton Live, le rack temps réel autonome : micro ou clavier MIDI en entrée,
    /// instrument (SoundFont / VSTi / plugin Koton) et chaîne d'effets, sortie carte son en WASAPI ou ASIO.
    ///
    /// L'exécutable ne contient AUCUNE logique : moteur et fenêtre vivent dans l'assembly de Koton Studio
    /// (<c>MusicTracker.Live</c>), ce qui garantit que le bouton « Live » du séquenceur et ce programme
    /// ouvrent exactement la même chose. Il faut donc que KotonLive.exe soit installé DANS le dossier de
    /// Koton Studio — c'est ce que fait le build (cf. la cible CopyNextToKotonStudio du .csproj), et c'est
    /// aussi ce qui donne accès à <c>plugins\</c>, <c>SoundFont\</c> et aux tables de langue.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Garde-crash, langue de l'interface, scan des plugins Koton : le même démarrage que le
            // séquenceur (sans quoi la liste des effets et instruments Koton serait vide).
            MusicTracker.AppBootstrap.Initialize();

            var window = new MusicTracker.Live.LiveWindow();
            MainWindow = window;
            // La fenêtre est le programme : sa fermeture termine le processus.
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
    }
}
