using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jacobi.Vst.Core;
using Jacobi.Vst.Host.Interop;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Cherche des plugins VST2 (fichiers .dll) dans les dossiers Steinberg standards. Résultat mis en
    /// cache pour la session (un premier accès scanne, les suivants sont instantanés). L'utilisateur peut
    /// re-scanner explicitement via <see cref="ForceRescan"/>.
    ///
    /// Le scan lui-même ne CHARGE PAS les DLL (juste <see cref="Directory.EnumerateFiles"/>) — pas de risque
    /// de charger un plugin buggé au démarrage. En revanche <see cref="ClassifyIfNeeded"/> charge chaque
    /// plugin encore inconnu pour lire son flag <see cref="VstPluginFlags.IsSynth"/> (VSTi = instrument,
    /// sinon = effet d'insert). Coût : ~50-500 ms par plugin. Résultat mis en cache session : appelé
    /// UNE fois par l'UI avant de peupler un sous-menu (Effet ou VSTi), instantané ensuite.
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

        /// <summary>Une entrée dans le catalogue — chemin + nom d'affichage + classification (instrument vs effet).
        /// <see cref="IsInstrument"/> reste <c>null</c> tant que <see cref="ClassifyIfNeeded"/> n'a pas été appelée
        /// pour l'entrée (ou que la classification a échoué : plugin qui refuse de se charger, DLL corrompue, etc.).</summary>
        public class PluginEntry
        {
            public string Path;
            public string DisplayName;
            /// <summary><c>true</c> = VSTi (source sonore, réagit au MIDI), <c>false</c> = effet d'insert,
            /// <c>null</c> = pas encore classifié ou échec de chargement.</summary>
            public bool? IsInstrument;
        }

        /// <summary>Récupère la liste courante (scanne si nécessaire). Filtre les doublons par chemin (case-insensitive).
        /// Ne classifie PAS : appelle <see cref="ClassifyIfNeeded"/> pour connaître <see cref="PluginEntry.IsInstrument"/>.</summary>
        public static List<PluginEntry> GetPlugins(IEnumerable<string> extraFolders = null)
        {
            lock (_lock)
            {
                if (_cache != null) return _cache;
                _cache = Scan(extraFolders);
                return _cache;
            }
        }

        /// <summary>Force un re-scan (utile après ajout d'un dossier personnalisé ou installation d'un nouveau VST).
        /// La classification est réinitialisée : appelez à nouveau <see cref="ClassifyIfNeeded"/>.</summary>
        public static List<PluginEntry> ForceRescan(IEnumerable<string> extraFolders = null)
        {
            lock (_lock)
            {
                _cache = Scan(extraFolders);
                return _cache;
            }
        }

        /// <summary>Sous-liste ne contenant que les instruments (<see cref="PluginEntry.IsInstrument"/> == true).
        /// Appelle <see cref="ClassifyIfNeeded"/> en interne — sync, peut geler l'UI quelques secondes la 1re fois.</summary>
        public static List<PluginEntry> GetInstruments(IEnumerable<string> extraFolders = null)
        {
            ClassifyIfNeeded(extraFolders);
            lock (_lock)
            {
                return _cache?.Where(e => e.IsInstrument == true).ToList() ?? new List<PluginEntry>();
            }
        }

        /// <summary>Sous-liste ne contenant que les effets (<see cref="PluginEntry.IsInstrument"/> == false).
        /// Les entrées non classifiées (échec de chargement) sont incluses par défaut — elles pourraient être
        /// des effets. Appelle <see cref="ClassifyIfNeeded"/> en interne.</summary>
        public static List<PluginEntry> GetEffects(IEnumerable<string> extraFolders = null)
        {
            ClassifyIfNeeded(extraFolders);
            lock (_lock)
            {
                return _cache?.Where(e => e.IsInstrument != true).ToList() ?? new List<PluginEntry>();
            }
        }

        /// <summary>Charge chaque plugin encore non classifié pour lire <c>PluginInfo.Flags &amp; IsSynth</c>, puis
        /// le dispose. Les échecs (chargement impossible, exception) laissent <see cref="PluginEntry.IsInstrument"/>
        /// à <c>null</c> — l'entrée peut encore être proposée dans le sous-menu effets (position conservatrice).
        /// Verrouille pendant l'opération : deux appels concurrents partagent le résultat sans double travail.</summary>
        public static void ClassifyIfNeeded(IEnumerable<string> extraFolders = null)
        {
            List<PluginEntry> todo;
            lock (_lock)
            {
                if (_cache == null) _cache = Scan(extraFolders);
                todo = _cache.Where(e => e.IsInstrument == null).ToList();
            }
            if (todo.Count == 0) return;

            foreach (var e in todo)
            {
                bool? isInst = TryDetectSynth(e.Path);
                lock (_lock) { e.IsInstrument = isInst; }
            }
        }

        /// <summary>Charge un plugin, lit son flag <see cref="VstPluginFlags.IsSynth"/>, puis le décharge.
        /// Renvoie <c>null</c> sur toute exception (plugin non VST2, corrompu, incompatible x64, etc.).</summary>
        static bool? TryDetectSynth(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            VstPluginContext ctx = null;
            try
            {
                // Sample rate + block size neutres : on ne va pas rendre, juste ouvrir pour lire les flags.
                var host = new VstHostCommandStub(44100, 512);
                ctx = VstPluginContext.Create(path, host);
                var flags = ctx.PluginInfo.Flags;
                return (flags & VstPluginFlags.IsSynth) != 0;
            }
            catch { return null; }
            finally
            {
                if (ctx != null)
                {
                    try { ctx.PluginCommandStub.Commands.Close(); } catch { }
                    try { ctx.Dispose(); } catch { }
                }
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
                        IsInstrument = null,   // classifié à la demande par ClassifyIfNeeded()
                    });
                }
            }

            return results
                .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
