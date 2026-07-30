using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Jacobi.Vst.Core;
using Jacobi.Vst.Host.Interop;
using MusicTracker.Engine.Timeline.Vst3;
using MusicTracker.Engine.Timeline.Vst3.Interop;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Blacklist persistante des plugins qui font PLANTER le scanner. Motif : un plugin VST2 mal codé peut
    /// crasher NATIVEMENT dans son handler <c>Close()</c> (ou même <c>Open()</c>) — AccessViolation / SEH
    /// non-catchables en .NET 5+ (le processus meurt, catch { } est ignoré). Sans blacklist, l'app crasherait
    /// à CHAQUE démarrage tant que le plugin foireux reste dans le dossier.
    ///
    /// Motif « canary » : avant d'attaquer un plugin, on écrit son chemin dans le champ <c>Pending</c>. Si le
    /// process meurt pendant la classification, au prochain démarrage on trouve <c>Pending</c> non-null →
    /// le plugin est promu blacklist permanente et plus jamais retouché. Après une classification qui
    /// réussit, <c>Pending</c> est effacé.
    ///
    /// L'utilisateur peut réinitialiser via <see cref="Clear"/> ou en supprimant à la main
    /// <c>%AppData%\MusicTracker\vst-scan-blacklist.json</c>.
    /// </summary>
    static class VstScanBlacklist
    {
        static readonly string _path = AppPaths.Roaming("vst-scan-blacklist.json");
        static HashSet<string> _blacklist;
        static readonly object _lock = new object();

        class State { public List<string> Blacklist { get; set; } = new List<string>(); public string Pending { get; set; } }

        static void Init()
        {
            lock (_lock)
            {
                if (_blacklist != null) return;
                _blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (!File.Exists(_path)) return;
                    var s = JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
                    if (s?.Blacklist != null) foreach (var p in s.Blacklist) _blacklist.Add(p);
                    // Un Pending survivant = le process a crashé pendant le scan de ce plugin la fois d'avant.
                    // Promu blacklist permanente + resauvé sans Pending.
                    if (!string.IsNullOrEmpty(s?.Pending)) { _blacklist.Add(s.Pending); SaveUnlocked(null); }
                }
                catch { }
            }
        }

        public static bool IsBlacklisted(string path) { Init(); lock (_lock) return _blacklist.Contains(path); }

        /// <summary>À appeler AVANT toute tentative de chargement du plugin — persiste le chemin sur disque.</summary>
        public static void BeginScan(string path) { Init(); lock (_lock) SaveUnlocked(path); }

        /// <summary>À appeler APRÈS un chargement + classification réussis — efface le Pending sur disque.</summary>
        public static void EndScan() { lock (_lock) SaveUnlocked(null); }

        /// <summary>Vide toute la blacklist (menu utilisateur « Re-scanner tout »). Le fichier est supprimé.</summary>
        public static void Clear() { lock (_lock) { _blacklist?.Clear(); try { File.Delete(_path); } catch { } } }

        static void SaveUnlocked(string pending)
        {
            try
            {
                var s = new State { Blacklist = _blacklist.ToList(), Pending = pending };
                File.WriteAllText(_path, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* best-effort ; si l'écriture échoue, tant pis, on retentera au boot suivant */ }
        }
    }


    /// <summary>
    /// Cherche des plugins VST2 (fichiers .dll) ET VST3 (fichiers/dossiers-bundles .vst3) dans les
    /// dossiers Steinberg standards. Résultat mis en cache pour la session (un premier accès scanne,
    /// les suivants sont instantanés). L'utilisateur peut re-scanner explicitement via <see cref="ForceRescan"/>.
    ///
    /// Le scan lui-même ne CHARGE PAS les DLL (juste <see cref="Directory.EnumerateFileSystemEntries"/>)
    /// — pas de risque de charger un plugin buggé au démarrage. En revanche <see cref="ClassifyIfNeeded"/>
    /// charge chaque plugin encore inconnu pour lire son flag « est un instrument » :
    /// <list type="bullet">
    ///   <item>VST2 : flag <see cref="VstPluginFlags.IsSynth"/> ;</item>
    ///   <item>VST3 : sous-catégorie <c>Instrument</c> dans <c>PClassInfo2.subCategories</c>.</item>
    /// </list>
    /// Coût : ~50-500 ms par plugin. Résultat mis en cache session : appelé UNE fois par l'UI avant
    /// de peupler un sous-menu (Effet ou VSTi), instantané ensuite.
    /// </summary>
    public static class VstPluginScanner
    {
        static List<PluginEntry> _cache;
        static readonly object _lock = new object();

        /// <summary>Dossiers testés dans l'ordre. Le dossier LOCAL <c>vst\</c> (à côté de l'exécutable) passe EN
        /// PREMIER : il permet de bundler des plugins gratuits avec un install portable, ou de tester un plugin sans
        /// l'installer système-wide, et un plugin local peut ainsi masquer un plugin système du même nom. Suivent
        /// les dossiers Steinberg standards. Un futur écran de préférences pourra en ajouter d'autres.
        /// Un dossier qui n'existe pas est skippé silencieusement (voir <see cref="Scan"/>) — le dossier local
        /// n'est PAS créé automatiquement : c'est un opt-in de l'utilisateur.</summary>
        public static readonly string[] DefaultFolders = new[]
        {
            Path.Combine(AppPaths.BaseDir, "vst"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\VstPlugins"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Steinberg\VstPlugins"),
            Environment.ExpandEnvironmentVariables(@"%CommonProgramFiles%\VST2"),
            Environment.ExpandEnvironmentVariables(@"%CommonProgramFiles%\Steinberg\VST2"),
            // VST3 : dossier système canonique + dossier utilisateur (moins fréquent). Un même fichier
            // repéré ici et dans vst\ local sera dédupliqué par le scanner (case-insensitive HashSet).
            Environment.ExpandEnvironmentVariables(@"%CommonProgramFiles%\VST3"),
            Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Programs\Common\VST3"),
        };

        /// <summary>Format d'un plugin scanné — sert au routage vers la bonne implémentation d'hôte
        /// (VstEffect / Vst3Effect / VstInstrument / Vst3Instrument).</summary>
        public enum PluginFormat { Vst2, Vst3 }

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
            /// <summary>Format du plugin, déduit de l'extension au scan (Path .vst3 → Vst3, sinon Vst2).</summary>
            public PluginFormat Format;
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
                // Un plugin qui a déjà planté le scanner (crash natif dans Open/Close) reste blacklisté
                // et n'est plus touché — sinon on crasherait à CHAQUE démarrage. Marqué "instrument = null"
                // avec un préfixe ⚠️ pour signaler visuellement à l'utilisateur.
                if (VstScanBlacklist.IsBlacklisted(e.Path))
                {
                    lock (_lock) { e.IsInstrument = null; if (!e.DisplayName.StartsWith("⚠")) e.DisplayName = "⚠ " + e.DisplayName; }
                    continue;
                }
                // Motif canary : on écrit le chemin AVANT de tenter la charge. Si le process meurt ensuite,
                // le prochain démarrage promeut ce chemin en blacklist permanente (cf. VstScanBlacklist.Init).
                VstScanBlacklist.BeginScan(e.Path);
                bool? isInst = e.Format == PluginFormat.Vst3
                    ? TryDetectSynthVst3(e.Path)
                    : TryDetectSynth(e.Path);
                VstScanBlacklist.EndScan();
                lock (_lock) { e.IsInstrument = isInst; }
            }
        }

        /// <summary>Charge un plugin VST2, lit son flag <see cref="VstPluginFlags.IsSynth"/>, puis le décharge.
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
                    //try { ctx.PluginCommandStub.Commands.Close(); } catch { }
                    try { ctx.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// Charge un module VST3, énumère les classes via <see cref="IPluginFactory2.getClassInfo2"/> et
        /// renvoie <c>true</c> dès qu'une classe <c>kVstAudioEffectClass</c> déclare la sous-catégorie
        /// <c>Instrument</c>. Sans v2 factory, on ne peut pas distinguer effet/instrument → renvoie <c>false</c>
        /// (position conservatrice : sous-menu Effet). Le module est libéré en fin de méthode.
        /// </summary>
        static bool? TryDetectSynthVst3(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            using (var loader = new Vst3ModuleLoader())
            {
                try
                {
                    loader.Load(path);
                    var f2 = loader.Factory2;
                    if (f2 == null) return false;
                    int n = f2.countClasses();
                    for (int i = 0; i < n; i++)
                    {
                        if (f2.getClassInfo2(i, out var info) != 0) continue;
                        if (!string.Equals(info.Category, Vst3Uids.kVstAudioEffectClass, StringComparison.Ordinal))
                            continue;
                        var subs = info.SubCategories ?? "";
                        // subCategories = liste sépa. par '|', e.g. "Fx|Distortion" ou "Instrument|Synth"
                        if (subs.Contains(Vst3Uids.kInstrumentSubCategory, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    return false;
                }
                catch { return null; }
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

                // ---- VST2 : *.dll (fichiers) --------------------------------------------------------
                IEnumerable<string> dlls;
                try { dlls = Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories); }
                catch { dlls = Array.Empty<string>(); }
                foreach (var f in dlls)
                {
                    if (!seen.Add(f)) continue;
                    results.Add(new PluginEntry
                    {
                        Path = f,
                        DisplayName = Path.GetFileNameWithoutExtension(f),
                        IsInstrument = null,
                        Format = PluginFormat.Vst2,
                    });
                }

                // ---- VST3 : *.vst3 (fichiers plats OU dossiers-bundles) -----------------------------
                // Un bundle est un DOSSIER "Foo.vst3/Contents/x86_64-win/Foo.vst3". On enregistre le
                // dossier lui-même comme "path" — Vst3ModuleLoader.ResolveBinaryPath se chargera de
                // trouver le binaire à l'intérieur. Un fichier .vst3 plat (SDK 3.6+) est enregistré tel quel.
                // On énumère TopDirectoryOnly pour ne pas re-descendre dans Contents (le fichier interne
                // serait alors listé en double comme "fichier .vst3" en plus du bundle).
                IEnumerable<string> vst3Entries;
                try
                {
                    vst3Entries = Directory.EnumerateFileSystemEntries(folder, "*.vst3", SearchOption.AllDirectories)
                        // Filtrer les fichiers INTERNES au bundle (Contents/.../Foo.vst3) — on garde uniquement
                        // le bundle-dir ou un .vst3 plat au niveau du dossier de plugins.
                        .Where(p => !p.Replace('\\', '/').Contains("/Contents/", StringComparison.OrdinalIgnoreCase));
                }
                catch { vst3Entries = Array.Empty<string>(); }
                foreach (var f in vst3Entries)
                {
                    if (!seen.Add(f)) continue;
                    results.Add(new PluginEntry
                    {
                        Path = f,
                        DisplayName = Path.GetFileNameWithoutExtension(f),
                        IsInstrument = null,
                        Format = PluginFormat.Vst3,
                    });
                }
            }

            return results
                .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
