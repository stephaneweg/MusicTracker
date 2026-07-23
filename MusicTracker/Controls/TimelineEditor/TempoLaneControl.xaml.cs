using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Controls.TimelineEditor
{
    /// <summary>
    /// The global tempo lane: bar ticks + a BPM marker at each tempo change. The initial tempo (beat 0)
    /// shows at the start; double-click a marker to edit its BPM inline (Enter / focus-out validates).
    /// Double-click an empty spot adds a tempo at the nearest beat, in edit mode. Added tempos get a small
    /// ✕ to delete them (the initial tempo can't be deleted). Mutates the shared tempo list in place.
    /// </summary>
    public partial class TempoLaneControl : UserControl
    {
        static readonly Brush BpmBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xDD, 0x66));
        static readonly Brush TickBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44));
        static readonly Brush MarkBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0x88, 0x44));
        static readonly Brush DelBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0x66, 0x66));

        const int MinBpm = 20, MaxBpm = 480;

        IList<TempoChange> tempo;
        double laneW, laneH, pxPerBeat;
        int editingIndex = -1;
        TextBox editBox;

        /// <summary>Raised after a tempo is added / edited / deleted.</summary>
        public event Action Changed;

        public TempoLaneControl()
        {
            InitializeComponent();
            canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
        }

        public void Configure(double width, double height, double pxPerBeat, IList<TempoChange> tempo)
        {
            this.laneW = width; this.laneH = height; this.pxPerBeat = pxPerBeat; this.tempo = tempo;
            canvas.Width = width; canvas.Height = height;
            editingIndex = -1;
            Redraw();
        }

        void Redraw()
        {
            canvas.Children.Clear();
            editBox = null;
            if (tempo == null) return;

            for (int b = 0; b * pxPerBeat < laneW; b += 4)
            {
                var tick = new Rectangle { Width = 1, Height = laneH, Fill = TickBrush };
                Canvas.SetLeft(tick, b * pxPerBeat); canvas.Children.Add(tick);
            }

            for (int i = 0; i < tempo.Count; i++) DrawMarker(i);

            if (editBox != null)
            {
                var tb = editBox;
                Dispatcher.BeginInvoke((Action)(() => { tb.Focus(); tb.SelectAll(); }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        void DrawMarker(int i)
        {
            var tc = tempo[i];
            double x = tc.Beat * pxPerBeat;
            bool isMain = i == 0; // the initial tempo (beat 0): not deletable

            if (!isMain)
            {
                var mark = new Rectangle { Width = 1, Height = laneH, Fill = MarkBrush };
                Canvas.SetLeft(mark, x); Canvas.SetTop(mark, 0); canvas.Children.Add(mark);
            }

            if (i == editingIndex)
            {
                var tb = new TextBox { Width = 42, Text = ((int)tc.Bpm).ToString(), FontSize = 11 };
                tb.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter) { CommitEdit(i, tb.Text); e.Handled = true; }
                    else if (e.Key == Key.Escape) { editingIndex = -1; Redraw(); e.Handled = true; }
                };
                tb.LostFocus += (s, e) => { if (editingIndex == i) CommitEdit(i, tb.Text); };
                Canvas.SetLeft(tb, x + 2); Canvas.SetTop(tb, 2); canvas.Children.Add(tb);
                editBox = tb;
                return;
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var txt = new TextBlock { Text = ((int)tc.Bpm).ToString(), Foreground = BpmBrush, FontSize = 11, Cursor = Cursors.Hand, ToolTip = "Double-clic pour modifier" };
            txt.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) { e.Handled = true; editingIndex = i; Redraw(); } };
            panel.Children.Add(txt);
            if (!isMain)
            {
                var del = new TextBlock { Text = "✕", Foreground = DelBrush, FontSize = 10, Margin = new Thickness(3, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = "Supprimer ce tempo" };
                del.MouseLeftButtonDown += (s, e) => { e.Handled = true; if (i < tempo.Count) tempo.RemoveAt(i); editingIndex = -1; Redraw(); Changed?.Invoke(); };
                panel.Children.Add(del);
            }
            Canvas.SetLeft(panel, x + 2); Canvas.SetTop(panel, 2); canvas.Children.Add(panel);
        }

        void CommitEdit(int i, string text)
        {
            if (i >= 0 && i < tempo.Count && int.TryParse((text ?? "").Trim(), out int bpm))
                tempo[i].Bpm = Math.Max(MinBpm, Math.Min(MaxBpm, bpm));
            editingIndex = -1;
            Redraw();
            Changed?.Invoke();
        }

        int IndexOfBeat(int beat)
        {
            for (int i = 0; i < tempo.Count; i++) if ((int)Math.Round(tempo[i].Beat) == beat) return i;
            return -1;
        }

        double BpmInEffectAt(int beat)
        {
            double bpm = tempo.Count > 0 ? tempo[0].Bpm : 120;
            foreach (var t in tempo) if (t.Beat <= beat) bpm = t.Bpm;
            return bpm;
        }

        void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2 || tempo == null) return; // double-click on empty space adds a tempo
            int beat = (int)Math.Round(e.GetPosition(canvas).X / pxPerBeat); // clamp to the nearest beat
            if (beat < 0) beat = 0;

            int existing = IndexOfBeat(beat);
            if (existing >= 0) { editingIndex = existing; Redraw(); return; } // already a tempo here -> edit it

            var tc = new TempoChange { Beat = beat, Bpm = BpmInEffectAt(beat) };
            tempo.Add(tc);
            editingIndex = tempo.IndexOf(tc);
            Redraw();
            Changed?.Invoke();
        }
    }
}
