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
    /// <summary>Accord stocké (Degree + Root + Quality) → index du combo de degrés, DOMINANTES SECONDAIRES incluses.
    ///
    /// Pourquoi un MultiBinding EN LECTURE SEULE et pas un binding TwoWay comme avant : l'aller-retour degré↔V/x n'est
    /// pas une propriété du modèle mais une CONVERSION qui a besoin (a) de la tonalité du projet, que seul l'éditeur
    /// connaît, et (b) des trois propriétés Degree/Root/Quality — une V/x est un accord chromatique stocké en « fixe ».
    /// L'écriture n'est donc pas symétrique (un seul index → trois propriétés) et ne peut pas passer par ConvertBack :
    /// elle est faite en code-behind (<c>chordDegree_SelectionChanged</c>), en UNE transaction. La lecture, elle, reste
    /// déclarative — donc virtualisation-compatible : rien ne force la réalisation des conteneurs, chaque carte
    /// n'observe que son propre accord.</summary>
    public class PolyDegreeIndexConverter : IMultiValueConverter
    {
        /// <summary>Le vocabulaire de la tonalité courante, injecté par l'éditeur (et remplacé si la tonalité change).</summary>
        public ChordDegreeChoices Choices;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var ch = Choices;
            if (ch == null || values == null || values.Length < 3) return 0;
            if (!(values[0] is int degree) || !(values[1] is int root) || !(values[2] is int quality)) return 0;
            return ch.IndexOf(degree, root, quality);
        }
        // OneWay : jamais appelé. On renvoie DoNothing plutôt que de lever, pour ne pas transformer une régression de
        // binding en plantage.
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            var r = new object[targetTypes?.Length ?? 0];
            for (int i = 0; i < r.Length; i++) r[i] = Binding.DoNothing;
            return r;
        }
    }

    /// <summary>Degré stocké → « l'accord est-il FIXE ? ». Sert à n'activer le combo de fondamentale que sur un accord
    /// manuel : sur un accord verrouillé à un degré, la fondamentale est dérivée de la tonalité et la saisir n'aurait
    /// aucun effet durable (la prochaine dérivation l'écraserait) — même règle que <c>RootEnabled</c> dans l'éditeur
    /// d'accord ordinaire.</summary>
    public class DegreeIsFixedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int i && i < 0;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
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
            NormalizeFixedChords();

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
            chkMonodicPick.IsChecked = pc.MonodicPick;
            txtMonodicSeed.Text = pc.MonodicSeed.ToString();

            // Combos accords : MÊME vocabulaire que l'éditeur d'accord ordinaire, dominantes secondaires comprises
            // (ChordDegreeChoices est partagé par les deux). La liste dépend de la tonalité, donc EnsureChoices.
            EnsureChoices();
            RootNames = PatternGenerator.RootNames;
            QualityNames = PatternGenerator.QualityNames;
            ColourNames = MusicTheory.DiatonicColourNames;
            SuspensionNames = MusicTheory.SuspensionNames;
            ModeNames = MusicTheory.ModeOverrideNames;

            // Noms de contour (mode 1-anneau). Alignés sur ceux de MelodicLineEngine.ContourNames pour l'utilisateur.
            ContourNames = new List<string> { Loc.T("ContourVague"), Loc.T("ContourMontante"), Loc.T("ContourDescendante"),
                                              Loc.T("ContourStatique"), Loc.T("ContourZigzag"), Loc.T("ContourAleatoire") };
        }

        // ---- ItemsSource / bindings sur self ---------------------------------------------------------------
        public IReadOnlyList<string> DegreeNames { get; private set; }
        public IReadOnlyList<string> RootNames { get; private set; }
        public IReadOnlyList<string> QualityNames { get; private set; }
        public IReadOnlyList<string> ColourNames { get; private set; }
        public IReadOnlyList<string> SuspensionNames { get; private set; }
        public IReadOnlyList<string> ModeNames { get; private set; }
        public IReadOnlyList<string> ContourNames { get; private set; }

        // ---- vocabulaire harmonique (dépend de la TONALITÉ) -------------------------------------------------
        Engine.Score.KeySignature Key => host?.Project?.Key ?? new Engine.Score.KeySignature();
        ChordDegreeChoices choices;

        /// <summary>(Re)construit la liste de degrés si elle ne correspond plus à la tonalité du projet. Appelée à
        /// l'ouverture ET à chaque redessin : si la tonalité change pendant que l'éditeur est ouvert, la liste des V/x
        /// (qui dépend des degrés tonicisables) et la casse des romains suivent.</summary>
        void EnsureChoices()
        {
            var key = Key;
            if (choices != null && choices.Matches(key)) return;
            choices = ChordDegreeChoices.For(key);
            DegreeNames = choices.Names;
            // Le convertisseur du DataTemplate est l'INSTANCE déclarée dans les ressources : on lui injecte la
            // tonalité courante (impossible en pur XAML — un IValueConverter n'a pas accès au projet).
            if (Resources["PolyDegreeIndex"] is PolyDegreeIndexConverter conv) conv.Choices = choices;
            OnPC(nameof(DegreeNames));
        }

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
        // Un champ d'accord ou d'anneau a changé. Les combos couleur/suspension/mode/qualité d'un accord sont bindés
        // TwoWay : on détecte leur édition ICI, sur le MODÈLE, et pas via SelectionChanged sur les combos. C'est
        // volontaire et nécessaire : la liste d'accords est virtualisée EN RECYCLAGE, donc réaliser ou recycler une
        // carte réaffecte le SelectedIndex de ses combos et lève SelectionChanged sans qu'aucun utilisateur n'ait
        // cliqué — un simple défilement re-dérivait alors la qualité des accords dont la qualité ne suit pas leur
        // degré. Un setter de PolyChordItem, lui, ne lève PropertyChanged que sur un VRAI changement de valeur.
        void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (applying) return;   // écritures de la transaction en cours : un seul redessin, à la fin
            if (sender is PolyChordItem c && DeriveOnChange(c, e.PropertyName)) return;
            RedrawAll();
        }

        // Retourne true si le changement a été traité (transaction + redessin déjà faits).
        bool DeriveOnChange(PolyChordItem c, string prop)
        {
            switch (prop)
            {
                case nameof(PolyChordItem.DiatonicColour):
                case nameof(PolyChordItem.Suspension):
                case nameof(PolyChordItem.ModeOverride):
                    ApplyToChord(() => Derive(c));
                    return true;
                case nameof(PolyChordItem.Quality):
                    // Qualité posée à la main : l'accord devient FIXE si elle ne colle plus avec son degré (même
                    // comportement que l'import/AiChord — on ne re-dérive plus la qualité au prochain changement de
                    // degré), et le trio couleur/suspension/mode se réaligne sur la qualité réelle.
                    bool becomesFixed = c.Degree >= 0
                        && c.Quality != MusicTheory.DiatonicChord(Key, c.Degree, c.DiatonicColour, c.Suspension, c.ModeOverride).quality;
                    if (c.Degree >= 0 && !becomesFixed) return false;   // qualité conforme au degré : rien à réaligner
                    ApplyToChord(() => { if (becomesFixed) c.Degree = -1; SyncColourTrio(c); });
                    return true;
                default:
                    return false;
            }
        }

        // Garde de TRANSACTION : poser une dominante secondaire écrit Degree PUIS Root PUIS Quality PUIS le trio
        // couleur/suspension/mode, soit 5 à 7 PropertyChanged selon l'accord de départ (mesuré). Sans regroupement,
        // chacun déclenchait un RedrawAll complet — donc autant de passes de Revoice et de rendus de la timeline pour
        // un seul clic. On neutralise les notifications le temps de l'écriture et on redessine UNE fois.
        bool applying;
        void ApplyToChord(Action mutate)
        {
            applying = true;
            try { mutate(); }
            finally { applying = false; }
            RedrawAll();
        }

        /// <summary>Re-dérive (Root, Quality) d'un accord d'après son degré et son trio couleur/suspension/mode —
        /// exactement comme <c>ChordEditorViewModel.ApplyDiatonic</c> pour un accord ordinaire, cas FIXE compris : la
        /// fondamentale d'un accord fixe ne bouge pas, seule sa qualité suit les combos (lue sur le degré I d'un Do
        /// majeur de référence).</summary>
        void Derive(PolyChordItem c)
        {
            if (c.Degree < 0) { c.Quality = ChordDegrees.QualityForColour(c.DiatonicColour, c.Suspension, c.ModeOverride); return; }
            var d = MusicTheory.DiatonicChord(Key, c.Degree, c.DiatonicColour, c.Suspension, c.ModeOverride);
            c.Root = d.root; c.Quality = d.quality;
        }

        /// <summary>L'inverse : relit le trio couleur/suspension/mode depuis la qualité réelle d'un accord fixe, pour
        /// que les trois combos décrivent l'accord. Ignoré si le système de couleurs ne sait pas exprimer cette qualité
        /// (tensions exotiques) : mieux vaut un trio inchangé qu'un trio qui mentirait et écraserait la qualité au
        /// prochain réglage.</summary>
        static void SyncColourTrio(PolyChordItem c)
        {
            var t = ChordDegrees.ColourForQuality(c.Quality);
            if (ChordDegrees.QualityForColour(t.colour, t.suspension, t.mode) != c.Quality) return;
            c.DiatonicColour = t.colour; c.Suspension = t.suspension; c.ModeOverride = t.mode;
        }

        // À l'ouverture : aligner le trio des accords FIXES sur leur qualité réelle (même normalisation qu'à
        // l'ouverture de l'éditeur d'accord ordinaire), sinon les combos affichent « Triade » pour un accord de
        // septième et le premier réglage de couleur détruirait la qualité. Fait AVANT SubscribeAll : aucun redessin.
        void NormalizeFixedChords()
        {
            if (pc?.Chords == null) return;
            foreach (var c in pc.Chords) if (c != null && c.Degree < 0) SyncColourTrio(c);
        }

        void SyncModuleControls()
        {
            // Un cboMode.SelectedIndex change → propage à pc.Mode. Ici c'est l'inverse, à l'ouverture.
            if (cboMode.SelectedIndex != (int)pc.Mode) cboMode.SelectedIndex = (int)pc.Mode;
            if (cboRestart.SelectedIndex != (int)pc.Restart) cboRestart.SelectedIndex = (int)pc.Restart;
            if (cboVoiceLead.SelectedIndex != pc.VoiceLeadAnchor) cboVoiceLead.SelectedIndex = pc.VoiceLeadAnchor;
        }

        // Garde de RÉ-ENTRANCE, et elle est indispensable : RedrawAll appelle Revoice, qui ÉCRIT Inversion et
        // OctaveShift sur chaque PolyChordItem ; chaque écriture lève PropertyChanged, que cet éditeur écoute
        // (Item_PropertyChanged) et qui rappelle RedrawAll. Le redessin se déclenchait donc lui-même par les
        // mutations qu'il provoque, en relançant à chaque tour le rendu complet de la timeline (host.Render).
        // Sur un module à peu d'accords ça convergeait ; sur les 64 accords d'un morceau généré par l'IA, non :
        // l'application se figeait à l'ouverture de l'éditeur (mesuré — jamais ouvert au bout de 3 minutes).
        // Un seul passage suffit : Revoice est appelée AVANT la lecture des valeurs par la roue et le rendu.
        bool redrawing;

        void RedrawAll()
        {
            if (redrawing) return;
            redrawing = true;
            try { RedrawCore(); }
            finally { redrawing = false; }
        }

        void RedrawCore()
        {
            // La tonalité du projet peut changer pendant que l'éditeur est ouvert (transposition, changement d'armure) :
            // la liste de degrés en dépend (casse des romains + degrés tonicisables), on la resynchronise ici.
            EnsureChoices();
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
        void chkMonodicPick_Click(object sender, RoutedEventArgs e)
        {
            if (chkMonodicPick.IsChecked == pc.MonodicPick) return;
            host.PushUndo?.Invoke("polychord:monodic");
            pc.MonodicPick = chkMonodicPick.IsChecked == true;
            RedrawAll();
        }
        void txtMonodicSeed_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtMonodicSeed.Text, out int v) && v != pc.MonodicSeed)
            { host.PushUndo?.Invoke("polychord:seed"); pc.MonodicSeed = v; RedrawAll(); }
        }

        // ---- accords : choix du degré (ou d'une DOMINANTE SECONDAIRE) ---------------------------------------
        // Le combo de degrés est bindé en LECTURE SEULE (MultiBinding, voir PolyDegreeIndexConverter) : l'écriture se
        // fait ici, en une transaction, parce qu'une V/x écrit trois propriétés d'un coup et a besoin de la tonalité.
        void chordDegree_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (applying) return;
            var cb = sender as ComboBox; var c = ChordFrom(sender);
            if (cb == null || c == null || cb.SelectedIndex < 0) return;
            EnsureChoices();
            int idx = cb.SelectedIndex;
            // Le SelectedIndex bougeant aussi quand la liste réalise/recycle un conteneur ou quand le modèle change,
            // seule une différence avec l'état réel de l'accord signale un choix de l'utilisateur.
            if (idx == choices.IndexOf(c.Degree, c.Root, c.Quality)) return;
            host.PushUndo?.Invoke("polychord:chord-degree");
            ApplyToChord(() =>
            {
                if (choices.TrySecondary(idx, out int secRoot, out int secQuality))
                {
                    // Dominante secondaire : accord CHROMATIQUE, donc stocké en fixe (Degree = −1), fondamentale à la
                    // quinte au-dessus du degré tonicisé et qualité de dominante 7 (cf. ChordDegreeChoices).
                    c.Degree = -1;
                    c.Root = secRoot;
                    if (secQuality >= 0) c.Quality = secQuality;
                    SyncColourTrio(c);
                    return;
                }
                c.Degree = idx <= 0 ? -1 : idx - 1;
                Derive(c);
            });
            // Le modèle ne change pas forcément : choisir « Manuel » sur un accord qui RESTE une dominante secondaire
            // (un accord de 7e de dominante à la quinte d'un degré tonicisable SE LIT comme tel) n'écrit rien, donc
            // aucun PropertyChanged ne rafraîchit le MultiBinding. On force la relecture pour que le combo n'affiche
            // jamais autre chose que ce que l'accord vaut réellement.
            BindingOperations.GetMultiBindingExpression(cb, ComboBox.SelectedIndexProperty)?.UpdateTarget();
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
