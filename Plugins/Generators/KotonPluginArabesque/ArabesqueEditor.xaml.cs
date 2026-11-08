using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using KotonStudio.Library;
using Microsoft.Win32;

namespace KotonPluginArabesque
{
    /// <summary>
    /// Éditeur du générateur Arabesque : l'image et le chemin parcouru (points pleins : une note, points creux : un silence
    /// ou une respiration), tous les réglages (écrits directement dans les <see cref="KotonParameter"/>, donc audibles au
    /// prochain re-flatten), et les notes du bloc colorées par rôle harmonique — recalculées à chaque réglage, avec
    /// l'harmonie de la timeline sous le bloc.
    /// </summary>
    public partial class ArabesqueEditor : UserControl, IKotonEditor
    {
        readonly ArabesqueGenerator _plugin;
        readonly DispatcherTimer _refresh;
        KotonRenderContext _ctx;
        bool _loading = true;
        int _tsNum = 4;
        readonly List<Action> _syncs = new List<Action>();

        static readonly (NoteRole Role, Color Color, string Name)[] Roles =
        {
            (NoteRole.Chord,        Color.FromRgb(0x4F, 0x9D, 0xF7), "note d'accord"),
            (NoteRole.Passing,      Color.FromRgb(0x5C, 0xD6, 0x8A), "passage"),
            (NoteRole.Neighbor,     Color.FromRgb(0x2F, 0xB8, 0xA6), "broderie"),
            (NoteRole.Appoggiatura, Color.FromRgb(0xF2, 0x6B, 0x5B), "appoggiature"),
            (NoteRole.Resolution,   Color.FromRgb(0xF2, 0xB4, 0x4A), "résolution"),
            (NoteRole.Cadence,      Color.FromRgb(0xC7, 0x8B, 0xF0), "cadence (avant respiration)"),
        };

        public ArabesqueEditor(ArabesqueGenerator plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            _ctx = KotonHost.CurrentContext?.Invoke();
            if (_ctx != null && _ctx.TimeSigNum > 0) _tsNum = _ctx.TimeSigNum;

            foreach (var b in Ifs.Builtin) TraceCombo.Items.Add(b.Name);
            TraceCombo.Items.Add("Fichier .ifs...");
            ImageCombo.Items.Add("Mandala");
            ImageCombo.Items.Add("Fichier");
            foreach (var n in Fractals.Names) ImageCombo.Items.Add(n);
            BuildParams();
            foreach (var r in Roles)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
                sp.Children.Add(new Rectangle { Width = 14, Height = 8, Fill = new SolidColorBrush(r.Color), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = r.Name, FontSize = 11, Foreground = (Brush)FindResource("KotonTextDim") });
                Legend.Children.Add(sp);
            }

            _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _refresh.Tick += (s, e) => { _refresh.Stop(); Redraw(); };
            foreach (var kp in _plugin.Parameters) kp.Changed += _ => Dispatcher.BeginInvoke(new Action(Schedule));
            Unloaded += (s, e) => _refresh.Stop();

            Sync();
            _loading = false;
            Redraw();
        }

        // =============================================================================================
        // Les réglages : un libellé, un contrôle, sa valeur
        // =============================================================================================
        void BuildParams()
        {
            var cols = new List<Action<int>>
            {
                // colonne 1 : le rythme et le chemin
                c => { Combo(c, "Unité", "unit", "Blanche", "Noire", "Croche", "Triolet", "Double", "Sextolet");
                       Text(c, "Rythme écrit, dans l'ordre (vide : la couleur)", () => _plugin.Rhythm, v => _plugin.Rhythm = v);
                       Slide(c, "Densité", "density", v => v.ToString("F0") + " %");
                       Slide(c, "Tenue", "gate", v => v.ToString("F0") + " %");
                       Slide(c, "Graine", "seed", v => v.ToString("F0"));
                       Slide(c, "Phase", "phase", v => v.ToString("F0"));
                       Duration(c); },
                // colonne 2 : la hauteur et la couleur
                c => { Combo(c, "Notes proposées", "pool", "Gamme du projet", "Pentatonique", "Accord seul");
                       Slide(c, "Grave", "low", NoteName);
                       Slide(c, "Aigu", "high", NoteName);
                       Slide(c, "Saut max", "max_leap", v => v.ToString("F0") + " demi-tons");
                       Combo(c, "Hauteur ← couleur", "map_pitch", "Rouge", "Vert", "Bleu");
                       Combo(c, "Durée ← couleur", "map_dur", "Rouge", "Vert", "Bleu");
                       Combo(c, "Nuance ← couleur", "map_vel", "Rouge", "Vert", "Bleu"); },
                // colonne 3 : l'harmonie et la respiration
                c => { Slide(c, "Notes de passage, broderies", "passing", v => v.ToString("F0") + " %");
                       Slide(c, "Appoggiatures", "appogg", v => v.ToString("F0") + " %");
                       Slide(c, "Phrase", "phrase", v => v < 0.5 ? "sans respiration" : v.ToString("F0") + " temps");
                       Slide(c, "Respiration", "breath", v => v.ToString("0.##") + " temps");
                       Slide(c, "Nuances", "range", v => v.ToString("F0"));
                       Combo(c, "Articulation", "map_art", "Tenue fixe", "Rouge", "Vert", "Bleu"); },
            };
            for (int i = 0; i < cols.Count; i++) cols[i](i * 2);
        }

