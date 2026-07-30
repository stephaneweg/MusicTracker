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
    /// <see cref="KotonPluginRegistry.GetInstruments"/> pour peupler le menu de sélection sans instancier
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
    /// Registre singleton des plugins Koton natifs (<c>.ksl</c>) trouvés dans <c>&lt;AppPaths.BaseDir&gt;/plugins/</c>.
    ///
    /// Un fichier <c>.ksl</c> = une DLL .NET renommée. Le loader utilise <see cref="Assembly.LoadFrom"/>
    /// — l'extension n'est pas contrôlée par le loader, seul le contenu compte. Chaque assembly est
    /// scanné à la recherche de types marqués <see cref="KotonInstrumentAttribute"/> /
    /// <see cref="KotonEffectAttribute"/> implémentant la bonne interface, avec un constructeur public
    /// sans paramètre. Les erreurs de chargement (fichier corrompu, dépendance manquante, réflexion qui
    /// jette) sont capturées silencieusement et le fichier est ignoré — le reste des plugins continue à
    /// se charger. Un plugin qui jette au constructeur n'est pas listé (l'instanciation de sondage se
    /// fait sur demande via <see cref="InstantiateInstrument"/>).
    ///
    /// **Scan** : paresseux au premier appel de <see cref="GetInstruments"/> / <see cref="GetEffects"/>.
    /// <see cref="Rescan"/> ré-analyse le dossier ; les assemblies DÉJÀ chargés ne peuvent pas être
    /// libérés dans un AppDomain standard (une future v2 pourra passer par un <c>AssemblyLoadContext</c>
    /// unloadable), donc "Rescan" ne fait qu'ajouter les nouveaux fichiers. Un utilisateur qui met à
    /// jour un plugin déjà chargé doit redémarrer l'app (comportement acceptable en bêta ; on affiche
    /// un tooltip explicatif dans l'UI).
    ///
    /// **Thread-safety** : le registre est verrouillé par un lock simple. Les scans sont rares (menu
    /// ouvert, redémarrage) — pas de contention.
    /// </summary>
    public static class KotonPluginRegistry
    {
        static readonly object _lock = new object();
        static readonly Dictionary<string, KotonInstrumentInfo> _instruments =
            new Dictionary<string, KotonInstrumentInfo>(StringComparer.Ordinal);
        static readonly Dictionary<string, KotonEffectInfo> _effects =
            new Dictionary<string, KotonEffectInfo>(StringComparer.Ordinal);
        static readonly HashSet<string> _loadedFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool _initialScanDone;

        /// <summary>Chemin absolu du dossier de plugins (à côté de l'exe). Créé si absent — dépose un
        /// <c>.ksl</c> dedans et le prochain <see cref="Rescan"/> le trouve.</summary>
        public static string PluginsDir
        {
            get
            {
                string dir = AppPaths.Local("plugins");
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
                catch { /* best-effort — un dossier inaccessible = registre vide, l'UI le montre. */ }
                return dir;
            }
        }

        /// <summary>Instruments Koton natifs disponibles. Déclenche un scan initial la première fois ;
        /// les appels suivants renvoient le cache jusqu'à <see cref="Rescan"/>.</summary>
        public static IReadOnlyList<KotonInstrumentInfo> GetInstruments()
        {
            EnsureInitialScan();
            lock (_lock) return _instruments.Values.OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Effets Koton natifs disponibles (non exposé par l'UI v1 — les 4 effets internes
        /// suffisent).</summary>
        public static IReadOnlyList<KotonEffectInfo> GetEffects()
        {
            EnsureInitialScan();
            lock (_lock) return _effects.Values.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Retrouve un instrument par <see cref="IKotonPlugin.Id"/> et retourne une instance
        /// fraîche. Null = id inconnu (plugin absent du dossier / non chargé). Un jet du constructeur du
        /// plugin est capturé (log silencieux, null renvoyé) — un plugin cassé ne doit pas empêcher la
        /// lecture du projet.</summary>
        public static IKotonInstrument InstantiateInstrument(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            EnsureInitialScan();
            KotonInstrumentInfo info;
            lock (_lock) { if (!_instruments.TryGetValue(id, out info)) return null; }
            try { return (IKotonInstrument)Activator.CreateInstance(info.Type); }
            catch { return null; }
        }

        /// <summary>Idem pour un effet.</summary>
        public static IKotonEffect InstantiateEffect(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            EnsureInitialScan();
            KotonEffectInfo info;
            lock (_lock) { if (!_effects.TryGetValue(id, out info)) return null; }
            try { return (IKotonEffect)Activator.CreateInstance(info.Type); }
            catch { return null; }
        }

        /// <summary>Re-scanne le dossier plugins et charge les NOUVEAUX assemblies (les déjà-chargés
        /// restent tels quels — on ne peut pas les libérer sans AssemblyLoadContext unloadable, non
        /// utilisé v1). Utilisé par le menu "Re-scanner" du header de piste.</summary>
        public static void Rescan()
        {
            ScanFolder();
        }

        static void EnsureInitialScan()
        {
            lock (_lock)
            {
                if (_initialScanDone) return;
                _initialScanDone = true;
            }
            ScanFolder();
        }

        static void ScanFolder()
        {
            string dir;
            try { dir = PluginsDir; }
            catch { return; }
            string[] files;
            try { files = Directory.GetFiles(dir, "*.ksl", SearchOption.AllDirectories); }
            catch { return; }
            foreach (var f in files) TryLoadFile(f);
        }

        static void TryLoadFile(string file)
        {
            // Chaque fichier n'est chargé qu'une fois par process. Un rescan qui retrouve le même
            // fichier ne re-tente pas (Assembly.LoadFrom sur le même chemin renvoie l'instance en
            // cache mais on économise la reflexion). Un fichier renommé serait vu comme nouveau — OK.
            lock (_lock) { if (_loadedFiles.Contains(file)) return; }
            Assembly asm;
            try
            {
                asm = Assembly.LoadFrom(file);
            }
            catch
            {
                lock (_lock) { _loadedFiles.Add(file); }  // marquer comme "vu et échoué" pour ne pas re-tenter
                return;
            }
            lock (_lock) { _loadedFiles.Add(file); }

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtle)
            {
                // Certains types peuvent référencer un assembly manquant. On garde ceux qu'on a pu charger.
                types = rtle.Types.Where(t => t != null).ToArray();
            }
            catch { return; }

            foreach (var t in types)
            {
                if (t == null || t.IsAbstract || t.IsInterface) continue;
                TryRegisterInstrument(t, file);
                TryRegisterEffect(t, file);
            }
        }

        static void TryRegisterInstrument(Type t, string sourceFile)
        {
            KotonInstrumentAttribute attr;
            try { attr = t.GetCustomAttribute<KotonInstrumentAttribute>(); }
            catch { return; }
            if (attr == null) return;
            if (!typeof(IKotonInstrument).IsAssignableFrom(t)) return;
            // Le ctor sans paramètre est requis — un ctor privé ou paramétré rend le type inutilisable
            // par Activator.CreateInstance (contrat du framework).
            var ctor = t.GetConstructor(Type.EmptyTypes);
            if (ctor == null) return;

            // On sonde l'Id en instanciant UNE FOIS (jetée aussitôt). Sinon on n'aurait aucun moyen de
            // désambiguïser deux types de même DisplayName. Un ctor qui jette = plugin ignoré (l'audio
            // sera protégé par le try/catch de InstantiateInstrument aussi).
            string id;
            try
            {
                using (var probe = (IKotonInstrument)Activator.CreateInstance(t))
                    id = probe?.Id;
            }
            catch { return; }
            if (string.IsNullOrEmpty(id)) return;

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
                // Doublon d'Id (deux plugins avec le même Id) : premier gagne, silencieux — l'utilisateur
                // ne saurait pas quoi faire d'un message, retirer un fichier manuellement suffit.
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

            string id;
            try
            {
                using (var probe = (IKotonEffect)Activator.CreateInstance(t))
                    id = probe?.Id;
            }
            catch { return; }
            if (string.IsNullOrEmpty(id)) return;

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
    }
}
