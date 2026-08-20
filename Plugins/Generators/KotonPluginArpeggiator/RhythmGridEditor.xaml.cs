using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KotonStudio.Library;

namespace KotonPluginArpeggiator
{
    /// <summary>
    /// Éditeur graphique de motif rythmique — une ligne par TEMPS empilée verticalement, chaque
    /// ligne contient N cellules (SlicesPerBeat). Comportement calqué sur
    /// <c>MusicTracker.Controls.RhythmGridControl</c> (éditeur de ligne mélodique) :
    /// <list type="bullet">
    /// <item>Press sur cellule VIDE → dessine une note (start = slice sous la souris)</item>
    /// <item>Drag horizontal DANS la même ligne (même temps) → étend la note</item>
    /// <item>Release → commit la note (fusion avec notes adjacentes si contigües)</item>
    /// <item>Press sur cellule OCCUPÉE → supprime la SLICE touchée (raccourcit / split la note,
    ///       PAS suppression de la note entière — permet d'éditer finement)</item>
    /// </list>
    ///
    /// **Sans Rebuild pendant le drag** : un rectangle preview est mis à jour in-place au MouseMove.
    /// Rebuild uniquement au release / création / suppression → la capture souris est préservée
    /// (autrement le rebuild détruisait le canvas et perdait le drag).
    ///
    /// **Autonome** : ce contrôle vit ENTIÈREMENT dans le plugin, aucune dépendance MusicTracker.
    /// Il n'utilise que <see cref="KotonRhythm"/> du SDK et l'API WPF standard.
    /// </summary>
    public partial class RhythmGridEditor : UserControl
    {
        const double CellW = 24;
        const double CellH = 28;
        const double LabelW = 32;

        static readonly int[] BeatsOptions = { 1, 2, 3, 4, 5, 6, 7, 8 };
        // Slices par temps : 2/4/8/16 = subdivisions binaires (croches, doubles, triples, quadruples).
        // 3/6/12/24 = subdivisions ternaires (triplet de croches, sextolet, etc.). Permet aux motifs
        // ternaires (rythmes 6/8, swing, triolets) d'aligner leurs notes sur des slices exactes.
        static readonly int[] SpbOptions   = { 2, 3, 4, 6, 8, 12, 16, 24 };

        readonly List<Note> _notes = new List<Note>();
        int _beats = 2;
        int _spb = 4;

        // État du geste courant.
        enum Gesture { None, Draw, Erase }
        Gesture _gesture = Gesture.None;
        int _dragBeat = -1;
        int _dragStartSlice = -1;   // absolute slice où on a commencé à dessiner
        int _dragEndSlice = -1;     // dernière position atteinte
        Canvas _dragCanvas;         // canvas courant (garde la MouseCapture jusqu'au release)
        Rectangle _dragPreview;     // rectangle en cours (draw only, updated in-place)

        bool _updating;

        /// <summary>Émis à chaque édition (draw, erase, dimension). Le plugin lit <see cref="Save"/>.</summary>
        public event Action<KotonRhythm> RhythmChanged;

        public RhythmGridEditor()
        {
            InitializeComponent();

            foreach (var b in BeatsOptions) cboBeats.Items.Add(b);
            foreach (var s in SpbOptions) cboSpb.Items.Add(s);

            cboBeats.SelectionChanged += (_, __) =>
            {
                if (_updating) return;
                if (cboBeats.SelectedItem is int b) { _beats = b; TrimOutOfRange(); Rebuild(); Fire(); }
            };
            cboSpb.SelectionChanged += (_, __) =>
            {
                if (_updating) return;
                if (cboSpb.SelectedItem is int s && s != _spb)
                {
                    // Requantifier les notes : convertit les slices oldSpb → newSpb pour PRÉSERVER
                    // la position musicale de chaque note dans son temps. Sans ça, doubler spb
                    // ne changerait que la résolution → les notes existantes seraient poussées
                    // dans la première moitié (start restant absolu en oldSpb).
                    RequantizeNotes(_spb, s);
                    _spb = s;
                    TrimOutOfRange();
                    Rebuild();
                    Fire();
                }
            };

            Load(new KotonRhythm { Beats = _beats, SlicesPerBeat = _spb });
        }