        readonly Dictionary<int, int> _rows = new Dictionary<int, int>();
        StackPanel Cell(int col, string label)
        {
            _rows.TryGetValue(col, out int row);
            _rows[col] = row + 1;
            while (ParamGrid.RowDefinitions.Count <= row) ParamGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(new TextBlock { Text = label, Foreground = (Brush)FindResource("KotonText") });
            Grid.SetColumn(sp, col); Grid.SetRow(sp, row);
            ParamGrid.Children.Add(sp);
            return sp;
        }
        KotonParameter P(string id) => _plugin.Parameters.First(p => p.Id == id);
        void Slide(int col, string label, string id, Func<double, string> fmt)
        {
            var kp = P(id);
            var sp = Cell(col, label);
            bool integral = kp.Max - kp.Min >= 4 && id != "breath";
            var s = new Slider { Minimum = kp.Min, Maximum = kp.Max, IsSnapToTickEnabled = true, TickFrequency = id == "breath" ? 0.25 : integral ? 1 : 0.01 };
            var val = new TextBlock { Foreground = (Brush)FindResource("KotonTextDim"), FontSize = 11 };
            s.ValueChanged += (o, e) => { val.Text = fmt(e.NewValue); if (!_loading) _plugin.SetParam(id, e.NewValue); };
            sp.Children.Add(s); sp.Children.Add(val);
            _syncs.Add(() => { s.Value = kp.Value; val.Text = fmt(kp.Value); });
        }
        void Combo(int col, string label, string id, params string[] items)
        {
            var kp = P(id);
            var sp = Cell(col, label);
            var cb = new ComboBox();
            foreach (var it in items) cb.Items.Add(it);
            cb.SelectionChanged += (o, e) => { if (!_loading && cb.SelectedIndex >= 0) _plugin.SetParam(id, cb.SelectedIndex); };
            sp.Children.Add(cb);
            _syncs.Add(() => cb.SelectedIndex = (int)Math.Round(kp.Value));
        }
        void Text(int col, string label, Func<string> get, Action<string> set)
        {
            var sp = Cell(col, label);
            var tb = new TextBox();
            tb.TextChanged += (o, e) => { if (!_loading) { set(tb.Text); Schedule(); } };
            sp.Children.Add(tb);
            _syncs.Add(() => tb.Text = get());
        }
        void Duration(int col)
        {
            var sp = Cell(col, "Durée du bloc");
            var s = new Slider { Minimum = 1, Maximum = 32, IsSnapToTickEnabled = true, TickFrequency = 1 };
            var val = new TextBlock { Foreground = (Brush)FindResource("KotonTextDim"), FontSize = 11 };
            s.ValueChanged += (o, e) =>
            {
                int m = (int)Math.Round(e.NewValue);
                val.Text = m + (m == 1 ? " mesure" : " mesures");
                if (_loading) return;
                _plugin.DurationBeats = m * _tsNum;
                KotonHost.NotifyDurationChanged?.Invoke(_plugin.DurationBeats);
                Schedule();
            };
            sp.Children.Add(s); sp.Children.Add(val);
            _syncs.Add(() => s.Value = Math.Max(1, Math.Round(_plugin.DurationBeats / _tsNum)));
        }
        static string NoteName(double v)
        {
            string[] n = { "do", "do♯", "ré", "mi♭", "mi", "fa", "fa♯", "sol", "la♭", "la", "si♭", "si" };
            int k = (int)Math.Round(v);
            return n[k % 12] + (k / 12 - 1) + " (" + k + ")";
        }
        void Sync()
        {
            bool was = _loading;
            _loading = true;
            foreach (var s in _syncs) s();
            TraceCombo.SelectedIndex = (int)_plugin.GetParam("trace");
            ImageCombo.SelectedIndex = (int)_plugin.GetParam("image");
            _loading = was;
        }

