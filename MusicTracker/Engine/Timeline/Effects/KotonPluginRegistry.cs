using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KotonStudio.Library;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Métadonnées d'un instrument Koton natif découvert dans <c>plugins/*.ksl</c>. Retourné par
    /// <see cref="KotonPluginRegistry.Instruments"/> pour peupler le menu de sélection sans instancier
    /// le plugin (l'instanciation vient au moment où l'utilisateur choisit l'item).
    /// </summary>
    public sealed class KotonInstrumentInfo
    {
        public string Id { get; internal set; }
        public string DisplayName { get; internal set; }
        public string Category { get; internal set; }
        public string Version { get; internal set; }
        public string Vendor { get; internal set; }
        internal Type Type { get; set; }
        internal string SourceFile { get; set; }   // pour tooltips debug
    }

    /// <summary>
    /// Idem pour un effet Koton natif. Non consommé par l'UI v1 (le mixeur reste sur les 4 effets
    /// internes + VST) mais le registre les liste pour être prêt.
    /// </summary>
    public sealed class KotonEffectInfo
    {
        public string Id { get; internal set; }
        public string DisplayName { get; internal set; }
        public string Category { get; internal set; }
        public string Version { get; internal set; }
        public string Vendor { get; internal set; }
        internal Type Type { get; set; }
        internal string SourceFile { get; set; }
    }

    /// <summary>
    /// Métadonnées d'un générateur Koton natif (<see cref="IKotonGenerator"/>). Retourné par
    /// <see cref="KotonPluginRegistry.Generators"/> pour peupler le sous-menu « Insérer ▸ Générateur
    /// Koton » sans instancier le plugin. <see cref="Type"/> sert au filtrage par type de piste
    /// (batterie → Drum/Percussion, mélodique → Melody/Bass/Chord, etc.).
    /// </summary>
    public sealed class KotonGeneratorInfo
    {
        public string Id { get; internal set; }
        public string DisplayName { get; internal set; }
        public KotonGeneratorType Type { get; internal set; }
        public string Category { get; internal set; }
        public string Version { get; internal set; }
        public string Vendor { get; internal set; }
        internal System.Type TypeToken { get; set; }
        internal string SourceFile { get; set; }
    }

    /// <summary>
    /// Registre singleton des plugins Koton natifs (<c>.ksl</c>) trouvés dans les dossiers configurés
    /// (<c>&lt;AppPaths.BaseDir&gt;/plugins/</c> bundle + <c>%LocalAppData%/MusicTracker/plugins/</c>
    /// utilisateur + dossiers utilisateur configurés dans <see cref="AppSettings"/>).
    ///
    /// **Architecture** — scan EXPLICITE au démarrage de l'app (<see cref="Initialize"/> appelé
    /// depuis <c>App.OnStartup</c>), pas de lazy-load. Le scan extrait les MÉTADONNÉES depuis les
    /// attributs UNIQUEMENT (aucun probe.Id qui instancie) — l'instanciation est différée au moment
    /// où l'utilisateur clique sur l'entrée du menu ou où un fichier .sq référence l'Id.
    ///
    /// Un fichier <c>.ksl</c> = une DLL .NET renommée. Le loader utilise <see cref="Assembly.LoadFrom"/>
    /// — l'extension n'est pas contrôlée, seul le contenu compte. Chaque assembly est scanné à la
    /// recherche de types marqués <see cref="KotonInstrumentAttribute"/> / <see cref="KotonEffectAttribute"/> /
    /// <see cref="KotonGeneratorAttribute"/> implémentant la bonne interface, avec un constructeur
    /// public sans paramètre. Les erreurs de chargement (fichier corrompu, dépendance manquante) sont
    /// capturées silencieusement et le fichier est ignoré — les autres plugins continuent à se charger.
    ///
    /// **Id du plugin** — pris de l'attribut (nouveau champ `Id` optionnel) ; à défaut, fallback sur
    /// <c>Type.FullName</c>. Aucune instanciation n'est nécessaire au scan.
    ///
    /// **Rescan** — <see cref="Rescan"/> ré-analyse les dossiers et ajoute les NOUVEAUX .ksl.
    /// Les assemblies déjà chargés ne peuvent pas être libérés dans un AppDomain standard (une v2
    /// pourra passer par un <c>AssemblyLoadContext</c> unloadable). Un utilisateur qui met à jour un
    /// plugin déjà chargé doit redémarrer l'app.
    ///
    /// **Thread-safety** — le registre est verrouillé par un lock simple. Les scans sont rares —
    /// pas de contention.
    /// </summary>
    public static class KotonPluginRegistry
    {
        static readonly object _lock = new object();
        static readonly Dictionary<string, KotonInstrumentInfo> _instruments =
            new Dictionary<string, KotonInstrumentInfo>(StringComparer.Ordinal);
        static readonly Dictionary<string, KotonEffectInfo> _effects =
            new Dictionary<string, KotonEffectInfo>(StringComparer.Ordinal);
        static readonly Dictionary<string, KotonGeneratorInfo> _generators =
            new Dictionary<string, KotonGeneratorInfo>(StringComparer.Ordinal);
        static readonly HashSet<string> _loadedFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly List<string> _scanDirs = new List<string>();

        /// <summary>Chemin par défaut du dossier de plugins bundlés avec l'exe. Toujours scanné en
        /// premier — permet à un plugin livré avec l'app d'être immédiatement dispo.</summary>
        public static string BundledDir
        {
            get
            {
                string dir = AppPaths.Local("plugins");
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        /// <summary>Chemin par défaut du dossier de plugins utilisateur (<c>%LocalAppData%\MusicTracker\plugins</c>).
        /// L'utilisateur peut y déposer ses propres .ksl sans avoir accès en écriture au dossier de l'exe
        /// (Program Files est read-only).</summary>
        public static string UserDir
        {
            get
            {
                string dir = AppPaths.LocalData("plugins");
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        /// <summary>Alias historique — le dossier de scan primaire (bundle) ; conservé pour un code
        /// externe qui référencerait ce nom. Utiliser <see cref="BundledDir"/> pour le nouveau code.</summary>
        public static string PluginsDir => BundledDir;

        /// <summary>Liste des dossiers scannés (bundle + user + dossiers config, dans cet ordre).
        /// Recomposée à chaque <see cref="Initialize"/> / <see cref="Rescan"/> depuis
        /// <see cref="AppSettings"/> — expose pour affichage debug/UI.</summary>
        public static IReadOnlyList<string> ScanDirs
        {
            get { lock (_lock) return _scanDirs.ToList(); }
        }

        /// <summary>Métadonnées de tous les instruments Koton natifs découverts. Liste triée par
        /// <see cref="KotonInstrumentInfo.DisplayName"/>. Utilisée comme datasource par le menu de
        /// sélection d'instrument par piste.</summary>
        public static IReadOnlyList<KotonInstrumentInfo> Instruments
        {
            get { lock (_lock) return _instruments.Values.OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(); }
        }

        /// <summary>Métadonnées de tous les effets Koton natifs découverts (non exposé par l'UI v1).</summary>
        public static IReadOnlyList<KotonEffectInfo> Effects
        {
            get { lock (_lock) return _effects.Values.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(); }
        }

        /// <summary>Métadonnées de tous les générateurs Koton natifs découverts, triés par nom.
        /// Datasource du sous-menu « Insérer ▸ Générateur Koton », filtré côté UI selon le type de
        /// piste sélectionnée.</summary>
        public static IReadOnlyList<KotonGeneratorInfo> Generators
        {
            get { lock (_lock) return _generators.Values.OrderBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(); }
        }

        // ------ Alias historiques (compat) ------

        /// <summary>Alias historique de <see cref="Instruments"/> — code existant qui appelait
        /// <c>GetInstruments()</c>. Conservé pour ne pas casser les callers.</summary>
        public static IReadOnlyList<KotonInstrumentInfo> GetInstruments() => Instruments;

        /// <summary>Alias historique de <see cref="Effects"/>.</summary>
        public static IReadOnlyList<KotonEffectInfo> GetEffects() => Effects;

        /// <summary>Alias historique de <see cref="Generators"/>.</summary>
        public static IReadOnlyList<KotonGeneratorInfo> GetGenerators() => Generators;

        /// <summary>Scan initial — appelé UNE FOIS au démarrage de l'app (<c>App.OnStartup</c>).
        /// Scanne le dossier bundle + le dossier user + les dossiers additionnels fournis
        /// (typiquement <c>AppSettings.KotonPluginFolders</c>). Sans-effet si déjà appelé.</summary>
        /// <param name="extraDirs">Dossiers additionnels à scanner (utilisateur), ou <c>null</c>
        /// pour se limiter aux dossiers par défaut (bundle + user).</param>
        public static void Initialize(IEnumerable<string> extraDirs = null)
        {
            lock (_lock)
            {
                _scanDirs.Clear();
                _scanDirs.Add(BundledDir);
                if (!string.Equals(UserDir, BundledDir, StringComparison.OrdinalIgnoreCase))
                    _scanDirs.Add(UserDir);
                if (extraDirs != null)
                    foreach (var d in extraDirs)
                        if (!string.IsNullOrEmpty(d) && !_scanDirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                            _scanDirs.Add(d);
            }
            ScanAllFolders();
        }

        /// <summary>Re-scanne les dossiers configurés et charge les NOUVEAUX assemblies. Les
        /// assemblies déjà chargés restent tels quels (limite AppDomain standard). Peut être appelé
        /// depuis un item de menu "Re-scan" ou après avoir déposé un nouveau .ksl.</summary>
        public static void Rescan()
        {
            ScanAllFolders();
        }

        /// <summary>Instancie un instrument par <see cref="KotonInstrumentInfo.Id"/> — instance fraîche
        /// à chaque appel. <c>null</c> si l'id est inconnu ou si le constructeur du plugin jette.</summary>
        public static IKotonInstrument InstantiateInstrument(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            KotonInstrumentInfo info;
            lock (_lock) { if (!_instruments.TryGetValue(id, out info)) return null; }
            try { return (IKotonInstrument)Activator.CreateInstance(info.Type); }
            catch { return null; }
        }

        /// <summary>Instancie un effet par id.</summary>
        public static IKotonEffect InstantiateEffect(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            KotonEffectInfo info;
            lock (_lock) { if (!_effects.TryGetValue(id, out info)) return null; }
            try { return (IKotonEffect)Activator.CreateInstance(info.Type); }
            catch { return null; }
        }

        /// <summary>Instancie un générateur par <see cref="KotonGeneratorInfo.Id"/> — instance fraîche
        /// (le player en crée une par bloc, l'éditeur du bloc réutilise l'instance vivante du module).
        /// <c>null</c> = id inconnu ou constructeur qui jette.</summary>
        public static IKotonGenerator InstantiateGenerator(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            KotonGeneratorInfo info;
            lock (_lock) { if (!_generators.TryGetValue(id, out info)) return null; }
            try { return (IKotonGenerator)Activator.CreateInstance(info.TypeToken); }
            catch { return null; }
        }

        // --------------- interne ---------------

        static void ScanAllFolders()
        {
            List<string> dirs;
            lock (_lock)
            {
                if (_scanDirs.Count == 0)
                {
                    // Initialize n'a pas encore été appelé (chemin dégradé — un code qui accède
                    // au registry avant App.OnStartup). Reprend les défauts au vol.
                    _scanDirs.Add(BundledDir);
                    if (!string.Equals(UserDir, BundledDir, StringComparison.OrdinalIgnoreCase))
                        _scanDirs.Add(UserDir);
                }
                dirs = _scanDirs.ToList();
            }
            foreach (var dir in dirs)
            {
                string[] files;
                try { files = Directory.GetFiles(dir, "*.ksl", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var f in files) TryLoadFile(f);
            }
        }

        static void TryLoadFile(string file)
        {
            // Chaque fichier n'est chargé qu'une fois par process. Un rescan qui retrouve le même
            // fichier ne re-tente pas.
            lock (_lock) { if (_loadedFiles.Contains(file)) return; }
            Assembly asm;
            try { asm = Assembly.LoadFrom(file); }
            catch
            {
                lock (_lock) { _loadedFiles.Add(file); }
                return;
            }
            lock (_lock) { _loadedFiles.Add(file); }

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtle)
            {
                types = rtle.Types.Where(t => t != null).ToArray();
            }
            catch { return; }

            foreach (var t in types)
            {
                if (t == null || t.IsAbstract || t.IsInterface) continue;
                TryRegisterInstrument(t, file);
                TryRegisterEffect(t, file);
                TryRegisterGenerator(t, file);
            }
        }

        static void TryRegisterInstrument(Type t, string sourceFile)
        {
            KotonInstrumentAttribute attr;
            try { attr = t.GetCustomAttribute<KotonInstrumentAttribute>(); }
            catch { return; }
            if (attr == null) return;
            if (!typeof(IKotonInstrument).IsAssignableFrom(t)) return;
            var ctor = t.GetConstructor(Type.EmptyTypes);
            if (ctor == null) return;

            // Id : attribut si renseigné, fallback FullName. Pas d'instanciation au scan (rapide, sûr,
            // et le ctor du plugin peut avoir des effets de bord qu'on ne veut pas déclencher juste
            // pour peupler un menu).
            string id = string.IsNullOrEmpty(attr.Id) ? t.FullName : attr.Id;

            var info = new KotonInstrumentInfo
            {
                Id = id,
                DisplayName = attr.DisplayName,
                Category = attr.Category ?? "",
                Version = attr.Version ?? "",
                Vendor = attr.Vendor ?? "",
                Type = t,
                SourceFile = sourceFile,
            };
            lock (_lock)
            {
                if (!_instruments.ContainsKey(id)) _instruments[id] = info;
                // Doublon d'Id (2 plugins avec le même Id) : premier gagne, silencieux.
            }
        }

        static void TryRegisterEffect(Type t, string sourceFile)
        {
            KotonEffectAttribute attr;
            try { attr = t.GetCustomAttribute<KotonEffectAttribute>(); }
            catch { return; }
            if (attr == null) return;
            if (!typeof(IKotonEffect).IsAssignableFrom(t)) return;
            var ctor = t.GetConstructor(Type.EmptyTypes);
            if (ctor == null) return;

            string id = string.IsNullOrEmpty(attr.Id) ? t.FullName : attr.Id;

            var info = new KotonEffectInfo
            {
                Id = id,
                DisplayName = attr.DisplayName,
                Category = attr.Category ?? "",
                Version = attr.Version ?? "",
                Vendor = attr.Vendor ?? "",
                Type = t,
                SourceFile = sourceFile,
            };
            lock (_lock)
            {
                if (!_effects.ContainsKey(id)) _effects[id] = info;
            }
        }

        static void TryRegisterGenerator(Type t, string sourceFile)
        {
            KotonGeneratorAttribute attr;
            try { attr = t.GetCustomAttribute<KotonGeneratorAttribute>(); }
            catch { return; }
            if (attr == null) return;
            if (!typeof(IKotonGenerator).IsAssignableFrom(t)) return;
            var ctor = t.GetConstructor(Type.EmptyTypes);
            if (ctor == null) return;

            string id = string.IsNullOrEmpty(attr.Id) ? t.FullName : attr.Id;

            var info = new KotonGeneratorInfo
            {
                Id = id,
                DisplayName = attr.DisplayName,
                Type = attr.Type,
                Category = attr.Category ?? "",
                Version = attr.Version ?? "",
                Vendor = attr.Vendor ?? "",
                TypeToken = t,
                SourceFile = sourceFile,
            };
            lock (_lock)
            {
                if (!_generators.ContainsKey(id)) _generators[id] = info;
            }
        }
    }
}
