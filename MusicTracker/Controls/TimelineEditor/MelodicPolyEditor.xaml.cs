using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MusicTracker.Engine;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Timeline;
using MusicTracker.Localization;

namespace MusicTracker.Controls.TimelineEditor
{
    /// <summary>Voice (0..2) → teal / amber / violet. Doit rester en phase avec les couleurs utilisées par la roue.</summary>
    public class VoiceToBrushConverter : IValueConverter
    {
        static readonly Color[] Cols = { Color.FromRgb(0x1F, 0xB6, 0xC3), Color.FromRgb(0xFF, 0xC1, 0x07), Color.FromRgb(0xC1, 0x6E, 0xE0) };
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int v = value is int i ? i : 0;
            return new SolidColorBrush(Cols[Math.Max(0, Math.Min(Cols.Length - 1, v))]);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public partial class MelodicPolyEditor : UserControl
    {
        readonly TimelineTrack track;
        readonly TimelineItem item;
        readonly MelodicPolyModule mp;
        readonly PolyEditorHost host;

        NAudio.Wave.WaveOutEvent wave;
        LoopingRiffProvider provider;
        System.Windows.Threading.DispatcherTimer timer;

        public MelodicPolyEditor(TimelineTrack track, TimelineItem item, MelodicPolyModule mp, PolyEditorHost host)
        {
            InitializeComponent();
            this.track = track; this.item = item; this.mp = mp; this.host = host;

            StepNames = new[] { Loc.T("Noire"), Loc.T("Croche"), Loc.T("DoubleCroche"), Loc.T("TrioletDeCroche") };
            LayersView = mp.Layers;
            // Assignation directe : les bindings ElementName sont évalués trop tôt, on court-circuite.
            layersList.ItemsSource = LayersView;

            btnPlay.Content = "▶ " + Loc.T("Ecouter");
            btnAdd.Content = "＋ " + Loc.T("AjouterUnCalque");
            txtBeats.Text = mp.Beats.ToString();
            txtRepeats.Text = mp.Repeats.ToString();

            // Abonnements symétriques : cf. PolyDrumEditor pour le pourquoi (le modèle survit à l'éditeur).
            SubscribeVoices();
            mp.Layers.CollectionChanged += Layers_CollectionChanged;
            wheel.CellClicked += Wheel_CellClicked;
            Unloaded += (_, __) => Dispose();

            UpdateAddDeleteVisibility();
            RedrawAll();
        }

        bool disposed;
        void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stop();
            mp.Layers.CollectionChanged -= Layers_CollectionChanged;
            foreach (var v in mp.Layers) v.PropertyChanged -= Voice_PropertyChanged;
            wheel.CellClicked -= Wheel_CellClicked;
        }

        void SubscribeVoices() { foreach (var v in mp.Layers) v.PropertyChanged += Voice_PropertyChanged; }

        void Layers_CollectionChanged(object s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (INotifyPropertyChanged o in e.OldItems) if (o != null) o.PropertyChanged -= Voice_PropertyChanged;
            if (e.NewItems != null) foreach (INotifyPropertyChanged o in e.NewItems) if (o != null) o.PropertyChanged += Voice_PropertyChanged;
            UpdateAddDeleteVisibility();
            RedrawAll();
        }

        void Voice_PropertyChanged(object sender, PropertyChangedEventArgs e) => RedrawAll();

        void RedrawAll()
        {
            mp.Touch();
            var rings = new System.Collections.Generic.List<PolyDrumWheel.Ring>();
            foreach (var v in mp.Layers)
                if (v != null) rings.Add(new PolyDrumWheel.Ring
                {
                    Hits = v.Hits, Steps = v.Steps, Rotation = v.Rotation, StepSlices = 0, Muted = v.Muted,
                    Color = VoiceColour(v.Voice),
                    Custom = v.CustomMode ? v.EffectivePattern() : null,
                    Editable = v.CustomMode
                });
            // Nouveau modèle : la roue montre UN cycle du module (Beats temps) ; tous les anneaux bouclent
            // ensemble à la fin du cycle. Chaque anneau est simplement découpé en son propre Steps.
            int beats = MelodicEuclid.CycleBeats(mp);
            int spq = MelodicEuclid.SpqFor(mp);
            wheel.SetRings(rings, beats * spq, beats, beats);
            host.Render?.Invoke();
        }

        static Color VoiceColour(int voice)
        {
            var c = new[] { Color.FromRgb(0x1F, 0xB6, 0xC3), Color.FromRgb(0xFF, 0xC1, 0x07), Color.FromRgb(0xC1, 0x6E, 0xE0) };
            return c[Math.Max(0, Math.Min(c.Length - 1, voice))];
        }

        public ObservableCollection<EuclidVoice> LayersView { get; private set; }
        public string[] StepNames { get; private set; }
        // Bindé par la croix de suppression : cachée quand il n'y a qu'un seul calque, pour ne pas laisser
        // supprimer la dernière voix (le module deviendrait vide).
        public Visibility DeleteVisibility { get; private set; } = Visibility.Visible;

        void UpdateAddDeleteVisibility()
        {
            btnAdd.Visibility = mp.Layers.Count < MelodicLineModule.MaxVoices ? Visibility.Visible : Visibility.Collapsed;
            var nv = mp.Layers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (nv != DeleteVisibility) { DeleteVisibility = nv; }   // pas de PropertyChanged : on rebind ci-dessous
            // Le binding sur DeleteVisibility est initialisé une fois ; on force un refresh via un rebind manuel
            // en repropageant DataContext-like : plus simple, on invalide chaque item.
            var items = layersList.Items;
            for (int i = 0; i < items.Count; i++)
            {
                var container = layersList.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                container?.InvalidateProperty(FrameworkElement.DataContextProperty);
            }
        }

