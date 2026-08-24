using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MusicTracker.Localization;

namespace MusicTracker.Controls
{
    /// <summary>
    /// Construction des menus de choix de plugin Koton, regroupés par catégorie.
    ///
    /// La liste des plugins livrés dépasse largement la vingtaine (instruments comme effets) : à plat, le
    /// menu déborde de l'écran et rien ne se retrouve. On regroupe donc par la <c>Category</c> déclarée
    /// dans l'attribut du plugin — sauf s'il n'y a qu'une seule catégorie, auquel cas un sous-menu unique
    /// n'ajouterait qu'un clic pour rien.
    ///
    /// Générique parce que les descripteurs d'instrument et d'effet n'ont pas d'ancêtre commun côté
    /// registre : l'appelant fournit trois accesseurs et l'action de sélection.
    /// </summary>
    public static class KotonPluginMenu
    {
        /// <summary>Catégorie de repli pour un plugin qui n'en déclare pas ; classée en dernier.</summary>
        public static string OtherCategory => Loc.T("KotonGenTypeOther");

        /// <param name="parent">Menu (ou sous-menu) qui reçoit les entrées.</param>
        /// <param name="plugins">Descripteurs à présenter.</param>
        /// <param name="category">Catégorie déclarée par le plugin (vide = <see cref="OtherCategory"/>).</param>
        /// <param name="displayName">Nom affiché.</param>
        /// <param name="isSelected">Vrai pour le plugin actuellement choisi — reçoit une coche.</param>
        /// <param name="onPick">Appelé au clic.</param>
        /// <param name="auditionId">Facultatif — id de plugin INSTRUMENT à faire entendre au survol de
        /// l'entrée (une note tenue tant que le pointeur reste dessus). Laisser <c>null</c> pour les menus
        /// d'effets ou de générateurs, qu'une note seule ne renseignerait pas.</param>
        public static void AddGroupedByCategory<T>(
            ItemsControl parent,
            IEnumerable<T> plugins,
            Func<T, string> category,
            Func<T, string> displayName,
            Func<T, bool> isSelected,
            Action<T> onPick,
            Func<T, string> auditionId = null)
        {
            var list = plugins?.ToList() ?? new List<T>();
            if (list.Count == 0) return;

            string other = OtherCategory;
            var groups = list
                .GroupBy(p => string.IsNullOrWhiteSpace(category(p)) ? other : category(p))
                // "Autres" toujours en fin de liste, le reste par ordre alphabétique.
                .OrderBy(g => g.Key == other ? "￿" : g.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            bool useSubmenus = groups.Count > 1;
            foreach (var g in groups)
            {
                ItemsControl target = parent;
                if (useSubmenus)
                {
                    var sub = new MenuItem { Header = g.Key };
                    parent.Items.Add(sub);
                    target = sub;
                }
                foreach (var p in g.OrderBy(displayName, StringComparer.CurrentCultureIgnoreCase))
                {
                    var item = p;
                    var mi = new MenuItem { Header = displayName(item) };
                    if (isSelected != null && isSelected(item))
                        mi.Icon = new TextBlock { Text = "✓", FontWeight = FontWeights.Bold };
                    mi.Click += (s, e) => onPick(item);
                    if (auditionId != null) Screens.KotonInstrumentAudition.Attach(mi, auditionId(item));
                    target.Items.Add(mi);
                }
            }
        }
    }
}
