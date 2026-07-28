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
    /// <summary>Hooks partagés entre les éditeurs polyrythmiques et l'écran hôte : évite un couplage direct avec
    /// TimelineScreen sans imposer un event par action.</summary>
    public class PolyEditorHost
    {
        public TimelineProject Project;
        public Action<string> PushUndo;
        public Action Render;
        public Action<TimelineTrack, TimelineItem> SelectItem;
        public ContentControl EditorHost;
        public Action<Window, string> EnsureSoundFont;
    }

    /// <summary>Un IValueConverter Lane → SolidColorBrush, pour peindre le liseré gauche de chaque carte de calque
    /// à la couleur de famille de l'instrument (DrumColors). Impossible à faire proprement en pur XAML.</summary>
    public class LaneToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => new SolidColorBrush(DrumColors.ForLane(value is int i ? i : 0));
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>Éditeur d'un module <see cref="PolyDrumModule"/>. Squelette + DataTemplate en XAML ; le code-behind
    /// ne fait plus que : gérer les events (Click, LostFocus), tenir la lecture audio, et retourner l'ItemsSource
    /// aux bindings. La mise à jour de l'affichage suit les notifications INotifyPropertyChanged du modèle.</summary>
    public partial class PolyDrumEditor : UserControl
    {
        readonly TimelineTrack track;
        readonly TimelineItem item;
        readonly PolyDrumModule pd;
        readonly PolyEditorHost host;

        NAudio.Wave.WaveOutEvent wave;
        LoopingRiffProvider provider;
        System.Windows.Threading.DispatcherTimer timer;

        public PolyDrumEditor(TimelineTrack track, TimelineItem item, PolyDrumModule pd, PolyEditorHost host)
        {
            InitializeComponent();
            this.track = track; this.item = item; this.pd = pd; this.host = host;

            LaneNames = DrumPattern.LaneNames;
            // « (aucun) » en premier → l'index 0 vaut « pas d'accent » (mappé sur AccentLane=-1 via AccentLaneIndex).
            var accent = new System.Collections.Generic.List<string> { "— " + Loc.T("AucunAccent") + " —" };
            accent.AddRange(DrumPattern.LaneNames);
            AccentLaneNames = accent;
            StepNames = new[] { Loc.T("Noire"), Loc.T("Croche"), Loc.T("DoubleCroche"), Loc.T("TrioletDeCroche") };
            LayersView = pd.Layers;
            // Les bindings {ElementName=self, Path=...} évaluent leur cible AVANT que le constructeur ait fini de
            // remplir les propriétés, et les propriétés CLR simples ne notifient pas ; on assigne donc les
            // ItemsSource directement, c'est court et fiable.
            layersList.ItemsSource = LayersView;

            btnPlay.Content = "▶ " + Loc.T("Ecouter");
            btnAdd.Content = "＋ " + Loc.T("AjouterUnCalque");
            txtDuration.Text = pd.DurationBeats.ToString();

            // Cycle de vie : abonnements en constructeur, désabonnement au Unload. Le modèle (pd) survit à
            // l'éditeur ; si on n'unsubscribe pas, le handler garde le UserControl en vie (fuite) et Render() finit
            // invoqué depuis un éditeur remplacé.
            SubscribeLayers();
            pd.Layers.CollectionChanged += Layers_CollectionChanged;
            wheel.CellClicked += Wheel_CellClicked;
            Unloaded += (_, __) => Dispose();

            RedrawAll();
        }

        bool disposed;
        void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stop();                                                                 // coupe l'audio + le timer
            pd.Layers.CollectionChanged -= Layers_CollectionChanged;
            foreach (var l in pd.Layers) l.PropertyChanged -= Layer_PropertyChanged;
            wheel.CellClicked -= Wheel_CellClicked;
        }

        void SubscribeLayers()
        {
            foreach (var l in pd.Layers) l.PropertyChanged += Layer_PropertyChanged;
        }

        void Layers_CollectionChanged(object s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (INotifyPropertyChanged o in e.OldItems) if (o != null) o.PropertyChanged -= Layer_PropertyChanged;
            if (e.NewItems != null) foreach (INotifyPropertyChanged o in e.NewItems) if (o != null) o.PropertyChanged += Layer_PropertyChanged;
            RedrawAll();
        }

        void Layer_PropertyChanged(object sender, PropertyChangedEventArgs e) => RedrawAll();

        void RedrawAll()
        {
            pd.Touch();
            wheel.SetModule(pd);
            wheel.InvalidateVisual();
            host.Render?.Invoke();
        }

        // ---- ItemsSource + ressources bindées ---------------------------------------------------------------
        public ObservableCollection<EuclidLayer> LayersView { get; private set; }
        public System.Collections.Generic.IReadOnlyList<string> LaneNames { get; private set; }
        /// <summary>Comme <see cref="LaneNames"/>, mais préfixé par « (aucun) » — utilisé par le ComboBox « Accent »
        /// (0 = pas d'accent, 1..N = lane N-1). Se marie avec <see cref="EuclidLayer.AccentLaneIndex"/>.</summary>
        public System.Collections.Generic.IReadOnlyList<string> AccentLaneNames { get; private set; }
        public string[] StepNames { get; private set; }

        // ---- clic-cellule sur la roue -----------------------------------------------------------------------
        void Wheel_CellClicked(int layerIndex, int step)
        {
            if (layerIndex < 0 || layerIndex >= pd.Layers.Count) return;
            var lay = pd.Layers[layerIndex];
            if (lay == null || !lay.CustomMode) return;
            host.PushUndo?.Invoke("poly:custom");
            var set = new System.Collections.Generic.HashSet<int>();
            if (lay.CustomHits != null) foreach (var p in lay.CustomHits) if (p >= 0 && p < lay.Steps) set.Add(p);
            if (!set.Add(step)) set.Remove(step);
            var arr = new int[set.Count]; int k = 0; foreach (var p in set) arr[k++] = p; Array.Sort(arr);
            lay.CustomHits = arr;   // notifie → redraw
        }

        // ---- événements du DataTemplate ---------------------------------------------------------------------
        // Chaque handler retrouve son calque via sender.DataContext, comme on le fait avec un templated control.
        static EuclidLayer From(object sender) => (sender as FrameworkElement)?.DataContext as EuclidLayer;

        void Layer_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var l = From(sender); if (l == null) return;
            wheel.SetHighlight(pd.Layers.IndexOf(l));
        }
        void Layer_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => wheel.SetHighlight(-1);

        void btnCollapse_Click(object sender, RoutedEventArgs e)
        {
            var l = From(sender); if (l == null) return;
            l.Collapsed = !l.Collapsed;
        }

        void chkCustom_Click(object sender, RoutedEventArgs e)
        {
            // Sémantique du toggle : au passage à custom, on seede CustomHits depuis E(K,N) courant pour que
            // l'utilisateur reparte de ce qu'il voit. Le CheckBox a déjà écrit CustomMode via le Binding TwoWay.
            var l = From(sender); if (l == null) return;
            host.PushUndo?.Invoke("poly:custom");
            if (l.CustomMode && l.CustomHits == null)
                l.CustomHits = RhythmAnalysis.ToPositions(EuclideanRhythm.Rotate(EuclideanRhythm.Pattern(l.Hits, l.Steps), l.Rotation));
        }

        void btnDelete_Click(object sender, RoutedEventArgs e)
        {
            var l = From(sender); if (l == null) return;
            host.PushUndo?.Invoke("poly:layer");
            pd.Layers.Remove(l);
        }

        void txtDuration_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtDuration.Text, out int v)) { pd.DurationBeats = v; RedrawAll(); }
        }

        void btnAdd_Click(object sender, RoutedEventArgs e)
        {
            host.PushUndo?.Invoke("poly:layer");
            int[] lanes = { 0, 1, 2, 11, 6 };
            int[,] kn = { { 3, 8 }, { 2, 8 }, { 7, 16 }, { 5, 8 }, { 3, 5 } };
            int i = Math.Min(pd.Layers.Count, lanes.Length - 1);
            pd.Layers.Add(new EuclidLayer { Lane = lanes[i], Hits = kn[i, 0], Steps = kn[i, 1], StepSlices = 12 });
        }

        void btnFreeze_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Loc.T("FigerPerdLesParametres"), Loc.T("FigerEnMotifEditable"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            host.PushUndo?.Invoke("poly:freeze");
            // Le module figé (batterie ordinaire) a besoin d'un BeatsPerBar : on le dérive de la longueur totale
            // du polyrythme, arrondie au temps supérieur, avec Repeats = 1 pour garder toute la matière.
            int frozenBeats = Math.Max(1, (int)Math.Ceiling(PolyDrum.TotalBeats(pd)));
            var dpm = new DrumPatternModule { Kit = pd.Kit, Style = DrumPattern.CustomStyle, BeatsPerBar = frozenBeats, Repeats = 1 };
            dpm.SetCustomNotes(PolyDrum.ToNotes(pd), DrumPattern.SlicesPerQuarter, PolyDrum.TotalSlices(pd));
            dpm.CatCategory = "Personnalisé"; dpm.CatMotif = "Personnalisé";
            item.Module = dpm;
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
                var ctx = new FlowContext { GmProgram = InstrumentCatalog.DrumKitProgram(pd.Kit), Drum = true, Bpm = host.Project.MainBpm };
                provider = new LoopingRiffProvider(() => PolyDrum.Generate(pd), ctx);
                wave = new NAudio.Wave.WaveOutEvent { DesiredLatency = 120 };
                wave.Init(provider); wave.Play();
                btnPlay.Content = "■ " + Loc.T("Stop");
                timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                timer.Tick += (s2, e2) => { if (provider != null) wheel.SetPlayhead(provider.CurrentSlice / (double)DrumPattern.SlicesPerQuarter); };
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
