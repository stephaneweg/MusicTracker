using System;
using System.Collections.Generic;

namespace MusicTracker
{
    /// <summary>
    /// Démarrage COMMUN aux exécutables de la suite : Koton Studio (le séquenceur) et Koton Live (le rack
    /// temps réel). Filet à exceptions, langue de l'interface, scan des plugins Koton natifs, câblage des
    /// callbacks du SDK — tout ce qui doit être en place AVANT la première fenêtre, et qui vaut pour les
    /// deux applications.
    ///
    /// Vit ici plutôt que dans <c>App.OnStartup</c> parce qu'un second exécutable qui référence cet
    /// assembly n'hérite PAS de la classe <c>App</c> de Koton Studio : sans point d'entrée partagé, Koton
    /// Live démarrerait sans registre de plugins (donc sans effets ni instruments Koton) et sans garde-crash.
    /// </summary>
    public static class AppBootstrap
    {
        static bool _done;

        /// <summary>Idempotent : un second appel ne refait rien (les deux applications peuvent l'appeler
        /// sans se coordonner).</summary>
        public static void Initialize()
        {
            if (_done) return;
            _done = true;

            // Filet de sécurité en PREMIER : à partir d'ici toute exception non gérée est journalisée et
            // proposée en rapport, y compris celles levées par le reste de ce démarrage.
            Engine.BugReport.CrashGuard.Install();

            // Applique la langue sauvegardée AVANT la création de la première fenêtre, pour que son premier
            // rendu soit déjà dans la bonne langue (les liaisons {loc:Tr} lisent le gestionnaire au chargement).
            try { Localization.LocalizationManager.Instance.SetLanguageCode(AppSettings.Instance.Language); }
            catch { /* la localisation est best-effort ; elle ne doit jamais bloquer le démarrage */ }

            // Câble le callback ReportException du SDK vers CrashGuard — les plugins peuvent signaler une
            // exception attrapée défensivement (thread propriétaire, callback qui n'atteint pas les
            // gestionnaires globaux). Journal + dialogue de report, l'app continue.
            KotonStudio.Library.KotonHost.ReportException = (ex, source) =>
            {
                try { Engine.BugReport.CrashGuard.Report(ex, source); } catch { }
            };

            // Catalogue de presets : un magasin utilisateur unique, partagé par les deux exécutables et par
            // tous les types de plugins. Rangé dans le dossier ROAMING — un preset est minuscule et doit
            // suivre l'utilisateur d'une machine à l'autre, contrairement aux .ksl et au SoundFont.
            try { KotonStudio.Library.KotonPresetCatalog.Folder = AppPaths.Roaming("presets"); }
            catch { /* le repli du SDK (%AppData%\MusicTracker\presets) vise le même endroit */ }

            // Scanne TOUS les plugins Koton (.ksl) → listes statiques prêtes avant la 1re fenêtre. Un plugin
            // ajouté après le démarrage demande un Rescan explicite (bouton dans les menus).
            try { Engine.Timeline.Effects.KotonPluginRegistry.Initialize(null); }
            catch { /* le scan est protégé en interne ; ce catch = filet ultime */ }

            // Callbacks « listage / instanciation d'instrument » — utilisés par les plugins qui pilotent
            // d'autres instruments Koton (InstrumentMorph). Le registre vit dans MusicTracker, donc on passe
            // par ces délégués pour que le SDK n'ait pas à y accéder.
            KotonStudio.Library.KotonHost.ListInstruments = () =>
            {
                var lst = new List<KotonStudio.Library.KotonInstrumentDescriptor>();
                foreach (var i in Engine.Timeline.Effects.KotonPluginRegistry.Instruments)
                    lst.Add(new KotonStudio.Library.KotonInstrumentDescriptor
                    {
                        Id = i.Id, DisplayName = i.DisplayName, Category = i.Category
                    });
                return lst;
            };
            KotonStudio.Library.KotonHost.InstantiateInstrument = id =>
                Engine.Timeline.Effects.KotonPluginRegistry.InstantiateInstrument(id);
        }
    }
}
