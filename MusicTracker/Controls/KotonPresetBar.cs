using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KotonStudio.Library;
using MusicTracker.Localization;

namespace MusicTracker.Controls
{
    /// <summary>
    /// Barre « Preset » posée par l'HÔTE au-dessus de l'éditeur d'un plugin Koton, quel que soit son type
    /// (instrument, effet, générateur, contrainte). Aucun plugin n'a rien à implémenter pour en profiter :
    /// tout passe par <see cref="IKotonPlugin.Parameters"/> et <see cref="IKotonPlugin.SaveState"/>, que
    /// le contrat impose déjà à tout le monde.
    ///
    /// Un combo ÉDITABLE plutôt qu'un combo + une boîte de dialogue « nommer le preset » : le nom affiché
    /// est déjà un champ de saisie, donc enregistrer sous un nouveau nom = taper dedans puis 💾, et
    /// écraser = choisir dans la liste puis 💾. Un aller-retour de dialogue en moins pour le geste le plus
    /// courant.
    ///
    /// Les réglages sont posés sur l'instance VIVANTE du plugin — celle que partagent l'éditeur et le
    /// moteur audio — donc un preset chargé pendant la lecture s'entend au buffer suivant.
    /// </summary>
    public sealed class KotonPresetBar : UserControl
    {
        readonly IKotonPlugin _plugin;
        readonly ComboBox _combo;
        bool _suppress;   // garde anti-boucle : repeupler le combo lève SelectionChanged.

        public KotonPresetBar(IKotonPlugin plugin)
        {
            _plugin = plugin;

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = Loc.T("PresetLabel"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 6, 0),
            };
            Grid.SetColumn(lbl, 0);
            grid.Children.Add(lbl);

            _combo = new ComboBox
            {
                IsEditable = true,
                FontSize = 11,
                MinWidth = 140,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Loc.T("PresetNameHint"),
            };
            _combo.SelectionChanged += Combo_SelectionChanged;
            Grid.SetColumn(_combo, 1);
            grid.Children.Add(_combo);

            var save = MakeButton("💾", Loc.T("PresetSaveTip"));
            save.Click += Save_Click;
            Grid.SetColumn(save, 2);
            grid.Children.Add(save);

            var del = MakeButton("🗑", Loc.T("PresetDeleteTip"));
            del.Click += Delete_Click;
            Grid.SetColumn(del, 3);
            grid.Children.Add(del);

            Content = grid;

            Reload();
            // Deux éditeurs du même plugin peuvent être ouverts (une piste et un insert) : chacun doit voir
            // ce que l'autre enregistre.
            KotonPresetCatalog.Changed += OnCatalogChanged;
            Unloaded += (s, e) => KotonPresetCatalog.Changed -= OnCatalogChanged;
        }

        static Button MakeButton(string glyph, string tip)
        {
            var b = new Button
            {
                Content = glyph,
                Width = 24,
                Height = 22,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = tip,
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 11,
            };
            if (Application.Current != null) b.Style = Application.Current.TryFindResource("TinyButton") as Style;
            return b;
        }

        void OnCatalogChanged(string pluginId)
        {
            if (_plugin != null && string.Equals(pluginId, _plugin.Id, StringComparison.Ordinal)) Reload();
        }

        /// <summary>Repeuple la liste depuis le catalogue en préservant le texte tapé (repeupler remet le
        /// combo à zéro, et perdre le nom en cours de frappe serait irritant).</summary>
        void Reload()
        {
            if (_plugin == null) return;
            string typed = _combo.Text;
            _suppress = true;
            try
            {
                _combo.Items.Clear();
                foreach (string n in KotonPresetCatalog.ListNames(_plugin.Id)) _combo.Items.Add(n);
                _combo.Text = typed;
            }
            finally { _suppress = false; }
        }

        void Combo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || _plugin == null) return;
            if (e.AddedItems == null || e.AddedItems.Count == 0) return;
            string name = e.AddedItems[0] as string;
            if (string.IsNullOrEmpty(name)) return;
            KotonPresetCatalog.Apply(_plugin.Id, name, _plugin);
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            string name = (_combo.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name)) { _combo.Focus(); return; }

            var owner = Window.GetWindow(this);
            if (KotonPresetCatalog.Exists(_plugin.Id, name) &&
                !Dialogs.ConfirmDialog.Ask(owner, Loc.T("PresetOverwriteTitle"),
                                           string.Format(Loc.T("PresetOverwriteMsg"), name),
                                           Loc.T("Save"), Loc.T("Cancel")))
                return;

            if (!KotonPresetCatalog.Save(_plugin, name))
            {
                MessageBox.Show(owner, Loc.T("PresetSaveFailed"), Loc.T("PresetLabel"),
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Reload();
            _suppress = true;
            try { _combo.SelectedItem = name; _combo.Text = name; }
            finally { _suppress = false; }
        }

        void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            string name = (_combo.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name) || !KotonPresetCatalog.Exists(_plugin.Id, name)) return;

            var owner = Window.GetWindow(this);
            if (!Dialogs.ConfirmDialog.Ask(owner, Loc.T("PresetDeleteTitle"),
                                           string.Format(Loc.T("PresetDeleteMsg"), name),
                                           Loc.T("Delete"), Loc.T("Cancel")))
                return;

            KotonPresetCatalog.Delete(_plugin.Id, name);
            Reload();
            _suppress = true;
            try { _combo.SelectedIndex = -1; _combo.Text = ""; }
            finally { _suppress = false; }
        }
    }
}
