using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KotonStudio.Library;

namespace KotonPluginSplineMelody
{
    /// <summary>
    /// Éditeur RYTHME multi-voix aligné sur un motif partagé (mêmes Beats + SlicesPerBeat pour toutes
    /// les voix). Une LIGNE = une voix, autant de COLONNES qu'il y a de slices dans le motif. Chaque
    /// voix dessine ses notes dans sa couleur.
    ///
    /// **Interactions par ligne** :
    ///   - Clic gauche sur cellule vide → START draw (crée une note d'1 slice, extensible en dragant
    ///     horizontalement dans la même ligne)
    ///   - Drag horizontal DANS la même ligne → étend la preview
    ///   - Release → commit + fusion avec voisines
    ///   - Clic gauche sur cellule occupée → EFFACE la slice (raccourcit / split la note ; drag
    ///     horizontal = efface une plage)
    ///
    /// **Contraintes** : le motif est PARTAGÉ (Beats + SlicesPerBeat identiques sur toutes les voix).
    /// Changer les combos requantifie et trim toutes les voix simultanément.
    /// </summary>
    public sealed class MultiVoiceRhythmGrid : UserControl
    {
        const double CellW = 20;
        const double CellH = 20;
        const double LabelW = 56;
        const double RowGap = 2;
        const double BeatSepW = 4;   // largeur d'une "cassure" entre 2 beats (petit gap + trait)
        static readonly int[] BeatsOptions = { 1, 2, 3, 4, 5, 6, 7, 8 };
        static readonly int[] SpbOptions   = { 2, 3, 4, 6, 8, 12, 16, 24 };

        readonly Func<int, SplineMelodyGenerator.VoiceSpec> _getVoice;
        readonly Func<int> _getVoiceCount;
        public event Action Changed;

        readonly ComboBox _cboBeats = new ComboBox { Width = 55, Margin = new Thickness(0, 0, 12, 0) };
        readonly ComboBox _cboSpb = new ComboBox { Width = 55 };
        readonly StackPanel _rowsPanel = new StackPanel { Orientation = Orientation.Vertical };

        bool _updating;
        int _beats = 2;
        int _spb = 4;

        enum Gesture { None, Draw, Erase }
        Gesture _gesture = Gesture.None;
        int _dragVoice = -1;
        int _dragStartSlice = -1;
        int _dragEndSlice = -1;
        Canvas _dragCanvas;
        Rectangle _dragPreview;

        public MultiVoiceRhythmGrid(Func<int, SplineMelodyGenerator.VoiceSpec> getVoice,
                                    Func<int> getVoiceCount)
        {
            _getVoice = getVoice ?? throw new ArgumentNullException(nameof(getVoice));
            _getVoiceCount = getVoiceCount ?? throw new ArgumentNullException(nameof(getVoiceCount));

            foreach (var b in BeatsOptions) _cboBeats.Items.Add(b);
            foreach (var s in SpbOptions) _cboSpb.Items.Add(s);

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            toolbar.Children.Add(new TextBlock { Text = "Temps :", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Foreground = Brushes.LightGray });
            toolbar.Children.Add(_cboBeats);
            toolbar.Children.Add(new TextBlock { Text = "Slices/temps :", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Foreground = Brushes.LightGray });
            toolbar.Children.Add(_cboSpb);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(toolbar, 0);
            root.Children.Add(toolbar);
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _rowsPanel,
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            Content = root;

            _cboBeats.SelectionChanged += (_, __) =>
            {
                if (_updating) return;
                if (_cboBeats.SelectedItem is int b && b != _beats)
                {
                    _beats = b;
                    // Trim toutes les voix : notes qui dépassent la nouvelle longueur sont raccourcies
                    // ou supprimées (fait dans SnapshotToRhythm/Rebuild).
                    ApplyDimsToAllVoices();
                    Rebuild();
                    Changed?.Invoke();
                }
            };
            _cboSpb.SelectionChanged += (_, __) =>
            {
                if (_updating) return;
                if (_cboSpb.SelectedItem is int s && s != _spb)
                {
                    // Requantifie chaque voix pour préserver la position musicale.
                    for (int v = 0; v < _getVoiceCount(); v++)
                    {
                        var spec = _getVoice(v);
                        if (spec == null || spec.Rhythm == null) continue;
                        RequantizeRhythm(spec.Rhythm, _spb, s);
                    }
                    _spb = s;
                    ApplyDimsToAllVoices();
                    Rebuild();
                    Changed?.Invoke();
                }
            };
        }

