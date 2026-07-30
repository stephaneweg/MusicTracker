using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Cherche des plugins VST2 (fichiers .dll) dans les dossiers Steinberg standards. Résultat mis en
    /// cache pour la session (un premier accès scanne, les suivants sont instantanés). L'utilisateur peut
    /// re-scanner explicitement via <see cref="ForceRescan"/>.
    ///
    /// Le scan ne CHARGE PAS les DLL (juste <see cref="Directory.EnumerateFiles"/>) — pas de risque de
    /// charger un plugin buggé au démarrage. La validation « est-ce vraiment un VST ? » n'a lieu qu'au
    /// moment de l'instanciation (VST.NET jette si le .dll ne répond pas au contrat).
    /// </summary>
    public static class VstPluginScanner
    {
        static List<PluginEntry> _cache;
        static readonly object _lock = new object();

        /// <summary>Dossiers Steinberg standards, testés dans l'ordre. Un futur écran de préférences pourra en ajouter.</summary>
        public static readonly string[] DefaultFolders = new[]
        {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\VstPlugins"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Steinberg\VstPlugins"),
            Environment.ExpandEnvironmentVariables(@"%CommonProgramFiles%\VST2"),
            Environment.ExpandEnvironmentVariables(@"%CommonProgramFiles%\Steinberg\VST2"),
        };

        /// <summary>Une entrée dans le catalogue — juste le chemin + nom de fichier (utilisé pour l'affichage).</summary>
        public class PluginEntry
        {
            public string Path;
            public string DisplayName;
        }

        /// <summary>Récupère la liste courante (scanne si nécessaire). Filtre les doublons par chemin (case-insensitive).</summary>
        public static List<PluginEntry> GetPlugins(IEnumerable<string> extraFolders = null)
        {
            lock (_lock)
            {
                if (_cache != null) return _cache;
                _cache = Scan(extraFolders);
                return _cache;
            }
        }

        /// <summary>Force un re-scan (utile après ajout d'un dossier personnalisé ou installation d'un nouveau VST).</summary>
        public static List<PluginEntry> ForceRescan(IEnumerable<string> extraFolders = null)
        {
            lock (_lock)
            {
                _cache = Scan(extraFolders);
                return _cache;
            }
        }

        static List<PluginEntry> Scan(IEnumerable<string> extraFolders)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<PluginEntry>();
            var folders = new List<string>();
            folders.AddRange(DefaultFolders);
            if (extraFolders != null) folders.AddRange(extraFolders.Where(f => !string.IsNullOrWhiteSpace(f)));

            foreach (var folder in folders)
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories);
                }
                catch { continue; }

                foreach (var f in files)
                {
                    if (!seen.Add(f)) continue;
                    results.Add(new PluginEntry
                    {
                        Path = f,
                        DisplayName = Path.GetFileNameWithoutExtension(f),
                    });
                }
            }

            return results
                .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