        public void Load(KotonRhythm r)
        {
            if (r == null) r = new KotonRhythm();
            _beats = Math.Max(1, r.Beats);
            _spb = Math.Max(1, r.SlicesPerBeat);
            _notes.Clear();
            if (r.StartSlices != null && r.LenSlices != null)
            {
                int n = Math.Min(r.StartSlices.Length, r.LenSlices.Length);
                for (int i = 0; i < n; i++)
                    _notes.Add(new Note { Start = r.StartSlices[i], Len = Math.Max(1, r.LenSlices[i]) });
            }
            SyncCombos();
            Rebuild();
        }

        public KotonRhythm Save()
        {
            _notes.Sort((a, b) => a.Start.CompareTo(b.Start));
            var starts = new int[_notes.Count];
            var lens = new int[_notes.Count];
            for (int i = 0; i < _notes.Count; i++) { starts[i] = _notes[i].Start; lens[i] = _notes[i].Len; }
            return new KotonRhythm
            {
                Beats = _beats,
                SlicesPerBeat = _spb,
                StartSlices = starts,
                LenSlices = lens,
            };
        }

        void SyncCombos()
        {
            _updating = true;
            try
            {
                int bi = Array.IndexOf(BeatsOptions, _beats);
                cboBeats.SelectedIndex = bi >= 0 ? bi : 1;
                int si = Array.IndexOf(SpbOptions, _spb);
                cboSpb.SelectedIndex = si >= 0 ? si : 2;
            }
            finally { _updating = false; }
        }

        /// <summary>Convertit les positions des notes de <paramref name="oldSpb"/> vers
        /// <paramref name="newSpb"/> en préservant la position musicale dans le temps. Ex : une note
        /// à slice 4 sur 4 slices/temps (= début du 2e temps) devient slice 8 sur 8 slices/temps
        /// (= même début du 2e temps). Arrondi au plus proche pour minimiser la dérive quand on
        /// réduit la résolution (ex. 8 → 4 : une note à slice 3 devient slice 2, pas slice 1).</summary>
        void RequantizeNotes(int oldSpb, int newSpb)
        {
            if (oldSpb <= 0 || newSpb <= 0 || oldSpb == newSpb) return;
            foreach (var n in _notes)
            {
                n.Start = (int)Math.Round((double)n.Start * newSpb / oldSpb);
                n.Len   = Math.Max(1, (int)Math.Round((double)n.Len * newSpb / oldSpb));
            }
        }

        void TrimOutOfRange()
        {
            int total = _beats * _spb;
            for (int i = _notes.Count - 1; i >= 0; i--)
            {
                if (_notes[i].Start >= total) _notes.RemoveAt(i);
                else if (_notes[i].Start + _notes[i].Len > total) _notes[i].Len = total - _notes[i].Start;
            }
        }

        void Fire() => RhythmChanged?.Invoke(Save());

        // --------- Notes helpers (draw / erase sur slices absolues) ---------

        // Trouve la note qui couvre `slice` (start <= slice < start+len). -1 si aucune.
        int FindNoteCovering(int slice)
        {
            for (int i = 0; i < _notes.Count; i++)
                if (_notes[i].Start <= slice && slice < _notes[i].Start + _notes[i].Len) return i;
            return -1;
        }

        // Marque `slice` comme occupée (fusionne avec des voisines si contigües sur le même temps).
        // Confined to the beat containing `slice` — on ne traverse pas la barre de temps.
        void PaintSlice(int slice)
        {
            if (FindNoteCovering(slice) >= 0) return;
            int beat = slice / _spb;
            int beatStart = beat * _spb;
            int beatEnd = beatStart + _spb;

            // On cherche un voisin à gauche (fin == slice) et un à droite (start == slice+1) — même beat.
            int leftIdx = -1, rightIdx = -1;
            for (int i = 0; i < _notes.Count; i++)
            {
                if (_notes[i].Start < beatStart || _notes[i].Start >= beatEnd) continue;
                if (_notes[i].Start + _notes[i].Len == slice) leftIdx = i;
                else if (_notes[i].Start == slice + 1) rightIdx = i;
            }
            if (leftIdx >= 0 && rightIdx >= 0)
            {
                // Fusion 3-parties : left + 1 slice + right → left absorbe tout
                _notes[leftIdx].Len += 1 + _notes[rightIdx].Len;
                _notes.RemoveAt(rightIdx);
            }
            else if (leftIdx >= 0) _notes[leftIdx].Len += 1;
            else if (rightIdx >= 0) { _notes[rightIdx].Start -= 1; _notes[rightIdx].Len += 1; }
            else _notes.Add(new Note { Start = slice, Len = 1 });
        }