        void Wheel_CellClicked(int layerIndex, int step)
        {
            if (layerIndex < 0 || layerIndex >= mp.Layers.Count) return;
            var lay = mp.Layers[layerIndex];
            if (lay == null || !lay.CustomMode) return;
            host.PushUndo?.Invoke("mlpoly:custom");
            var set = new System.Collections.Generic.HashSet<int>();
            if (lay.CustomHits != null) foreach (var p in lay.CustomHits) if (p >= 0 && p < lay.Steps) set.Add(p);
            if (!set.Add(step)) set.Remove(step);
            var arr = new int[set.Count]; int k = 0; foreach (var p in set) arr[k++] = p; Array.Sort(arr);
            lay.CustomHits = arr;
        }

        static EuclidVoice From(object sender) => (sender as FrameworkElement)?.DataContext as EuclidVoice;

        void Layer_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var v = From(sender); if (v == null) return;
            wheel.SetHighlight(mp.Layers.IndexOf(v));
        }
        void Layer_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => wheel.SetHighlight(-1);

        void btnCollapse_Click(object sender, RoutedEventArgs e) { var v = From(sender); if (v != null) v.Collapsed = !v.Collapsed; }

        void chkCustom_Click(object sender, RoutedEventArgs e)
        {
            var v = From(sender); if (v == null) return;
            host.PushUndo?.Invoke("mlpoly:custom");
            if (v.CustomMode && v.CustomHits == null)
                v.CustomHits = RhythmAnalysis.ToPositions(EuclideanRhythm.Rotate(EuclideanRhythm.Pattern(v.Hits, v.Steps), v.Rotation));
        }

        void btnDelete_Click(object sender, RoutedEventArgs e)
        {
            var v = From(sender); if (v == null) return;
            host.PushUndo?.Invoke("mlpoly:layer");
            mp.Layers.Remove(v);
            MelodicEuclid.Renumber(mp.Layers);
        }

        void txtBeats_LostFocus(object sender, RoutedEventArgs e) { if (int.TryParse(txtBeats.Text, out int v)) { mp.Beats = v; RedrawAll(); } }
        void txtRepeats_LostFocus(object sender, RoutedEventArgs e) { if (int.TryParse(txtRepeats.Text, out int v)) { mp.Repeats = v; RedrawAll(); } }

        void btnAdd_Click(object sender, RoutedEventArgs e)
        {
            if (mp.Layers.Count >= MelodicLineModule.MaxVoices) return;
            host.PushUndo?.Invoke("mlpoly:layer");
            int[] ks = { 3, 5, 7 }, ns = { 8, 8, 16 };
            int i = Math.Min(mp.Layers.Count, ks.Length - 1);
            mp.Layers.Add(new EuclidVoice { Voice = mp.Layers.Count, Hits = ks[i], Steps = ns[i] });
        }

        void btnFreeze_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Loc.T("FigerRiffPerdLesParametres"), Loc.T("FigerEnRiff"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            double startBeat = TimelineHelper.ItemStartBeat(track, item);
            var key = host.Project.Key ?? new Engine.Score.KeySignature();
            var riff = MelodicEuclid.Generate(mp, host.Project, TimelineHelper.RiffById, key, startBeat);
            if (riff?.Notes == null || riff.Notes.Count == 0) { MessageBox.Show(Loc.T("AucunCoupARienAFiger")); return; }
            host.PushUndo?.Invoke("mlpoly:freeze");
            riff.Name = "Ligne poly " + (RiffLibrary.Instance.Riffs.Count + 1);
            RiffLibrary.Instance.Riffs.Add(riff);
            item.Module = new PlayRiffModule { RiffId = riff.Id };
            host.SelectItem?.Invoke(track, item);
            host.Render?.Invoke();
        }

        void btnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (wave != null) { Stop(); wheel.SetPlayhead(-1); return; }
            var w = Window.GetWindow(this);
            host.EnsureSoundFont?.Invoke(w, "Playback");
            try
            {
                double startBeat = TimelineHelper.ItemStartBeat(track, item);
                var key = host.Project.Key ?? new Engine.Score.KeySignature();
                var preset = InstrumentCatalog.GetPreset(track.Instrument);
                var ctx = new FlowContext { GmProgram = preset?.PatchNumber ?? 0, Drum = preset?.BankNumber == InstrumentCatalog.DrumIndex, Bpm = host.Project.MainBpm };
                provider = new LoopingRiffProvider(() => MelodicEuclid.Generate(mp, host.Project, TimelineHelper.RiffById, key, startBeat), ctx);
                wave = new NAudio.Wave.WaveOutEvent { DesiredLatency = 120 };
                wave.Init(provider); wave.Play();
                btnPlay.Content = "■ " + Loc.T("Stop");
                timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                // Le riff mélodique utilise spq = MelodicEuclid.SpqFor(m) — même remarque que côté batterie.
                int spq = MelodicEuclid.SpqFor(mp);
                timer.Tick += (s2, e2) => { if (provider != null) wheel.SetPlayhead(provider.CurrentSlice / (double)spq); };
                timer.Start();
            }
            catch { Stop(); wheel.SetPlayhead(-1); }
        }

        public void Stop()
        {
            if (timer != null) { timer.Stop(); timer = null; }
            if (wave != null) { try { wave.Stop(); wave.Dispose(); } catch { } wave = null; }
            provider = null;
            btnPlay.Content = "▶ " + Loc.T("Ecouter");
        }
    }
}
