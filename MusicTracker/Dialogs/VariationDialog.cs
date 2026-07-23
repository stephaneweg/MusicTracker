using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// "Insérer → Variation" : pick a variation TECHNIQUE to apply to the selected theme riff. Combines the tonal
    /// catalogue (<see cref="ArrangementEngine.VariationNames"/> → <see cref="ArrangementEngine.ApplyVariation"/>) with the
    /// book-inspired development ops (<see cref="RecipeRenderer.Develop"/>: augment/diminish/expand/spin/grow/thuemorse/evolve).
    /// Code-only WPF, dark chrome. On OK, either <see cref="CatalogTech"/> (when <see cref="IsDevelop"/> is false) or
    /// <see cref="DevelopOp"/> (when true) tells the caller which path to use.
    /// </summary>
    public sealed class VariationDialog : Window
    {
        // Book development ops (label → RecipeRenderer op token). Retrograde/Inversion already live in the catalogue.
        static readonly (string label, string op)[] DevelopOps =
        {
            ("Augmentation (durées ×2)", "augment"),
            ("Diminution (durées ÷2)", "diminish"),
            ("Intervalles élargis", "expand"),
            ("Fortspinnung (dévidage)", "spin"),
            ("L-système (dév.)", "grow"),
            ("Thue-Morse (dév.)", "thuemorse"),
            ("Génétique (dév.)", "evolve"),
        };

        readonly ComboBox cbo = new ComboBox();

        /// <summary>True → use <see cref="DevelopOp"/> via <see cref="RecipeRenderer.Develop"/>; false → use <see cref="CatalogTech"/>.</summary>
        public bool IsDevelop { get; private set; }
        /// <summary>Index into <see cref="ArrangementEngine.VariationNames"/> (0 = Auto) when <see cref="IsDevelop"/> is false.</summary>
        public int CatalogTech { get; private set; }
        /// <summary>RecipeRenderer op token when <see cref="IsDevelop"/> is true.</summary>
        public string DevelopOp { get; private set; }

        static Brush Res(string key) => (Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null) ?? Brushes.Gray;

        public VariationDialog()
        {
            Title = "Générer une variation";
            Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;

            // Catalogue first (Tag = ("cat", idx)), then the book ops (Tag = ("dev", token)).
            for (int i = 0; i < ArrangementEngine.VariationNames.Length; i++)
                cbo.Items.Add(new ComboBoxItem { Content = ArrangementEngine.VariationNames[i], Tag = Tuple.Create("cat", i.ToString()), Foreground = Res("CommonForeground") });
            foreach (var d in DevelopOps)
                cbo.Items.Add(new ComboBoxItem { Content = d.label, Tag = Tuple.Create("dev", d.op), Foreground = Res("CommonForeground") });
            cbo.SelectedIndex = 0;

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22), MinWidth = 360 };
            root.Children.Add(new TextBlock { Text = "🔀 Variation du thème", Foreground = Res("CommonForeground"), FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            root.Children.Add(new TextBlock { Text = "Applique une technique de variation au riff sélectionné (l'original est conservé).", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), MaxWidth = 360 });
            root.Children.Add(new TextBlock { Text = "Technique", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)), FontSize = 11, Margin = new Thickness(0, 0, 0, 3) });
            root.Children.Add(cbo);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var cancel = new Button { Content = "Annuler", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand };
            var ok = new Button { Content = "Générer", Padding = new Thickness(16, 4, 16, 4), Cursor = System.Windows.Input.Cursors.Hand, IsDefault = true };
            cancel.Click += (s, e) => { DialogResult = false; };
            ok.Click += (s, e) => Accept();
            btnRow.Children.Add(cancel); btnRow.Children.Add(ok);
            root.Children.Add(btnRow);

            Content = new Border
            {
                Background = Res("CommonBackground"),
                BorderBrush = Res("OutlineColorBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = root,
            };
        }

        void Accept()
        {
            var tag = (cbo.SelectedItem as ComboBoxItem)?.Tag as Tuple<string, string>;
            if (tag == null) { IsDevelop = false; CatalogTech = 0; }
            else if (tag.Item1 == "dev") { IsDevelop = true; DevelopOp = tag.Item2; }
            else { IsDevelop = false; int.TryParse(tag.Item2, out int idx); CatalogTech = idx; }
            DialogResult = true;
        }
    }
}
