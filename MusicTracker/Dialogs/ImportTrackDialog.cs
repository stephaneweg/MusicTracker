using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MusicTracker.Engine;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// Track picker for "Importer dans le riff": lists the tracks of an imported MIDI/MuseScore
    /// <see cref="MuseScoreImporter.Score"/> so the user chooses which one to load into the riff editor.
    /// Code-only WPF, matching the app's dark Border chrome (same look as <see cref="CreateStructureDialog"/>).
    /// The chosen track is in <see cref="SelectedTrack"/> once DialogResult == true.
    /// </summary>
    public sealed class ImportTrackDialog : Window
    {
        readonly ListBox list = new ListBox();
        readonly List<MuseScoreImporter.Track> tracks;

        /// <summary>The track the user picked (valid only when DialogResult == true).</summary>
        public MuseScoreImporter.Track SelectedTrack { get; private set; }

        static Brush Res(string key) => (Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null) ?? Brushes.Gray;

        public ImportTrackDialog(MuseScoreImporter.Score score, string fileName)
        {
            tracks = (score?.Tracks ?? new List<MuseScoreImporter.Track>()).Where(t => t != null).ToList();

            Title = "Importer une piste";
            Width = 470; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;

            // Build one row per track: "N. name · X notes · instrument". Pre-select the densest (most notes) track.
            var names = InstrumentCatalog.Names();
            int bestIdx = 0, bestCount = -1;
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                int nc = t.Notes != null ? t.Notes.Count : 0;
                string instr = t.IsDrum
                    ? "Batterie"
                    : (t.GmProgram >= 0 && t.GmProgram < names.Count ? names[t.GmProgram] : ("GM " + t.GmProgram));
                string label = string.Format("{0}. {1}  ·  {2} notes  ·  {3}",
                    i + 1, string.IsNullOrWhiteSpace(t.Name) ? "(sans nom)" : t.Name, nc, instr);
                list.Items.Add(new ListBoxItem { Content = label, Tag = i, Foreground = Res("CommonForeground"), Padding = new Thickness(4, 3, 4, 3) });
                if (nc > bestCount) { bestCount = nc; bestIdx = i; }
            }
            list.SelectedIndex = tracks.Count > 0 ? bestIdx : -1;
            list.Background = Res("TextBoxOuterBackground");
            list.BorderBrush = Res("TextBoxOuterBorder");
            list.Height = Math.Min(340, Math.Max(64, tracks.Count * 28 + 8));
            list.MouseDoubleClick += (s, e) => { if (list.SelectedItem != null) Accept(); };

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22), MinWidth = 380 };
            root.Children.Add(new TextBlock { Text = "🎼 Importer une piste dans le riff", Foreground = Res("CommonForeground"), FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
            root.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(fileName ?? ""), Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)), FontSize = 11, Margin = new Thickness(0, 0, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis });
            root.Children.Add(new TextBlock { Text = "Choisis la piste à charger dans le riff. La timeline n'est pas modifiée — tu édites le riff ensuite.", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), MaxWidth = 390 });
            root.Children.Add(list);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = new Button { Content = "Annuler", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand };
            var ok = new Button { Content = "Importer", Padding = new Thickness(16, 4, 16, 4), Cursor = System.Windows.Input.Cursors.Hand, IsDefault = true };
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
            int idx = (list.SelectedItem is ListBoxItem it && it.Tag is int i) ? i : -1;
            if (idx < 0 || idx >= tracks.Count) { DialogResult = false; return; }
            SelectedTrack = tracks[idx];
            DialogResult = true;
        }
    }
}
