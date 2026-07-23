using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MusicTracker.Engine.Compose;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// "Insérer → Thème" : pick a 100 %-procedural generation TECHNIQUE (twelve-tone serial, cellular automaton,
    /// L-system, genetic, 1/f fractal, Thue-Morse — see <see cref="ProceduralComposer"/>), a length and a register.
    /// When the target is a structure's Thème section, an optional "Propager" reports the theme onto the derived
    /// sections with a chosen variation method. Code-only WPF, dark chrome (same look as <see cref="ImportTrackDialog"/>).
    /// </summary>
    public sealed class GenerateThemeDialog : Window
    {
        static readonly string[] TechLabels =
            { "Sérielle (dodécaphonique)", "Automate cellulaire", "L-système", "Génétique", "Fractale 1/f", "Thue-Morse" };
        static readonly ProceduralComposer.ProcTechnique[] TechValues =
        {
            ProceduralComposer.ProcTechnique.Serial, ProceduralComposer.ProcTechnique.CellularAutomaton,
            ProceduralComposer.ProcTechnique.LSystem, ProceduralComposer.ProcTechnique.Genetic,
            ProceduralComposer.ProcTechnique.Fractal1f, ProceduralComposer.ProcTechnique.ThueMorse,
        };
        static readonly string[] RegisterLabels = { "Grave", "Médium", "Aigu" };
        static readonly (int lo, int hi)[] RegisterRanges = { (48, 72), (60, 84), (67, 91) };

        readonly ComboBox cboTech = new ComboBox();
        readonly TextBox txtBars = new TextBox();
        readonly ComboBox cboRegister = new ComboBox();
        readonly CheckBox chkPropagate = new CheckBox();
        readonly ComboBox cboVarMethod = new ComboBox();

        public ProceduralComposer.ProcTechnique Technique { get; private set; }
        public int Bars { get; private set; } = 4;
        /// <summary>Chosen register as (lowMidi, highMidi).</summary>
        public (int lo, int hi) Register { get; private set; } = (60, 84);
        public bool Propagate { get; private set; }
        /// <summary>Variation technique for the propagation report: index into <see cref="ArrangementEngine.VariationNames"/> (0 = Auto).</summary>
        public int VariationTech { get; private set; }

        static Brush Res(string key) => (Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null) ?? Brushes.Gray;
        static Brush Grey => new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

        /// <param name="allowPropagate">true only when the target is a structure's Thème section.</param>
        public GenerateThemeDialog(bool allowPropagate)
        {
            Title = "Générer un thème";
            Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;

            foreach (var l in TechLabels) cboTech.Items.Add(l);
            cboTech.SelectedIndex = 0;
            txtBars.Text = "4"; txtBars.Width = 60; txtBars.HorizontalAlignment = HorizontalAlignment.Left;
            foreach (var l in RegisterLabels) cboRegister.Items.Add(l);
            cboRegister.SelectedIndex = 1;

            chkPropagate.Content = "Propager aux sections dérivées (ré-expo / développement / conclusion)";
            chkPropagate.Foreground = Res("CommonForeground");
            chkPropagate.IsEnabled = allowPropagate;
            chkPropagate.Margin = new Thickness(0, 6, 0, 0);

            cboVarMethod.Items.Add("Auto — l'algo choisit");
            for (int i = 1; i < ArrangementEngine.VariationNames.Length; i++) cboVarMethod.Items.Add(ArrangementEngine.VariationNames[i]);
            cboVarMethod.SelectedIndex = 0;
            cboVarMethod.IsEnabled = false;
            chkPropagate.Checked += (s, e) => cboVarMethod.IsEnabled = true;
            chkPropagate.Unchecked += (s, e) => cboVarMethod.IsEnabled = false;

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22), MinWidth = 380 };
            root.Children.Add(new TextBlock { Text = "🎵 Générer un thème (procédural)", Foreground = Res("CommonForeground"), FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            root.Children.Add(new TextBlock { Text = "Musique sérielle & algorithmique (aucun modèle appris). Voir docs/algorithmic-composition-nierhaus.md.", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), MaxWidth = 380 });

            root.Children.Add(Label("Technique"));
            root.Children.Add(cboTech);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var barsCol = new StackPanel(); barsCol.Children.Add(Label("Mesures")); barsCol.Children.Add(txtBars);
            var regCol = new StackPanel { Margin = new Thickness(20, 0, 0, 0) }; regCol.Children.Add(Label("Registre")); cboRegister.Width = 120; cboRegister.HorizontalAlignment = HorizontalAlignment.Left; regCol.Children.Add(cboRegister);
            row.Children.Add(barsCol); row.Children.Add(regCol);
            root.Children.Add(row);

            root.Children.Add(chkPropagate);
            root.Children.Add(Label("Méthode de variation (pour la propagation)"));
            root.Children.Add(cboVarMethod);

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

        static TextBlock Label(string t) => new TextBlock { Text = t, Foreground = Grey, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };

        void Accept()
        {
            int ti = cboTech.SelectedIndex; if (ti < 0) ti = 0;
            Technique = TechValues[ti];
            int bars; if (!int.TryParse((txtBars.Text ?? "").Trim(), out bars) || bars < 1) bars = 4;
            Bars = Math.Min(64, bars);
            int ri = cboRegister.SelectedIndex; if (ri < 0) ri = 1;
            Register = RegisterRanges[ri];
            Propagate = chkPropagate.IsEnabled && chkPropagate.IsChecked == true;
            VariationTech = Math.Max(0, cboVarMethod.SelectedIndex);   // 0 = Auto, else index into VariationNames
            DialogResult = true;
        }
    }
}