        /// <summary>Applique le premier motif de voix disponible (source de vérité) comme dimensions
        /// partagées + rebuild. Appelé par l'éditeur au chargement et à chaque changement de voix.</summary>
        public void ReloadFromModel()
        {
            _updating = true;
            try
            {
                // Prend Beats+Spb de la première voix comme référence (les autres seront alignées).
                var first = _getVoice(0);
                if (first != null && first.Rhythm != null)
                {
                    _beats = Math.Max(1, first.Rhythm.Beats);
                    _spb = Math.Max(1, first.Rhythm.SlicesPerBeat);
                }
                int bi = Array.IndexOf(BeatsOptions, _beats);
                _cboBeats.SelectedIndex = bi >= 0 ? bi : 1;
                int si = Array.IndexOf(SpbOptions, _spb);
                _cboSpb.SelectedIndex = si >= 0 ? si : 2;
                ApplyDimsToAllVoices();
            }
            finally { _updating = false; }
            Rebuild();
        }

        void ApplyDimsToAllVoices()
        {
            for (int v = 0; v < _getVoiceCount(); v++)
            {
                var spec = _getVoice(v);
                if (spec == null) continue;
                if (spec.Rhythm == null) spec.Rhythm = new KotonRhythm();
                if (spec.Rhythm.SlicesPerBeat != _spb) RequantizeRhythm(spec.Rhythm, spec.Rhythm.SlicesPerBeat, _spb);
                spec.Rhythm.Beats = _beats;
                spec.Rhythm.SlicesPerBeat = _spb;
                TrimRhythm(spec.Rhythm);
            }
        }

        static void RequantizeRhythm(KotonRhythm r, int oldSpb, int newSpb)
        {
            if (oldSpb <= 0 || newSpb <= 0 || oldSpb == newSpb) return;
            if (r.StartSlices == null || r.LenSlices == null) return;
            for (int i = 0; i < r.StartSlices.Length; i++)
            {
                r.StartSlices[i] = (int)Math.Round((double)r.StartSlices[i] * newSpb / oldSpb);
                r.LenSlices[i]   = Math.Max(1, (int)Math.Round((double)r.LenSlices[i] * newSpb / oldSpb));
            }
        }

        static void TrimRhythm(KotonRhythm r)
        {
            int total = r.Beats * r.SlicesPerBeat;
            if (r.StartSlices == null || r.LenSlices == null) return;
            var newStarts = new List<int>();
            var newLens = new List<int>();
            for (int i = 0; i < r.StartSlices.Length; i++)
            {
                int st = r.StartSlices[i];
                int ln = r.LenSlices[i];
                if (st >= total) continue;
                if (st + ln > total) ln = total - st;
                if (ln <= 0) continue;
                newStarts.Add(st);
                newLens.Add(ln);
            }
            r.StartSlices = newStarts.ToArray();
            r.LenSlices = newLens.ToArray();
        }

        void Rebuild()
        {
            _rowsPanel.Children.Clear();
            int vc = _getVoiceCount();
            for (int v = 0; v < vc; v++) _rowsPanel.Children.Add(BuildVoiceRow(v));
        }

