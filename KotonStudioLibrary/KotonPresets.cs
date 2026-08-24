using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace KotonStudio.Library
{
    /// <summary>
    /// Un preset = un jeu de réglages nommé, rangé au niveau UTILISATEUR (pas dans le projet) et
    /// rechargeable dans n'importe quel morceau. Purement déclaratif : rien ici ne participe au rendu
    /// du son, ce sont les valeurs que le plugin relira ensuite comme si l'utilisateur avait bougé
    /// les curseurs à la main.
    ///
    /// **Deux couches, et c'est volontaire** :
    /// <list type="bullet">
    /// <item><see cref="Params"/> — le dictionnaire id → valeur des <see cref="KotonParameter"/>. Lisible,
    /// diffable, éditable au bloc-notes, et robuste aux versions : un paramètre ajouté après coup garde
    /// simplement son défaut, un paramètre supprimé est ignoré.</item>
    /// <item><see cref="State"/> — le blob <see cref="IKotonPlugin.SaveState"/> encodé en base64, écrit
    /// SEULEMENT si le plugin en produit un non vide. Plusieurs plugins portent un état qui n'est pas
    /// dans leurs paramètres : la courbe de Spline Melody, l'accord des cordes du guqin, les motifs
    /// d'une boîte à rythme. Un preset limité aux paramètres perdrait l'essentiel en silence pour
    /// ceux-là.</item>
    /// </list>
    ///
    /// À la relecture, l'état est restauré d'abord et les paramètres écrits par-dessus (cf.
    /// <see cref="KotonPresetCatalog.Apply(KotonPreset, IKotonPlugin)"/>) : si les deux couches
    /// divergent — plugin dont le format de blob a changé entre deux versions — ce sont les valeurs
    /// lisibles qui gagnent.
    /// </summary>
    public sealed class KotonPreset
    {
        /// <summary><see cref="IKotonPlugin.Id"/> du plugin auquel ce preset appartient. Un preset n'est
        /// jamais proposé à un autre plugin : les ids de paramètres n'ont de sens que chez lui.</summary>
        public string PluginId { get; set; }

        /// <summary>Nom affiché, tel que l'utilisateur l'a tapé (accents et espaces compris — le nom de
        /// FICHIER, lui, est assaini, cf. <see cref="KotonPresetCatalog"/>).</summary>
        public string Name { get; set; }

        /// <summary>Valeurs des paramètres, indexées par <see cref="KotonParameter.Id"/>.</summary>
        public Dictionary<string, double> Params { get; set; } = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>Blob <see cref="IKotonPlugin.SaveState"/> en base64, ou <c>null</c> si le plugin n'a
        /// pas d'état interne à préserver.</summary>
        public string State { get; set; }

        /// <summary>Date d'écriture (ISO 8601), purement informative — sert à trier « les plus récents »
        /// si un jour l'UI le propose.</summary>
        public string Saved { get; set; }
    }

    /// <summary>
    /// Catalogue GLOBAL des presets, partagé par tous les plugins Koton quel que soit leur type —
    /// instrument, effet, générateur, contrainte de générateur. C'est le même magasin pour tout le
    /// monde : un plugin n'a rien à implémenter pour en profiter, l'hôte affiche la barre de presets
    /// au-dessus de son éditeur et appelle ce catalogue.
    ///
    /// **Rangement** : un dossier par plugin, un fichier <c>.kpreset</c> (JSON) par preset —
    /// <c>&lt;Folder&gt;/&lt;pluginId&gt;/&lt;nom&gt;.kpreset</c>. Un fichier par preset plutôt qu'un
    /// gros index : sauvegarder n'a alors jamais à réécrire le travail des autres, un preset corrompu
    /// n'emporte pas la collection, et partager un réglage se résume à envoyer un fichier.
    ///
    /// **Emplacement** : l'hôte pose <see cref="Folder"/> au démarrage. Sans hôte (test isolé d'un
    /// plugin, harnais de mesure), le repli tombe sur le même dossier utilisateur que celui de
    /// l'application, donc les deux voient toujours la même collection.
    ///
    /// **Robustesse** : aucune méthode ne jette. Un disque plein, un dossier en lecture seule ou un
    /// JSON abîmé rendent <c>false</c> / <c>null</c> / une liste vide — perdre un preset ne doit jamais
    /// coûter la session en cours.
    /// </summary>
    public static class KotonPresetCatalog
    {
        /// <summary>Extension des fichiers de preset. Distincte pour que le dossier reste lisible et
        /// qu'un futur double-clic puisse être associé.</summary>
        public const string Extension = ".kpreset";

        static string _folder;

        /// <summary>Dossier racine du catalogue. L'hôte le pose au démarrage ; sinon repli sur
        /// <c>%AppData%\MusicTracker\presets</c> — même dossier que celui qu'utilise l'application, pour
        /// qu'un plugin lancé hors hôte voie la même collection.</summary>
        public static string Folder
        {
            get
            {
                if (!string.IsNullOrEmpty(_folder)) return _folder;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MusicTracker", "presets");
            }
            set { _folder = value; _cache.Clear(); }
        }

        /// <summary>Levé après un enregistrement ou une suppression, avec l'id du plugin concerné.
        /// Permet à une barre de presets ouverte ailleurs (deux éditeurs du même plugin) de se
        /// rafraîchir. Thread UI attendu — le catalogue n'est appelé que depuis là.</summary>
        public static event Action<string> Changed;

        // Cache par plugin : les presets sont minuscules et relus à chaque ouverture d'éditeur, autant
        // ne pas retoucher le disque à chaque fois. Invalidé en écriture (donc jamais périmé du fait de
        // l'app elle-même) ; un fichier déposé à la main pendant que l'app tourne demande un Refresh.
        static readonly Dictionary<string, List<KotonPreset>> _cache =
            new Dictionary<string, List<KotonPreset>>(StringComparer.Ordinal);

        /// <summary>Oublie ce qui est en cache et forcera une relecture disque au prochain appel.
        /// Utile après avoir déposé des fichiers à la main dans le dossier.</summary>
        public static void Refresh() { _cache.Clear(); }

        /// <summary>Dossier des presets d'un plugin donné (créé à la demande à l'écriture seulement —
        /// lire ne doit pas semer des dossiers vides).</summary>
        public static string FolderFor(string pluginId) => Path.Combine(Folder, Sanitize(pluginId ?? "unknown"));

        /// <summary>Tous les presets d'un plugin, triés par nom. Liste vide si le plugin n'en a aucun
        /// (cas de très loin le plus courant : rien n'est livré d'usine).</summary>
        public static IReadOnlyList<KotonPreset> List(string pluginId)
        {
            if (string.IsNullOrEmpty(pluginId)) return Array.Empty<KotonPreset>();
            if (_cache.TryGetValue(pluginId, out var hit)) return hit;

            var found = new List<KotonPreset>();
            try
            {
                string dir = FolderFor(pluginId);
                if (Directory.Exists(dir))
                {
                    foreach (string f in Directory.EnumerateFiles(dir, "*" + Extension))
                    {
                        var p = ReadFile(f);
                        if (p == null) continue;
                        // Le nom affiché vient du CONTENU, pas du nom de fichier : il a pu être assaini
                        // (« Cuivre 3/4 » → « Cuivre 3_4 ») et on veut réafficher ce que l'utilisateur a tapé.
                        if (string.IsNullOrWhiteSpace(p.Name)) p.Name = Path.GetFileNameWithoutExtension(f);
                        p.PluginId = pluginId;
                        found.Add(p);
                    }
                }
            }
            catch (Exception ex) { Report(ex); }

            found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            _cache[pluginId] = found;
            return found;
        }

        /// <summary>Noms des presets d'un plugin, dans l'ordre d'affichage.</summary>
        public static IReadOnlyList<string> ListNames(string pluginId) =>
            List(pluginId).Select(p => p.Name).ToList();

        /// <summary>Le preset de ce nom, ou <c>null</c>. La comparaison ignore la casse — l'utilisateur
        /// qui retape « Doux » au lieu de « doux » écrase bien le sien plutôt que d'en créer un second.</summary>
        public static KotonPreset Find(string pluginId, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var p in List(pluginId))
                if (string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)) return p;
            return null;
        }

        /// <summary>Vrai si un preset de ce nom existe déjà (pour demander confirmation avant d'écraser).</summary>
        public static bool Exists(string pluginId, string name) => Find(pluginId, name) != null;

        /// <summary>Photographie l'état courant du plugin sous forme de preset, SANS écrire sur le disque
        /// (utile pour un « comparer avec… » ou un annuler local).</summary>
        public static KotonPreset Capture(IKotonPlugin plugin, string name)
        {
            if (plugin == null) return null;
            var preset = new KotonPreset
            {
                PluginId = plugin.Id,
                Name = name,
                Saved = DateTime.Now.ToString("o"),
            };
            try
            {
                var ps = plugin.Parameters;
                if (ps != null)
                    foreach (var p in ps)
                        if (p != null && !string.IsNullOrEmpty(p.Id)) preset.Params[p.Id] = p.Value;
            }
            catch (Exception ex) { Report(ex); }

            try
            {
                byte[] blob = plugin.SaveState();
                // Un blob vide n'est pas écrit : la moitié des plugins n'ont rien de plus que leurs
                // paramètres, et un champ "state":"" dans chaque fichier n'apprendrait rien à personne.
                if (blob != null && blob.Length > 0) preset.State = Convert.ToBase64String(blob);
            }
            catch (Exception ex) { Report(ex); }

            return preset;
        }

        /// <summary>Enregistre l'état courant du plugin sous <paramref name="name"/>, en écrasant un
        /// preset existant de même nom. Retourne faux si l'écriture a échoué (dossier en lecture seule,
        /// disque plein) — l'appelant peut alors le dire à l'utilisateur.</summary>
        public static bool Save(IKotonPlugin plugin, string name)
        {
            if (plugin == null || string.IsNullOrWhiteSpace(name)) return false;
            var preset = Capture(plugin, name.Trim());
            return Write(preset);
        }

        /// <summary>Écrit un preset déjà constitué (celui rendu par <see cref="Capture"/>, ou un preset
        /// importé). Le nom de fichier découle du nom affiché ; le nom affiché reste dans le contenu.</summary>
        public static bool Write(KotonPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.PluginId) || string.IsNullOrWhiteSpace(preset.Name))
                return false;
            try
            {
                string dir = FolderFor(preset.PluginId);
                Directory.CreateDirectory(dir);
                string path = PathFor(preset.PluginId, preset.Name);
                string json = JsonSerializer.Serialize(preset, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    // Les noms de presets sont écrits par des humains, en français : sans ce réglage,
                    // System.Text.Json échappe tous les non-ASCII (« Doux été ») et le fichier
                    // devient illisible à l'œil, alors qu'un preset doit rester bidouillable au bloc-notes.
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                File.WriteAllText(path, json, new UTF8Encoding(false));
                _cache.Remove(preset.PluginId);
                RaiseChanged(preset.PluginId);
                return true;
            }
            catch (Exception ex) { Report(ex); return false; }
        }

        /// <summary>Supprime un preset. Vrai si le fichier a bien disparu (ou n'existait déjà plus).</summary>
        public static bool Delete(string pluginId, string name)
        {
            if (string.IsNullOrEmpty(pluginId) || string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                // Passer par la liste plutôt que par PathFor : le fichier a pu être déposé à la main sous
                // un nom de fichier qui ne correspond pas au nom affiché.
                string target = null;
                string dir = FolderFor(pluginId);
                if (Directory.Exists(dir))
                {
                    foreach (string f in Directory.EnumerateFiles(dir, "*" + Extension))
                    {
                        var p = ReadFile(f);
                        string n = p != null && !string.IsNullOrWhiteSpace(p.Name) ? p.Name : Path.GetFileNameWithoutExtension(f);
                        if (string.Equals(n, name, StringComparison.CurrentCultureIgnoreCase)) { target = f; break; }
                    }
                }
                if (target != null) File.Delete(target);
                _cache.Remove(pluginId);
                RaiseChanged(pluginId);
                return true;
            }
            catch (Exception ex) { Report(ex); return false; }
        }

        /// <summary>Charge le preset <paramref name="name"/> dans le plugin. Voir
        /// <see cref="Apply(KotonPreset, IKotonPlugin)"/> pour l'ordre de restauration.</summary>
        public static bool Apply(string pluginId, string name, IKotonPlugin plugin) =>
            Apply(Find(pluginId, name), plugin);

        /// <summary>
        /// Recharge un preset dans un plugin vivant. L'état interne est restauré d'abord, les valeurs de
        /// paramètres écrites ensuite : si le blob date d'une version dont le format a changé, les
        /// valeurs lisibles rattrapent ce que la relecture du blob a raté.
        ///
        /// L'écriture passe par <see cref="KotonParameter.Value"/> (et non
        /// <see cref="KotonParameter.SetFromAutomation"/>) : c'est un geste utilisateur, ponctuel, et
        /// l'éditeur ouvert DOIT voir ses curseurs bouger. Appeler depuis le thread UI.
        /// </summary>
        public static bool Apply(KotonPreset preset, IKotonPlugin plugin)
        {
            if (preset == null || plugin == null) return false;
            bool ok = false;

            if (!string.IsNullOrEmpty(preset.State))
            {
                try
                {
                    plugin.LoadState(Convert.FromBase64String(preset.State));
                    ok = true;
                }
                catch (Exception ex) { Report(ex); }
            }

            try
            {
                var ps = plugin.Parameters;
                if (ps != null && preset.Params != null)
                {
                    foreach (var p in ps)
                    {
                        if (p == null || string.IsNullOrEmpty(p.Id)) continue;
                        // Un paramètre absent du preset garde sa valeur courante plutôt que de retomber au
                        // défaut : un preset écrit par une version antérieure du plugin ne doit pas
                        // réinitialiser en douce les réglages ajoutés depuis.
                        if (preset.Params.TryGetValue(p.Id, out double v)) { p.Value = v; ok = true; }
                    }
                }
            }
            catch (Exception ex) { Report(ex); }

            return ok;
        }

        // ------------------------------------------------------------------------------------------

        static string PathFor(string pluginId, string name) =>
            Path.Combine(FolderFor(pluginId), Sanitize(name) + Extension);

        static KotonPreset ReadFile(string path)
        {
            try { return JsonSerializer.Deserialize<KotonPreset>(File.ReadAllText(path)); }
            catch { return null; }   // Fichier abîmé : il est simplement absent de la liste, sans bruit.
        }

        /// <summary>Rend un nom utilisable comme nom de fichier. Les caractères interdits deviennent
        /// « _ » ; un nom vide après nettoyage devient « preset ».</summary>
        static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "preset";
            var sb = new StringBuilder(s.Length);
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in s.Trim())
                sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            string outp = sb.ToString().Trim('.', ' ');
            return string.IsNullOrEmpty(outp) ? "preset" : outp;
        }

        static void RaiseChanged(string pluginId) { try { Changed?.Invoke(pluginId); } catch { } }

        static void Report(Exception ex) { try { KotonHost.ReportException?.Invoke(ex, "KotonPresetCatalog"); } catch { } }
    }
}