        // =============================================================================================
        // Le bandeau
        // =============================================================================================
        void TraceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || TraceCombo.SelectedIndex < 0) return;
            if (TraceCombo.SelectedIndex == Ifs.Builtin.Length)
            {
                var d = new OpenFileDialog { Filter = "Codes IFS (*.ifs)|*.ifs" };
                if (d.ShowDialog() != true) { _loading = true; TraceCombo.SelectedIndex = (int)_plugin.GetParam("trace"); _loading = false; return; }
                _plugin.TraceFile = d.FileName;
            }
            _plugin.SetParam("trace", TraceCombo.SelectedIndex);
        }
        void ImageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ImageCombo.SelectedIndex < 0) return;
            if (ImageCombo.SelectedIndex == 1 && !File.Exists(_plugin.ImageFile)) { PickImage_Click(null, null); return; }
            _plugin.SetParam("image", ImageCombo.SelectedIndex);
        }
        void PickImage_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" };
            if (d.ShowDialog() == true) { _plugin.ImageFile = d.FileName; _plugin.SetParam("image", 1); }
            Sync();
            Schedule();
        }
        void NewMandala_Click(object sender, RoutedEventArgs e) { _plugin.NewMandala(); Sync(); }
        void Reroll_Click(object sender, RoutedEventArgs e) { _plugin.RerollSeed(); Sync(); }
        void Preview_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ctx = KotonHost.CurrentContext?.Invoke() ?? new KotonRenderContext { Tonic = 0, IsMajor = true, Tempo = 120, TimeSigNum = 4, TimeSigDen = 4 };
                KotonHost.PreviewNotes?.Invoke(_plugin.RenderNotes(0, _plugin.DurationBeats, ctx).ToList());
            }
            catch (Exception ex) { KotonHost.ReportException?.Invoke(ex, "Arabesque"); }
        }
        void Stop_Click(object sender, RoutedEventArgs e) => KotonHost.StopPreview?.Invoke();

        public void OnContextUpdated(KotonRenderContext ctx)
        {
            _ctx = ctx;
            if (ctx != null && ctx.TimeSigNum > 0) _tsNum = ctx.TimeSigNum;
            Schedule();
        }

        // =============================================================================================
        // Les dessins : l'image et le chemin, les notes
        // =============================================================================================
        void Schedule() { _refresh.Stop(); _refresh.Start(); }

        void Redraw()
        {
            var ctx = _ctx ?? KotonHost.CurrentContext?.Invoke() ?? new KotonRenderContext { Tonic = 0, IsMajor = true, Tempo = 120, TimeSigNum = 4, TimeSigDen = 4 };
            var path = new List<(double U, double V, bool Played)>();
            List<RoledNote> notes;
            try { notes = _plugin.Compose(ctx, path); }
            catch (Exception ex) { KotonHost.ReportException?.Invoke(ex, "Arabesque"); return; }

            // l'image
            var pic = _plugin.CurrentPicture();
            var bmp = new WriteableBitmap(pic.W, pic.H, 96, 96, PixelFormats.Bgr32, null);
            bmp.WritePixels(new Int32Rect(0, 0, pic.W, pic.H), pic.Px, pic.W * 4, 0);
            PictureImage.Source = bmp;

            // le chemin
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var pen = new Pen(Brushes.White, 1.2);
                for (int i = 0; i < path.Count; i++)
                {
                    var p = new Point(path[i].U * 230, path[i].V * 230);
                    double a = 0.35 + 0.65 * i / Math.Max(1, path.Count - 1);
                    if (path[i].Played) dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(a * 255), 255, 255, 255)), null, p, 2.2, 2.2);
                    else dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(a * 200), 255, 255, 255)), 1), p, 2.2, 2.2);
                }
            }
            var rtb = new RenderTargetBitmap(230, 230, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            PathImage.Source = rtb;

            // les notes, colorées par rôle, sur les barres de mesure
            int w = 900, h = 110;
            double dur = Math.Max(1, _plugin.DurationBeats);
            int lo = notes.Count > 0 ? notes.Min(n => n.Note.MidiNote) - 2 : 48, hi = notes.Count > 0 ? notes.Max(n => n.Note.MidiNote) + 2 : 84;
            if (hi - lo < 12) { lo -= 6; hi += 6; }
            var dv2 = new DrawingVisual();
            using (var dc = dv2.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x27)), null, new Rect(0, 0, w, h));
                var bar = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4C)), 1);
                for (double b = 0; b <= dur; b += _tsNum) dc.DrawLine(bar, new Point(b / dur * w, 0), new Point(b / dur * w, h));
                foreach (var n in notes)
                {
                    var c = Roles.First(r => r.Role == n.Role).Color;
                    double x0 = n.Note.StartBeat / dur * w, x1 = (n.Note.StartBeat + n.Note.NotationDurationBeats) / dur * w;
                    double yy = h - 4 - (n.Note.MidiNote - lo) * (h - 8.0) / (hi - lo);
                    dc.DrawRoundedRectangle(new SolidColorBrush(c), null, new Rect(x0 + 0.5, yy - 2.5, Math.Max(2, x1 - x0 - 1), 5), 1.5, 1.5);
                }
            }
            var rtb2 = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb2.Render(dv2);
            RollImage.Source = rtb2;

            int nct = notes.Count(n => n.Role == NoteRole.Passing || n.Role == NoteRole.Neighbor || n.Role == NoteRole.Appoggiatura);
            bool chords = KotonHost.GetChordAt != null;
            InfoText.Text = notes.Count + " notes, dont " + nct + " étrangères à l'accord ; " + path.Count(p => !p.Played) + " silences."
                + (chords ? "" : "\nPas d'harmonie sous le bloc : l'accord de la tonique sert de repère.");
        }
    }
}