        UIElement BuildVoiceRow(int voiceIdx)
        {
            var spec = _getVoice(voiceIdx);
            Color voiceCol = spec != null ? spec.Color : SplineMelodyGenerator.DefaultColorFor(voiceIdx);

            var host = new Grid { Margin = new Thickness(0, 0, 0, RowGap) };
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelW) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ComputeRowWidth()) });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellH) });

            // Chip + label voix
            var chipStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var chip = new Border
            {
                Width = 10, Height = 10,
                Background = new SolidColorBrush(voiceCol),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            chipStack.Children.Add(chip);
            chipStack.Children.Add(new TextBlock
            {
                Text = "V" + (voiceIdx + 1),
                Foreground = Brushes.LightGray,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(chipStack, 0);
            host.Children.Add(chipStack);

            int total = _beats * _spb;
            double canvasW = ComputeRowWidth();
            var canvas = new Canvas
            {
                Width = canvasW,
                Height = CellH,
                Background = Brushes.Transparent,
                Tag = voiceIdx,
            };
            // Cellules de fond UNIFORMES + séparateur fin entre chaque beat (gap horizontal +
            // trait vertical très discret). Style aligne sur l'editeur de rythme n-Track (matche
            // le screenshot user 2026-08-23).
            var cellBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x32)); cellBrush.Freeze();
            var sepBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55)); sepBrush.Freeze();
            for (int s = 0; s < total; s++)
            {
                double x = SliceLeftPx(s);
                var rect = new Rectangle
                {
                    Width = CellW - 2,
                    Height = CellH - 2,
                    Fill = cellBrush,
                    RadiusX = 2, RadiusY = 2,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, x + 1);
                Canvas.SetTop(rect, 1);
                canvas.Children.Add(rect);
            }
            // Séparateurs de beats : petit trait vertical fin dans le gap qui suit chaque beat.
            for (int b = 1; b < _beats; b++)
            {
                double x = SliceLeftPx(b * _spb) - BeatSepW / 2;
                canvas.Children.Add(new Rectangle
                {
                    Width = 1, Height = CellH,
                    Fill = sepBrush,
                    IsHitTestVisible = false,
                });
                Canvas.SetLeft(canvas.Children[canvas.Children.Count - 1], x);
                Canvas.SetTop(canvas.Children[canvas.Children.Count - 1], 0);
            }

            // Notes dessinées dans la couleur de la voix.
            if (spec != null && spec.Rhythm != null && spec.Rhythm.StartSlices != null && spec.Rhythm.LenSlices != null)
            {
                var fill = new SolidColorBrush(voiceCol); fill.Freeze();
                int n = Math.Min(spec.Rhythm.StartSlices.Length, spec.Rhythm.LenSlices.Length);
                for (int i = 0; i < n; i++)
                {
                    int st = spec.Rhythm.StartSlices[i];
                    int ln = spec.Rhythm.LenSlices[i];
                    if (st < 0 || st >= total) continue;
                    if (st + ln > total) ln = total - st;
                    double x = SliceLeftPx(st);
                    double width = SliceLeftPx(st + ln) - x - 2;   // -2 pour rester à l'intérieur des cellules
                    var noteRect = new Rectangle
                    {
                        Width = Math.Max(1, width),
                        Height = CellH - 4,
                        Fill = fill,
                        RadiusX = 2, RadiusY = 2,
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(noteRect, x + 1);
                    Canvas.SetTop(noteRect, 2);
                    canvas.Children.Add(noteRect);
                }
            }

            canvas.MouseLeftButtonDown += Canvas_MouseDown;
            canvas.MouseMove += Canvas_MouseMove;
            canvas.MouseLeftButtonUp += Canvas_MouseUp;
            canvas.LostMouseCapture += Canvas_LostCapture;

            Grid.SetColumn(canvas, 1);
            host.Children.Add(canvas);

            return host;
        }

        /// <summary>Position X (pixel) du bord gauche du slice <paramref name="s"/>. Prend en compte
        /// les gaps BeatSepW entre beats (le slice au début du beat 2 est déplacé de BeatSepW vers
        /// la droite, etc.).</summary>
        double SliceLeftPx(int s) => s * CellW + (s / _spb) * BeatSepW;

        double ComputeRowWidth() => _beats * _spb * CellW + Math.Max(0, _beats - 1) * BeatSepW;

        int SliceFromPos(Canvas c, MouseEventArgs e)
        {
            var pos = e.GetPosition(c);
            int total = _beats * _spb;
            // Scan linéaire — coût trivial (max ~192 slices).
            for (int s = 0; s < total; s++)
            {
                double left = SliceLeftPx(s);
                if (pos.X < left + CellW) return s;
            }
            return total - 1;
        }

        void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Canvas c) || !(c.Tag is int voiceIdx)) return;
            var spec = _getVoice(voiceIdx);
            if (spec == null) return;
            int slice = SliceFromPos(c, e);

            _dragCanvas = c;
            _dragVoice = voiceIdx;
            _dragStartSlice = slice;
            _dragEndSlice = slice;

            if (FindNoteCovering(spec.Rhythm, slice) >= 0)
            {
                _gesture = Gesture.Erase;
                EraseSlice(spec.Rhythm, slice);
                RebuildRow(c, spec, voiceIdx);
                Changed?.Invoke();
            }
            else
            {
                _gesture = Gesture.Draw;
                Color col = spec.Color;
                var prevFill = new SolidColorBrush(Color.FromArgb(200, col.R, col.G, col.B)); prevFill.Freeze();
                _dragPreview = new Rectangle
                {
                    Height = CellH - 8,
                    Fill = prevFill,
                    Stroke = new SolidColorBrush(Color.FromArgb(255, (byte)(col.R / 2), (byte)(col.G / 2), (byte)(col.B / 2))),
                    StrokeThickness = 1,
                    RadiusX = 3, RadiusY = 3,
                    IsHitTestVisible = false,
                };
                UpdatePreviewRect();
                c.Children.Add(_dragPreview);
            }
            c.CaptureMouse();
            e.Handled = true;
        }

        void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_gesture == Gesture.None || !(sender is Canvas c) || _dragCanvas != c) return;
            if (!(c.Tag is int voiceIdx) || voiceIdx != _dragVoice) return;
            int slice = SliceFromPos(c, e);
            if (slice == _dragEndSlice) return;

            if (_gesture == Gesture.Draw)
            {
                _dragEndSlice = slice;
                UpdatePreviewRect();
            }
            else if (_gesture == Gesture.Erase)
            {
                var spec = _getVoice(voiceIdx);
                if (spec == null) return;
                int lo = Math.Min(_dragEndSlice, slice);
                int hi = Math.Max(_dragEndSlice, slice);
                for (int s = lo; s <= hi; s++) EraseSlice(spec.Rhythm, s);
                _dragEndSlice = slice;
                RebuildRow(c, spec, voiceIdx);
                Changed?.Invoke();
            }
        }

        void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_gesture == Gesture.None) return;
            if (sender is Canvas c) c.ReleaseMouseCapture();
            FinishGesture();
        }

        void Canvas_LostCapture(object sender, MouseEventArgs e)
        {
            if (_gesture != Gesture.None) FinishGesture();
        }

        void FinishGesture()
        {
            if (_gesture == Gesture.Draw && _dragVoice >= 0)
            {
                var spec = _getVoice(_dragVoice);
                if (spec != null && spec.Rhythm != null)
                {
                    int lo = Math.Min(_dragStartSlice, _dragEndSlice);
                    int hi = Math.Max(_dragStartSlice, _dragEndSlice);
                    // Fusion : toute note qui touche [lo, hi] absorbée.
                    var starts = new List<int>(spec.Rhythm.StartSlices ?? Array.Empty<int>());
                    var lens = new List<int>(spec.Rhythm.LenSlices ?? Array.Empty<int>());
                    for (int i = starts.Count - 1; i >= 0; i--)
                    {
                        int st = starts[i], ln = lens[i];
                        int nEnd = st + ln - 1;
                        if (st <= hi && nEnd >= lo)
                        {
                            lo = Math.Min(lo, st);
                            hi = Math.Max(hi, nEnd);
                            starts.RemoveAt(i); lens.RemoveAt(i);
                        }
                    }
                    starts.Add(lo); lens.Add(hi - lo + 1);
                    var sortIdx = new List<int>();
                    for (int i = 0; i < starts.Count; i++) sortIdx.Add(i);
                    sortIdx.Sort((a, b) => starts[a].CompareTo(starts[b]));
                    var s2 = new int[starts.Count]; var l2 = new int[lens.Count];
                    for (int i = 0; i < sortIdx.Count; i++) { s2[i] = starts[sortIdx[i]]; l2[i] = lens[sortIdx[i]]; }
                    spec.Rhythm.StartSlices = s2;
                    spec.Rhythm.LenSlices = l2;
                    if (_dragCanvas != null) RebuildRow(_dragCanvas, spec, _dragVoice);
                    Changed?.Invoke();
                }
            }
            _gesture = Gesture.None;
            _dragCanvas = null;
            _dragPreview = null;
            _dragVoice = -1;
            _dragStartSlice = _dragEndSlice = -1;
        }

        void UpdatePreviewRect()
        {
            if (_dragPreview == null) return;
            int lo = Math.Min(_dragStartSlice, _dragEndSlice);
            int hi = Math.Max(_dragStartSlice, _dragEndSlice);
            double x = SliceLeftPx(lo);
            double width = SliceLeftPx(hi + 1) - x - 2;
            Canvas.SetLeft(_dragPreview, x + 1);
            Canvas.SetTop(_dragPreview, 2);
            _dragPreview.Height = CellH - 4;
            _dragPreview.Width = Math.Max(1, width);
        }

        void RebuildRow(Canvas c, SplineMelodyGenerator.VoiceSpec spec, int voiceIdx)
        {
            // Reconstruit UNIQUEMENT la ligne concernée (garde la capture souris qui vit sur le
            // canvas — un Rebuild() global détruit le canvas et perd la capture).
            var parent = c.Parent as Grid;
            if (parent == null) { Rebuild(); return; }
            int idx = _rowsPanel.Children.IndexOf(parent);
            if (idx < 0) { Rebuild(); return; }
            _rowsPanel.Children.RemoveAt(idx);
            _rowsPanel.Children.Insert(idx, BuildVoiceRow(voiceIdx));
        }

        // -----------------------------------------------------------------------------------------
        // Helpers de manipulation KotonRhythm en tableaux immuables
        // -----------------------------------------------------------------------------------------

        static int FindNoteCovering(KotonRhythm r, int slice)
        {
            if (r == null || r.StartSlices == null || r.LenSlices == null) return -1;
            int n = Math.Min(r.StartSlices.Length, r.LenSlices.Length);
            for (int i = 0; i < n; i++)
                if (r.StartSlices[i] <= slice && slice < r.StartSlices[i] + r.LenSlices[i]) return i;
            return -1;
        }

        static void EraseSlice(KotonRhythm r, int slice)
        {
            if (r == null) return;
            int idx = FindNoteCovering(r, slice);
            if (idx < 0) return;
            var starts = new List<int>(r.StartSlices);
            var lens = new List<int>(r.LenSlices);
            int st = starts[idx], ln = lens[idx];
            if (st == slice && ln == 1) { starts.RemoveAt(idx); lens.RemoveAt(idx); }
            else if (slice == st) { starts[idx] = st + 1; lens[idx] = ln - 1; if (lens[idx] <= 0) { starts.RemoveAt(idx); lens.RemoveAt(idx); } }
            else if (slice == st + ln - 1) { lens[idx] = ln - 1; if (lens[idx] <= 0) { starts.RemoveAt(idx); lens.RemoveAt(idx); } }
            else
            {
                // Split : [st..slice[ + [slice+1..end[
                int oldEnd = st + ln;
                lens[idx] = slice - st;
                starts.Add(slice + 1); lens.Add(oldEnd - (slice + 1));
                // Re-tri
                var sortIdx = new List<int>();
                for (int i = 0; i < starts.Count; i++) sortIdx.Add(i);
                sortIdx.Sort((a, b) => starts[a].CompareTo(starts[b]));
                var s2 = new int[starts.Count]; var l2 = new int[lens.Count];
                for (int i = 0; i < sortIdx.Count; i++) { s2[i] = starts[sortIdx[i]]; l2[i] = lens[sortIdx[i]]; }
                r.StartSlices = s2; r.LenSlices = l2;
                return;
            }
            r.StartSlices = starts.ToArray();
            r.LenSlices = lens.ToArray();
        }
    }
}
