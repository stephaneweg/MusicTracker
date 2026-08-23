using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KotonPluginSplineMelody
{
    /// <summary>
    /// Éditeur RYTHME POLYRYTHMIQUE — une carte par voix avec K (coups) / N (pas) / décalage /
    /// octave / legato. Le cycle est PARTAGÉ entre voix (poly_cycle_beats à l'échelle du générateur).
    /// Style aligné sur MelodicPolyEditor de l'app hôte : liseré coloré à gauche du bloc, champs
    /// numériques inline, checkbox Legato en bas.
    /// </summary>
    public sealed class PolyVoicePanel : UserControl
    {
        readonly Func<int, SplineMelodyGenerator.VoiceSpec> _getVoice;
        readonly Func<int> _getVoiceCount;
        readonly Func<double> _getCycleBeats;
        readonly Action<double> _setCycleBeats;

        public event Action Changed;

        readonly TextBox _txtCycle = new TextBox { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
        readonly StackPanel _cardsPanel = new StackPanel { Orientation = Orientation.Vertical };

        bool _updating;

        public PolyVoicePanel(Func<int, SplineMelodyGenerator.VoiceSpec> getVoice,
                              Func<int> getVoiceCount,
                              Func<double> getCycleBeats,
                              Action<double> setCycleBeats)
        {
            _getVoice = getVoice ?? throw new ArgumentNullException(nameof(getVoice));
            _getVoiceCount = getVoiceCount ?? throw new ArgumentNullException(nameof(getVoiceCount));
            _getCycleBeats = getCycleBeats ?? throw new ArgumentNullException(nameof(getCycleBeats));
            _setCycleBeats = setCycleBeats ?? throw new ArgumentNullException(nameof(setCycleBeats));

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            toolbar.Children.Add(new TextBlock
            {
                Text = "Temps du cycle :",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Foreground = Brushes.LightGray,
            });
            toolbar.Children.Add(_txtCycle);
            _txtCycle.LostFocus += (s, e) =>
            {
                if (_updating) return;
                if (double.TryParse(_txtCycle.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double d) && d >= 1)
                {
                    _setCycleBeats(d);
                    Changed?.Invoke();
                }
                else
                {
                    _txtCycle.Text = ((int)_getCycleBeats()).ToString(CultureInfo.InvariantCulture);
                }
            };
            Grid.SetRow(toolbar, 0);
            root.Children.Add(toolbar);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _cardsPanel,
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            Content = root;
        }

        public void ReloadFromModel()
        {
            _updating = true;
            try { _txtCycle.Text = ((int)_getCycleBeats()).ToString(CultureInfo.InvariantCulture); }
            finally { _updating = false; }
            Rebuild();
        }

        public void Rebuild()
        {
            _cardsPanel.Children.Clear();
            int vc = _getVoiceCount();
            for (int v = 0; v < vc; v++) _cardsPanel.Children.Add(BuildCard(v));
        }

        Border BuildCard(int voiceIdx)
        {
            var spec = _getVoice(voiceIdx);
            Color col = spec != null ? spec.Color : SplineMelodyGenerator.DefaultColorFor(voiceIdx);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(col),
                BorderThickness = new Thickness(3, 1, 1, 1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var stack = new StackPanel();

            // Header : chip + libelle
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            head.Children.Add(new Border
            {
                Width = 10, Height = 10,
                Background = new SolidColorBrush(col),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            head.Children.Add(new TextBlock
            {
                Text = "Voix " + (voiceIdx + 1),
                Foreground = new SolidColorBrush(col),
                FontSize = 12, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(head);

            // Rangée K / N / Décalage / Octave
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(BuildLabeledInt("Coups", spec.Hits, v => { spec.Hits = Math.Max(0, v); Changed?.Invoke(); }));
            row.Children.Add(BuildLabeledInt("Pas", spec.Steps, v => { spec.Steps = Math.Max(1, v); Changed?.Invoke(); }));
            row.Children.Add(BuildLabeledInt("Décalage", spec.Rotation, v => { spec.Rotation = v; Changed?.Invoke(); }));
            row.Children.Add(BuildLabeledInt("Octave", spec.OctaveOffset, v => { spec.OctaveOffset = Math.Max(-4, Math.Min(4, v)); Changed?.Invoke(); }));
            stack.Children.Add(row);

            // Legato
            var chkLegato = new CheckBox
            {
                Content = "Legato",
                IsChecked = spec.Legato,
                Foreground = Brushes.LightGray,
                FontSize = 11,
                ToolTip = "Tenir chaque coup jusqu'au suivant",
            };
            chkLegato.Checked += (s, e) => { spec.Legato = true; Changed?.Invoke(); };
            chkLegato.Unchecked += (s, e) => { spec.Legato = false; Changed?.Invoke(); };
            stack.Children.Add(chkLegato);

            border.Child = stack;
            return border;
        }

        static StackPanel BuildLabeledInt(string label, int value, Action<int> onChange)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            sp.Children.Add(new TextBlock
            {
                Text = label + " :",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.LightGray,
                FontSize = 11,
                Margin = new Thickness(0, 0, 4, 0),
            });
            var tb = new TextBox
            {
                Width = 40,
                Text = value.ToString(CultureInfo.InvariantCulture),
                VerticalAlignment = VerticalAlignment.Center,
            };
            tb.LostFocus += (s, e) =>
            {
                if (int.TryParse(tb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    onChange(v);
                else
                    tb.Text = value.ToString(CultureInfo.InvariantCulture);
            };
            sp.Children.Add(tb);
            return sp;
        }
    }
}