        // Efface la SLICE `slice` (raccourcit/split la note qui la couvre). Aucun effet si la slice est déjà libre.
        void EraseSlice(int slice)
        {
            int idx = FindNoteCovering(slice);
            if (idx < 0) return;
            var n = _notes[idx];
            if (n.Start == slice && n.Len == 1)
            {
                _notes.RemoveAt(idx);
                return;
            }
            if (slice == n.Start)
            {
                n.Start += 1; n.Len -= 1;
                if (n.Len <= 0) _notes.RemoveAt(idx);
            }
            else if (slice == n.Start + n.Len - 1)
            {
                n.Len -= 1;
                if (n.Len <= 0) _notes.RemoveAt(idx);
            }
            else
            {
                // Split : la note devient [Start, slice[ + une nouvelle [slice+1, end[
                int oldEnd = n.Start + n.Len;
                n.Len = slice - n.Start;
                _notes.Add(new Note { Start = slice + 1, Len = oldEnd - (slice + 1) });
            }
        }

        // --------- Rendu ---------

        void Rebuild()
        {
            beatsPanel.Children.Clear();
            for (int b = 0; b < _beats; b++) beatsPanel.Children.Add(BuildBeatRow(b));
        }

        UIElement BuildBeatRow(int beat)
        {
            var host = new Grid();
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelW) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_spb * CellW) });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellH) });

            var lbl = new TextBlock
            {
                Text = "T" + (beat + 1),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brushes.LightGray,
                FontSize = 11,
            };
            Grid.SetColumn(lbl, 0);
            host.Children.Add(lbl);

            var canvas = new Canvas
            {
                Width = _spb * CellW,
                Height = CellH,
                Background = Brushes.Transparent,
                Tag = beat,
            };
            // Cellules de fond — look Koton : gap visible entre chaque slice (pas de bord), coins
            // légèrement arrondis. Premier slice (début du temps) légèrement plus clair pour marquer
            // l'attaque.
            for (int s = 0; s < _spb; s++)
            {
                var rect = new Rectangle
                {
                    Width = CellW - 3,
                    Height = CellH - 4,
                    Fill = (s == 0) ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44))
                                    : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x32)),
                    RadiusX = 2,
                    RadiusY = 2,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, s * CellW + 1);
                Canvas.SetTop(rect, 2);
                canvas.Children.Add(rect);
            }

            int beatStart = beat * _spb;
            int beatEnd = beatStart + _spb;
            // Notes : teal Koton (#1FB6C3) — accent principal de l'app, coins arrondis, sans bord
            // dur (le teinte foncée en fill suffit à contraster avec le fond).
            var tealFill = new SolidColorBrush(Color.FromRgb(0x1F, 0xB6, 0xC3)); tealFill.Freeze();
            var tealBorder = new SolidColorBrush(Color.FromRgb(0x0F, 0x6E, 0x7A)); tealBorder.Freeze();
            foreach (var n in _notes)
            {
                if (n.Start < beatStart || n.Start >= beatEnd) continue;
                int offset = n.Start - beatStart;
                int drawLen = Math.Min(n.Len, _spb - offset);
                var noteRect = new Rectangle
                {
                    Width = drawLen * CellW - 4,
                    Height = CellH - 8,
                    Fill = tealFill,
                    Stroke = tealBorder,
                    StrokeThickness = 1,
                    RadiusX = 3,
                    RadiusY = 3,
                    IsHitTestVisible = false,   // le canvas gère TOUS les hits — permet le drag qui passe par-dessus les notes
                };
                Canvas.SetLeft(noteRect, offset * CellW + 2);
                Canvas.SetTop(noteRect, 4);
                canvas.Children.Add(noteRect);
            }

            canvas.MouseLeftButtonDown += Canvas_MouseDown;
            canvas.MouseMove += Canvas_MouseMove;
            canvas.MouseLeftButtonUp += Canvas_MouseUp;
            canvas.LostMouseCapture += Canvas_LostCapture;

            Grid.SetColumn(canvas, 1);
            host.Children.Add(canvas);

            return host;
        }

        // --------- Interactions ---------

        static int SliceFromPos(Canvas c, MouseEventArgs e, int spb)
        {
            var pos = e.GetPosition(c);
            int slice = (int)Math.Floor(pos.X / CellW);
            if (slice < 0) slice = 0;
            if (slice >= spb) slice = spb - 1;
            return slice;
        }

        void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Canvas c) || !(c.Tag is int beat)) return;
            int localSlice = SliceFromPos(c, e, _spb);
            int absSlice = beat * _spb + localSlice;

            _dragCanvas = c;
            _dragBeat = beat;
            _dragStartSlice = absSlice;
            _dragEndSlice = absSlice;

            if (FindNoteCovering(absSlice) >= 0)
            {
                // ERASE gesture : on efface la slice touchée immédiatement, puis on peut continuer à effacer
                // par drag (chaque slice traversée s'efface).
                _gesture = Gesture.Erase;
                EraseSlice(absSlice);
                Rebuild();
                Fire();
            }
            else
            {
                // DRAW gesture : preview rect en overlay, mis à jour au MouseMove SANS Rebuild.
                _gesture = Gesture.Draw;
                var tealPrev = new SolidColorBrush(Color.FromArgb(200, 0x1F, 0xB6, 0xC3)); tealPrev.Freeze();
                var tealBord = new SolidColorBrush(Color.FromRgb(0x0F, 0x6E, 0x7A)); tealBord.Freeze();
                _dragPreview = new Rectangle
                {
                    Height = CellH - 8,
                    Fill = tealPrev,
                    Stroke = tealBord,
                    StrokeThickness = 1,
                    RadiusX = 3,
                    RadiusY = 3,
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
            if (!(c.Tag is int beat) || beat != _dragBeat) return;   // v1 : reste dans le même temps
            int localSlice = SliceFromPos(c, e, _spb);
            int absSlice = beat * _spb + localSlice;
            if (absSlice == _dragEndSlice) return;

            if (_gesture == Gesture.Draw)
            {
                _dragEndSlice = absSlice;
                UpdatePreviewRect();  // in-place, pas de Rebuild
            }
            else if (_gesture == Gesture.Erase)
            {
                // Efface toutes les slices traversées entre _dragEndSlice et absSlice (dans les 2 sens).
                int lo = Math.Min(_dragEndSlice, absSlice);
                int hi = Math.Max(_dragEndSlice, absSlice);
                for (int s = lo; s <= hi; s++) EraseSlice(s);
                _dragEndSlice = absSlice;
                Rebuild();
                Fire();
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
            // Filet de sécurité : capture perdue pour une raison externe → finalise ce qu'on peut.
            if (_gesture != Gesture.None) FinishGesture();
        }

        void FinishGesture()
        {
            if (_gesture == Gesture.Draw)
            {
                int lo = Math.Min(_dragStartSlice, _dragEndSlice);
                int hi = Math.Max(_dragStartSlice, _dragEndSlice);
                // FUSION : toute note EXISTANTE dont l'intervalle intersecte [lo, hi] est absorbée
                // dans la nouvelle note (le range résultant = union). Reproduit le comportement de
                // l'éditeur de riff : "quand je dessine et que je passe sur une case déjà on, ça
                // fusionne". Les notes voisines NON traversées par le drag restent distinctes.
                for (int i = _notes.Count - 1; i >= 0; i--)
                {
                    var n = _notes[i];
                    int nEnd = n.Start + n.Len - 1;
                    if (n.Start <= hi && nEnd >= lo)
                    {
                        lo = Math.Min(lo, n.Start);
                        hi = Math.Max(hi, nEnd);
                        _notes.RemoveAt(i);
                    }
                }
                _notes.Add(new Note { Start = lo, Len = hi - lo + 1 });
                Rebuild();
                Fire();
            }
            _gesture = Gesture.None;
            _dragCanvas = null;
            _dragPreview = null;
            _dragBeat = -1;
            _dragStartSlice = _dragEndSlice = -1;
        }

        void UpdatePreviewRect()
        {
            if (_dragPreview == null) return;
            int lo = Math.Min(_dragStartSlice, _dragEndSlice);
            int hi = Math.Max(_dragStartSlice, _dragEndSlice);
            int beatStart = _dragBeat * _spb;
            int offset = lo - beatStart;
            int len = hi - lo + 1;
            int drawLen = Math.Min(len, _spb - offset);
            // Alignement identique aux notes committées (offset*CellW + 2, top 4, width -4)
            Canvas.SetLeft(_dragPreview, offset * CellW + 2);
            Canvas.SetTop(_dragPreview, 4);
            _dragPreview.Width = Math.Max(1, drawLen * CellW - 4);
        }

        // --------- Modèle interne ---------

        class Note
        {
            public int Start;
            public int Len;
        }
    }
}
