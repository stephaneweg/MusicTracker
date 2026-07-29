using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
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
    /// <summary>Convertit un Degree stocké (-1 = fixe, 0..6 = degré diatonique) en/depuis l'index du ComboBox
    /// (0 = « Fixe », 1..7 = degré I..VII). En pur XAML on ne peut pas décaler un binding d'un cran.</summary>
    public class DegreeIndexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int i ? Math.Max(0, i + 1) : 0;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int i ? i - 1 : -1;
    }

    /// <summary>Éditeur d'un module <see cref="PolyChordModule"/>. Trois colonnes : options du module + liste
    /// d'accords, liste d'anneaux, roue polyrythmique. Le squelette est en XAML, ici on : bind les ItemsSource,
    /// gère la lecture, propage les changements de mode aux titres des anneaux, et applique la logique de dérivation
    /// « degré → root/qualité » sur les combos d'accords.</summary>
    public partial class PolyChordEditor : UserControl, INotifyPropertyChanged
    {
        readonly TimelineTrack track;
        readonly TimelineItem item;
        readonly PolyChordModule pc;
        readonly PolyEditorHost host;

        NAudio.Wave.WaveOutEvent wave;
        LoopingRiffProvider provider;
        System.Windows.Threading.DispatcherTimer timer;

        public PolyChordEditor(TimelineTrack track, TimelineItem item, PolyChordModule pc, PolyEditorHost host)
        {
            InitializeComponent();
            this.track = track; this.item = item; this.pc = pc; this.host = host;

            InitStaticLists();

            // Assignation directe : idem PolyDrumEditor/MelodicPolyEditor — les {ElementName=self, Path=...}
            // s'évaluent avant que le constructeur ait fini.
            chordsList.ItemsSource = pc.Chords;
            layersList.ItemsSource = pc.Layers;

            btnPlay.Content = "▶ " + Loc.T("Ecouter");
            btnAddChord.Content = "＋ " + Loc.T("AjouterUnAccord");
            btnAddLayer.Content = "＋ " + Loc.T("AjouterUnCalque");

            SyncModuleControls();
            SubscribeAll();
            pc.Chords.CollectionChanged += Collection_Changed;
            pc.Layers.CollectionChanged += Collection_Changed;
            wheel.CellClicked += Wheel_CellClicked;
            Unloaded += (_, __) => Dispose();

            RedrawAll();
        }

        void InitStaticLists()
        {
            var modes = new List<string> { Loc.T("PolyChordMultiAnneaux"), Loc.T("PolyChordUnAnneauSweep") };
            cboMode.ItemsSource = modes;
            cboMode.SelectedIndex = (int)pc.Mode;

            var restarts = new List<string> { Loc.T("RestartNearest"), Loc.T("RestartGrave"), Loc.T("RestartAigu"),
                                              Loc.T("RestartTonic"), Loc.T("RestartTierce"), Loc.T("RestartQuinte") };
            cboRestart.ItemsSource = restarts;
            cboRestart.SelectedIndex = (int)pc.Restart;

            var vla = new List<string> { Loc.T("AutoMouvementMini"), Loc.T("BasseProche"), Loc.T("HautProche") };
            cboVoiceLead.ItemsSource = vla;
            cboVoiceLead.SelectedIndex = pc.VoiceLeadAnchor;

            txtOctave.Text = pc.Octave.ToString();
            txtCycleBeats.Text = pc.CycleBeats.ToString();
            chkOpenVoicing.IsChecked = pc.OpenVoicing;

            // Combos accords : « Fixe » + I..VII (romains). Les V/x et couleurs avancées se règlent via le combo Qualité
            // (comme le dialogue d'ajout d'accord classique) — on garde l'UI simple.
            DegreeNames = new List<string> { Loc.T("ManuelAccordFixe"), "I", "II", "III", "IV", "V", "VI", "VII" };
            QualityNames = PatternGenerator.QualityNames;
            ColourNames = MusicTheory.DiatonicColourNames;
            SuspensionNames = MusicTheory.SuspensionNames;

            // Noms de contour (mode 1-anneau). Alignés sur ceux de MelodicLineEngine.ContourNames pour l'utilisateur.
            ContourNames = new List<string> { Loc.T("ContourVague"), Loc.T("ContourMontante"), Loc.T("ContourDescendante"),
                                              Loc.T("ContourStatique"), Loc.T("ContourZigzag"), Loc.T("ContourAleatoire") };
        }

        // ---- ItemsSource / bindings sur self ---------------------------------------------------------------
        public IReadOnlyList<string> DegreeNames { get; private set; }
        public IReadOnlyList<string> QualityNames { get; private set; }
        public IReadOnlyList<string> ColourNames { get; private set; }
        public IReadOnlyList<string> SuspensionNames { get; private set; }
        public IReadOnlyList<string> ContourNames { get; private set; }

        // Visibilité conditionnelle du bloc « Mode Multi » / « Mode Sweep » sur chaque carte d'anneau.
        // Change quand cboMode change → on notifie pour rafraîchir toutes les cartes d'un coup.
        public Visibility MultiVisibility => pc.Mode == PolyChordMode.OneRingPerTone ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SweepVisibility => pc.Mode == PolyChordMode.OneRingSweep ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler PropertyChanged;
        void OnPC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        // ---- cycle de vie ------------------------------------------------------------------------------------
        bool disposed;
        void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stop();
            pc.Chords.CollectionChanged -= Collection_Changed;
            pc.Layers.CollectionChanged -= Collection_Changed;
            foreach (var c in pc.Chords) c.PropertyChanged -= Item_PropertyChanged;
            foreach (var l in pc.Layers) l.PropertyChanged -= Item_PropertyChanged;
            wheel.CellClicked -= Wheel_CellClicked;
        }
        void SubscribeAll()
        {
            foreach (var c in pc.Chords) c.PropertyChanged += Item_PropertyChanged;
            foreach (var l in pc.Layers) l.PropertyChanged += Item_PropertyChanged;
        }
        void Collection_Changed(object s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (INotifyPropertyChanged o in e.OldItems) if (o != null) o.PropertyChanged -= Item_PropertyChanged;
            if (e.NewItems != null) foreach (INotifyPropertyChanged o in e.NewItems) if (o != null) o.PropertyChanged += Item_PropertyChanged;
            RedrawAll();
        }
        void Item_PropertyChanged(object sender, PropertyChangedEventArgs e) => RedrawAll();

        void SyncModuleControls()
        {
            // Un cboMode.SelectedIndex change → propage à pc.Mode. Ici c'est l'inverse, à l'ouverture.
            if (cboMode.SelectedIndex != (int)pc.Mode) cboMode.SelectedIndex = (int)pc.Mode;
            if (cboRestart.SelectedIndex != (int)pc.Restart) cboRestart.SelectedIndex = (int)pc.Restart;
            if (cboVoiceLead.SelectedIndex != pc.VoiceLeadAnchor) cboVoiceLead.SelectedIndex = pc.VoiceLeadAnchor;
        }

        void RedrawAll()
        {
            pc.Touch();
            // La revoice se chaîne aussi avec les modules voisins de la piste — on la déclenche ici pour que
            // les inversions/octaves soient à jour AVANT le rendu de la roue et de la timeline.
            Engine.Flow.ChordDegrees.Revoice(track);

            // Alimente la roue avec un anneau par calque.
            var rings = new List<PolyDrumWheel.Ring>();
            if (pc.Layers != null)
                foreach (var l in pc.Layers)
                    if (l != null) rings.Add(new PolyDrumWheel.Ring
                    {
                        Hits = l.Hits, Steps = l.Steps, Rotation = l.Rotation, StepSlices = 0, Muted = l.Muted,
                        Color = FuchsiaShade(pc.Layers.IndexOf(l)),
                        Custom = l.CustomMode ? l.EffectivePattern() : null,
                        Editable = l.CustomMode
                    });
            // La roue représente UN cycle du module (durée = CycleBeats), indépendamment des accords : c'est bien
            // ce cycle qui pilote le rythme des anneaux, les changements d'accord ne font que basculer le voicing.
            int cycBeats = PolyChord.CycleBeats(pc);
            int spq = PolyChord.SpqFor(pc);
            wheel.SetRings(rings, cycBeats * spq, cycBeats, cycBeats);

            RefreshLayerTitles();
            host.Render?.Invoke();
        }

        static Color FuchsiaShade(int i)
        {
            // Palette dérivée du fuchsia principal — évite d'avoir tous les anneaux identiques sans forcer
            // une couleur par voix qui n'aurait pas de sens musical ici.
            var pal = new[] {
                Color.FromRgb(0xD6, 0x33, 0x84), Color.FromRgb(0xB2, 0x1B, 0x67),
                Color.FromRgb(0xF7, 0x5C, 0xB0), Color.FromRgb(0x8D, 0x0E, 0x50),
                Color.FromRgb(0xE1, 0x44, 0x96), Color.FromRgb(0xA0, 0x27, 0x76),
            };
            return pal[((i % pal.Length) + pal.Length) % pal.Length];
        }

        // Met à jour le TextBlock « Note N » / « Anneau N » dans chaque carte de calque, en fonction du mode.
        // On accède au TextBlock nommé via l'ItemContainerGenerator pour ne pas ajouter une propriété dépendante
        // par calque juste pour ce titre.
        void RefreshLayerTitles()
        {
            for (int i = 0; i < layersList.Items.Count; i++)
            {
                var container = layersList.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (container == null) continue;
                var tb = FindByName<TextBlock>(container, "layerTitle");
                if (tb == null) continue;
                var lay = pc.Layers[i];
                string prefix = pc.Mode == PolyChordMode.OneRingPerTone ? Loc.T("Note") : Loc.T("Anneau");
                string suffix = pc.Mode == PolyChordMode.OneRingPerTone ? " (idx " + (lay?.ToneIndex ?? 0) + ")" : "";
                tb.Text = prefix + " " + (i + 1) + suffix;
            }
        }
        static T FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            if (root is T fe && fe.Name == name) return fe;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                var r = FindByName<T>(c, name);
                if (r != null) return r;
            }
            return null;
        }

        // ---- roue : clic-cellule (mode custom seulement) ------------------------------------------------
        void Wheel_CellClicked(int layerIndex, int step)
        {
            if (layerIndex < 0 || layerIndex >= pc.Layers.Count) return;
            var lay = pc.Layers[layerIndex];
            if (lay == null || !lay.CustomMode) return;
            host.PushUndo?.Invoke("polychord:custom");
            var set = new HashSet<int>();
            if (lay.CustomHits != null) foreach (var p in lay.CustomHits) if (p >= 0 && p < lay.Steps) set.Add(p);
            if (!set.Add(step)) set.Remove(step);
            var arr = new int[set.Count]; int k = 0; foreach (var p in set) arr[k++] = p; Array.Sort(arr);
            lay.CustomHits = arr;
        }

        static EuclidChordLayer LayerFrom(object sender) => (sender as FrameworkElement)?.DataContext as EuclidChordLayer;
        static PolyChordItem ChordFrom(object sender) => (sender as FrameworkElement)?.DataContext as PolyChordItem;

        // ---- module : options -------------------------------------------------------------------------------
        void cboMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cboMode.SelectedIndex < 0) return;
            var nm = (PolyChordMode)cboMode.SelectedIndex;
            if (pc.Mode == nm) return;
            host.PushUndo?.Invoke("polychord:mode");
            pc.Mode = nm;
            OnPC(nameof(MultiVisibility)); OnPC(nameof(SweepVisibility));
            RedrawAll();
        }
        void cboRestart_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cboRestart.SelectedIndex < 0) return;
            var nr = (ChordRestartMode)cboRestart.SelectedIndex;
            if (pc.Restart == nr) return;
            host.PushUndo?.Invoke("polychord:restart");
            pc.Restart = nr;
            RedrawAll();
        }
        void cboVoiceLead_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cboVoiceLead.SelectedIndex < 0) return;
            if (pc.VoiceLeadAnchor == cboVoiceLead.SelectedIndex) return;
            host.PushUndo?.Invoke("polychord:vla");
            pc.VoiceLeadAnchor = cboVoiceLead.SelectedIndex;
            RedrawAll();
        }
        void txtOctave_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtOctave.Text, out int v) && v != pc.Octave) { host.PushUndo?.Invoke("polychord:octave"); pc.Octave = v; RedrawAll(); }
        }
        void txtCycleBeats_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtCycleBeats.Text, out int v) && v > 0 && v != pc.CycleBeats)
            { host.PushUndo?.Invoke("polychord:cycle"); pc.CycleBeats = v; RedrawAll(); }
        }
        void chkOpenVoicing_Click(object sender, RoutedEventArgs e)
        {
            if (chkOpenVoicing.IsChecked == pc.OpenVoicing) return;
            host.PushUndo?.Invoke("polychord:open");
            pc.OpenVoicing = chkOpenVoicing.IsChecked == true;
            RedrawAll();
        }

        // ---- accords : dérivation degré → root/qualité -----------------------------------------------------
        // Quand l'utilisateur change le degré, on re-dérive (Root, Quality) via MusicTheory.DiatonicChord — SAUF
        // pour « Fixe » (Degree = -1) qui laisse l'utilisateur poser à la main via le combo Qualité.
        void chordDegree_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var c = ChordFrom(sender); if (c == null) return;
            if (c.Degree < 0) return;   // Fixe : rien à re-dériver
            var key = host.Project?.Key ?? new Engine.Score.KeySignature();
            var d = MusicTheory.DiatonicChord(key, c.Degree, c.DiatonicColour, c.Suspension, c.ModeOverride);
            if (c.Root != d.root) c.Root = d.root;
            if (c.Quality != d.quality) c.Quality = d.quality;
        }
        void chordColour_SelectionChanged(object sender, SelectionChangedEventArgs e) => chordDegree_SelectionChanged(sender, e);
        void chordSuspension_SelectionChanged(object sender, SelectionChangedEventArgs e) => chordDegree_SelectionChanged(sender, e);
        void chordQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var c = ChordFrom(sender); if (c == null) return;
            // L'utilisateur touche la qualité → on considère qu'il pose une qualité « à la main » et bascule en
            // accord Fixe si la nouvelle qualité ne colle pas avec le degré actuel (comportement calqué sur
            // AiChord/import : on ne re-drift pas la qualité au prochain changement de degré).
            if (c.Degree >= 0)
            {
                var key = host.Project?.Key ?? new Engine.Score.KeySignature();
                var d = MusicTheory.DiatonicChord(key, c.Degree, c.DiatonicColour, c.Suspension, c.ModeOverride);
                if (c.Quality != d.quality) c.Degree = -1;
            }
        }
        void chordBeats_LostFocus(object sender, RoutedEventArgs e) { /* Binding TwoWay a déjà écrit ; le PropertyChanged déclenche RedrawAll */ }
        void chordDelete_Click(object sender, RoutedEventArgs e)
        {
            var c = ChordFrom(sender); if (c == null) return;
            host.PushUndo?.Invoke("polychord:chord-del");
            pc.Chords.Remove(c);
        }
        void btnAddChord_Click(object sender, RoutedEventArgs e)
        {
            host.PushUndo?.Invoke("polychord:chord-add");
            // Nouveau : hérite des paramètres du dernier accord (durée + couleur/suspension) — mêmes défauts que
            // ChordModelOps.NewChordLike pour un accord classique.
            int beats = pc.Chords.Count > 0 ? pc.Chords[pc.Chords.Count - 1].Beats : Math.Max(1, host.Project != null ? Engine.Timeline.ChordModelOps.BarTemps(host.Project) : 4);
            var key = host.Project?.Key ?? new Engine.Score.KeySignature();
            var d = MusicTheory.DiatonicChord(key, 0);       // I par défaut
            pc.Chords.Add(new PolyChordItem { Degree = 0, Root = d.root, Quality = d.quality, Beats = beats });
        }

        // ---- anneaux ----------------------------------------------------------------------------------------
        void Layer_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var l = LayerFrom(sender); if (l == null) return;
            wheel.SetHighlight(pc.Layers.IndexOf(l));
        }
        void Layer_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => wheel.SetHighlight(-1);
        void btnCollapse_Click(object sender, RoutedEventArgs e) { var l = LayerFrom(sender); if (l != null) l.Collapsed = !l.Collapsed; }
        void chkCustom_Click(object sender, RoutedEventArgs e)
        {
            var l = LayerFrom(sender); if (l == null) return;
            host.PushUndo?.Invoke("polychord:custom");
            if (l.CustomMode && l.CustomHits == null)
                l.CustomHits = RhythmAnalysis.ToPositions(EuclideanRhythm.Rotate(EuclideanRhythm.Pattern(l.Hits, l.Steps), l.Rotation));
        }
        void btnDelete_Click(object sender, RoutedEventArgs e)
        {
            var l = LayerFrom(sender); if (l == null) return;
            host.PushUndo?.Invoke("polychord:layer-del");
            pc.Layers.Remove(l);
        }
        void btnAddLayer_Click(object sender, RoutedEventArgs e)
        {
            host.PushUndo?.Invoke("polychord:layer-add");
            int[] ks = { 3, 5, 7 }, ns = { 8, 8, 16 };
            int i = Math.Min(pc.Layers.Count, ks.Length - 1);
            int nextTone = pc.Layers.Count;  // en mode Multi, chaque nouvel anneau vise la note suivante par défaut
            pc.Layers.Add(new EuclidChordLayer { Hits = ks[i], Steps = ns[i], ToneIndex = nextTone });
        }

        // ---- « Figer » --------------------------------------------------------------------------------------
        void btnFreeze_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Loc.T("FigerPolyChordPerdParametres"), Loc.T("FigerEnAccordsIndividuels"),
                                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            host.PushUndo?.Invoke("polychord:freeze");
            var notesPerChord = PolyChord.ToNotesPerChord(pc);
            int spq = PolyChord.SpqFor(pc);
            // Remplace l'item du module par le premier accord et insère les autres à la suite. Chaque accord
            // est un PatternGeneratorModule (Personnalisé + notes en index de voix), ce qui donne à l'utilisateur
            // les mêmes réglages qu'un accord classique gelé — édition, style etc. redeviennent disponibles.
            var items = track.Items;
            int idx = items.IndexOf(item);
            if (idx < 0) return;
            items.RemoveAt(idx);
            for (int i = 0; i < pc.Chords.Count; i++)
            {
                var c = pc.Chords[i];
                var pg = new PatternGeneratorModule
                {
                    Root = c.Root, Quality = c.Quality, Degree = c.Degree,
                    DiatonicColour = c.DiatonicColour, Suspension = c.Suspension, ModeOverride = c.ModeOverride,
                    Octave = pc.Octave + c.OctaveShift, Inversion = c.Inversion, OpenVoicing = pc.OpenVoicing,
                    BeatsPerBar = Math.Max(1, c.Beats), Repeats = 1,
                    Style = PatternGenerator.CustomStyle,
                };
                pg.SetCustomNotes(notesPerChord[i], spq, Math.Max(1, c.Beats) * spq);
                items.Insert(idx + i, new TimelineItem { Module = pg });
            }
            host.SelectItem?.Invoke(track, items[idx]);
            host.Render?.Invoke();
        }

        // ---- lecture ----------------------------------------------------------------------------------------
        void btnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (wave != null) { Stop(); wheel.SetPlayhead(-1); return; }
            var w = Window.GetWindow(this);
            host.EnsureSoundFont?.Invoke(w, "Playback");
            try
            {
                var preset = InstrumentCatalog.GetPreset(track.Instrument);
                var ctx = new FlowContext { GmProgram = preset?.PatchNumber ?? 0, Drum = preset?.BankNumber == InstrumentCatalog.DrumIndex, Bpm = host.Project.MainBpm };
                provider = new LoopingRiffProvider(() => PolyChord.Generate(pc), ctx);
                wave = new NAudio.Wave.WaveOutEvent { DesiredLatency = 120 };
                wave.Init(provider); wave.Play();
                btnPlay.Content = "■ " + Loc.T("Stop");
                timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                int spq = PolyChord.SpqFor(pc);
                timer.Tick += (s2, e2) =>
                {
                    if (provider == null) return;
                    // La roue montre UN cycle du module ; l'aiguille tourne modulo CycleBeats — même contrat que PolyDrum.
                    int cyc = PolyChord.CycleBeats(pc);
                    double beats = (provider.CurrentSlice / (double)spq) % cyc;
                    wheel.SetPlayhead(beats);
                };
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
