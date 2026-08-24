using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MusicTracker.Controls;
using MusicTracker.Dialogs;
using MusicTracker.Engine;
using MusicTracker.Engine.Timeline.Effects;
using MusicTracker.Localization;

namespace MusicTracker.Live
{
    /// <summary>
    /// Fenêtre de Koton Live : le rack temps réel. Deux modes, un seul moteur (<see cref="LiveEngine"/>) :
    ///
    /// * **Effets** — le micro (ou l'entrée ligne) traverse la chaîne d'inserts et ressort sur la carte son.
    /// * **Instrument** — le micro (détection de hauteur monophonique) ou un clavier MIDI joue un instrument
    ///   SoundFont / VSTi / Koton, qui traverse ensuite la même chaîne d'inserts.
    ///
    /// La fenêtre est hébergeable par DEUX applications : Koton Studio (menu ▸ Live) et l'exécutable
    /// autonome KotonLive.exe. Elle ne suppose donc rien de son hôte au-delà des ressources partagées de
    /// <c>Theme/AppResources.xaml</c>, et persiste ses réglages dans son propre <see cref="LiveSettings"/>.
    /// </summary>
    public partial class LiveWindow : Window
    {
        static readonly string[] RootNames = { "Do", "Do♯", "Ré", "Mi♭", "Mi", "Fa", "Fa♯", "Sol", "Sol♯", "La", "Si♭", "Si" };
        static readonly int[] LatencyChoices = { 5, 10, 15, 20, 30, 50, 80, 120 };
        /// <summary>Amplitude du sélecteur d'octave : ±3 couvre tout ce qu'un chanteur ou un petit clavier
        /// maîtrise, sans sortir de la plage MIDI utile.</summary>
        const int OctaveRange = 3;
        /// <summary>Facteur d'échelle du vumètre de détection : les niveaux utiles vivent sous 0,1 en RMS, une
        /// barre linéaire 0..1 serait illisible.</summary>
        const double MicMeterScale = 12;
        /// <summary>Longueurs proposées pour le filtre médian de hauteur (en fenêtres d'analyse).</summary>
        static readonly int[] SmoothingChoices = { 1, 3, 5, 7, 9 };
        /// <summary>Tailles de fenêtre d'analyse proposées. 2048 à 44,1 kHz = 46 ms de signal.</summary>
        static readonly int[] FrameSizeChoices = { 1024, 2048, 4096 };
        /// <summary>Plage du sélecteur « note la plus grave » : de Mi1 (41 Hz, sous la 4e corde d'une basse)
        /// à Do5 (523 Hz). Au-delà il n'y a plus d'instrument à couvrir, et en dessous la recherche redevient
        /// aussi large que le défaut.</summary>
        const int LowestNoteMin = 28, LowestNoteMax = 72;

        readonly LiveEngine _engine = new LiveEngine();
        readonly LiveSettings _cfg = LiveSettings.Instance;
        readonly DispatcherTimer _timer;

        LiveMidiInput _midi;
        WaveNoteSourceProvider _mic;
        InsertChainPanel _insertPanel;

        List<LiveDeviceInfo> _inputs = new List<LiveDeviceInfo>();
        List<LiveDeviceInfo> _outputs = new List<LiveDeviceInfo>();
        List<LiveDeviceInfo> _midiDevices = new List<LiveDeviceInfo>();
        List<LiveDeviceInfo> _micDevices = new List<LiveDeviceInfo>();
        List<string> _asioDrivers = new List<string>();

        // Plugin choisi pour le mode instrument (chemin VST ou Id Koton selon le type).
        string _pluginRef = "";
        bool _loading = true;

        // Chargement du SoundFont : c'est AppSettings.Apply() qui le résout (il vit dans %LocalAppData%,
        // pas à côté de l'exe) et le charge dans InstrumentCatalog. Le séquenceur le fait depuis sa fenêtre
        // principale ; Koton Live doit donc le faire lui-même, sinon l'instrument SoundFont est MUET. En
        // tâche de fond parce que le fichier pèse quelques centaines de Mo.
        readonly System.Threading.Tasks.Task _soundFontReady;

        int _lastNote = -1;                 // dernière note reconnue au micro (numéro MIDI), pour l'affichage
        DateTime _lastNoteAt = DateTime.MinValue;

        // Paramètres de détection RECOPIÉS depuis l'UI. Les fonctions passées au détecteur de hauteur sont
        // invoquées depuis le THREAD DE CAPTURE audio ; or lire une propriété d'un contrôle WPF depuis un
        // autre thread que celui qui le possède JETTE. Une lambda du genre `() => sldOnset.Value` tue donc
        // l'analyse dès la première fenêtre — silencieusement, puisque l'exception meurt dans le callback du
        // pilote. D'où ces copies en champs simples, rafraîchies sur le thread UI. (L'éditeur de riff, lui,
        // lit AppSettings, un objet ordinaire : c'est pour ça qu'il n'a jamais eu le problème.)
        int _scaleMaskCache = AudioPitch.Chromatic;
        double _onsetCache = 0.5;
        int _velocityCache = 100;
        int _octaveCache;

        // Note SOURCE -> note réellement jouée. La transposition est figée au note-on : sans ça, changer
        // d'octave pendant qu'une note sonne enverrait le note-off sur une autre hauteur et la laisserait
        // coincée. Verrouillée : alimentée depuis le thread MIDI ou celui de la détection.
        readonly Dictionary<int, int> _sounding = new Dictionary<int, int>();

        void CacheDetectionParams()
        {
            if (cboScale == null || cboRoot == null || sldOnset == null || sldVelocity == null || cboOctave == null || cboLowestNote == null) return;
            _scaleMaskCache = CurrentScaleMask();
            _onsetCache = sldOnset.Value;
            _velocityCache = (int)Math.Round(sldVelocity.Value);
            _octaveCache = cboOctave.SelectedIndex - OctaveRange;   // index 0 = -OctaveRange
            if (_mic != null) _mic.MinFrequency = CurrentMinFrequency;
        }

        /// <summary>Joue une note reçue d'une source, transposée de l'octave choisie.</summary>
        void PlaySourceNoteOn(int sourceMidi, int velocity)
        {
            int played = Math.Max(0, Math.Min(127, sourceMidi + 12 * _octaveCache));
            int previous = -1;
            lock (_sounding)
            {
                if (_sounding.TryGetValue(sourceMidi, out previous)) _sounding.Remove(sourceMidi);
                _sounding[sourceMidi] = played;
            }
            var inst = _engine.Instrument;
            if (previous >= 0) inst?.NoteOff(previous);   // re-déclenchement de la même note source
            inst?.NoteOn(played, velocity);
        }

        void PlaySourceNoteOff(int sourceMidi)
        {
            int played;
            lock (_sounding)
            {
                if (!_sounding.TryGetValue(sourceMidi, out played))
                    played = Math.Max(0, Math.Min(127, sourceMidi + 12 * _octaveCache));
                else _sounding.Remove(sourceMidi);
            }
            _engine.Instrument?.NoteOff(played);
        }

        /// <summary>Un réglage de détection a bougé : on rafraîchit la copie lue par le thread de capture.</summary>
        void Detection_Changed(object sender, SelectionChangedEventArgs e) => CacheDetectionParams();
        void Detection_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => CacheDetectionParams();

        /// <summary>Le seuil de voisement s'applique AU VOL au détecteur : on l'entend bouger sans redémarrer,
        /// et le repère du vumètre suit — c'est ce qui rend le réglage faisable à l'oreille.</summary>
        void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblThreshold != null) lblThreshold.Text = sldThreshold.Value.ToString("0.000");
            if (_mic != null) _mic.SilenceThreshold = sldThreshold.Value;
        }

        /// <summary>Durée minimale d'une note : appliquée au vol elle aussi, pour se régler à l'oreille.</summary>
        void MinDuration_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblMinDuration != null) lblMinDuration.Text = Math.Round(sldMinDuration.Value * 1000) + " ms";
            if (_mic != null) _mic.MinNoteSeconds = sldMinDuration.Value;
        }

        void MaxLeap_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblMaxLeap != null) lblMaxLeap.Text = ((int)Math.Round(sldMaxLeap.Value)).ToString();
            if (_mic != null) _mic.MaxLeapSemitones = (int)Math.Round(sldMaxLeap.Value);
        }

        void Snap_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblSnap != null) lblSnap.Text = ((int)Math.Round(sldSnap.Value)) + " ¢";
            if (_mic != null) _mic.SnapHysteresisCents = sldSnap.Value;
        }

        void Smoothing_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_mic != null) _mic.MedianFrames = CurrentSmoothing;
        }

        void OctaveBias_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mic != null) _mic.OctaveBias = sldOctaveBias.Value;
        }

        /// <summary>La taille de fenêtre n'est lue qu'à l'ouverture de la capture : on relance donc la
        /// détection pour que le changement s'entende tout de suite.</summary>
        void FrameSize_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _mic == null) return;
            _mic.FrameSize = CurrentFrameSize;
            _mic.Start();   // Reopen : réalloue la fenêtre d'analyse et redémarre la capture
        }

        int CurrentSmoothing => SmoothingChoices[Math.Max(0, Math.Min(SmoothingChoices.Length - 1, cboSmoothing.SelectedIndex))];
        int CurrentFrameSize => FrameSizeChoices[Math.Max(0, Math.Min(FrameSizeChoices.Length - 1, cboFrameSize.SelectedIndex))];

        public LiveWindow()
        {
            InitializeComponent();

            PopulateStaticCombos();
            RefreshDevices();
            ApplySettings();

            _soundFontReady = System.Threading.Tasks.Task.Run(() =>
            {
                try { AppSettings.Instance.Apply(); } catch { /* pas de SoundFont : signalé à la création de l'instrument */ }
            });

            // La chaîne d'inserts éditée est CELLE du moteur : le panneau la modifie en place, on n'a plus
            // qu'à reconstruire les instances audio après chaque changement.
            _insertPanel = new InsertChainPanel(_engine.Inserts, this, _engine.SampleRate, (System.Windows.Style)FindResource("TinyButton"));
            _insertPanel.Changed += () => { if (_engine.IsRunning) _engine.RebuildEffects(); PersistInserts(); };
            insertsHost.Content = _insertPanel;

            _engine.Failed += OnEngineFailed;

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
            _timer.Tick += (s, e) => UpdateMeters();
            _timer.Start();

            _loading = false;
            CacheDetectionParams();
            UpdateUiState();
        }

        // ---- initialisation de l'UI ----------------------------------------------------------------------

        void PopulateStaticCombos()
        {
            cboBackend.Items.Add("WASAPI");
            cboBackend.Items.Add("ASIO");

            foreach (int ms in LatencyChoices) cboLatency.Items.Add(ms + " ms");

            cboScale.Items.Add(Loc.T("LiveScaleChromatic"));
            foreach (var n in AudioPitch.ScaleNames) cboScale.Items.Add(n);
            foreach (var n in RootNames) cboRoot.Items.Add(n);

            for (int i = 0; i < InstrumentCatalog.Count; i++) cboProgram.Items.Add(InstrumentCatalog.Name(i));

            for (int o = -OctaveRange; o <= OctaveRange; o++) cboOctave.Items.Add(o > 0 ? "+" + o : o.ToString());
            foreach (int k in SmoothingChoices) cboSmoothing.Items.Add(k == 1 ? "1 (" + Loc.T("LiveSmoothingNone") + ")" : k.ToString());
            foreach (int n in FrameSizeChoices) cboFrameSize.Items.Add(n.ToString());
            for (int m = LowestNoteMin; m <= LowestNoteMax; m++)
                cboLowestNote.Items.Add(NoteName(m) + "  (" + MidiToHz(m).ToString("0") + " Hz)");

            cboInstrumentKind.Items.Add(Loc.T("LiveInstrumentSoundFont"));
            cboInstrumentKind.Items.Add(Loc.T("LiveInstrumentVst"));
            cboInstrumentKind.Items.Add(Loc.T("LiveInstrumentKoton"));
        }

        /// <summary>(Re)lit la liste des périphériques. Appelée au démarrage — un branchement à chaud est pris
        /// en compte à la prochaine ouverture de la fenêtre, comme partout ailleurs dans l'app.</summary>
        void RefreshDevices()
        {
            _inputs = LiveDevices.Inputs();
            _outputs = LiveDevices.Outputs();
            _midiDevices = LiveDevices.MidiInputs();
            _micDevices = LiveDevices.WaveInDevices();
            _asioDrivers = LiveDevices.AsioDrivers();

            Fill(cboInput, _inputs);
            Fill(cboMidiDevice, _midiDevices);
            Fill(cboMicDevice, _micDevices);
            // cboOutput est rempli par UpdateBackendUi (endpoints WASAPI ou pilotes ASIO selon le moteur).
        }

        static void Fill(ComboBox combo, List<LiveDeviceInfo> devices)
        {
            combo.Items.Clear();
            if (devices.Count == 0) { combo.Items.Add(Loc.T("LiveNoDevice")); combo.IsEnabled = false; return; }
            combo.IsEnabled = true;
            foreach (var d in devices) combo.Items.Add(d.Name);
        }

        void ApplySettings()
        {
            cboBackend.SelectedIndex = _cfg.Backend == LiveBackend.Asio ? 1 : 0;
            UpdateBackendUi();

            SelectById(cboInput, _inputs, _cfg.InputDeviceId);
            if (_cfg.Backend == LiveBackend.Asio) SelectByText(cboOutput, _cfg.AsioDriver);
            else SelectById(cboOutput, _outputs, _cfg.OutputDeviceId);

            int li = Array.IndexOf(LatencyChoices, _cfg.LatencyMs);
            cboLatency.SelectedIndex = li >= 0 ? li : Array.IndexOf(LatencyChoices, 25) >= 0 ? Array.IndexOf(LatencyChoices, 25) : 4;
            cboAsioChannel.SelectedIndex = 0;

            rbInsert.IsChecked = _cfg.Mode == LiveMode.Insert;
            rbInstrument.IsChecked = _cfg.Mode == LiveMode.Instrument;
            rbMidi.IsChecked = _cfg.NoteSource == LiveNoteSource.Midi;
            rbMic.IsChecked = _cfg.NoteSource == LiveNoteSource.Microphone;

            SelectByText(cboMidiDevice, _cfg.MidiDevice);
            SelectByText(cboMicDevice, _cfg.MicDevice);
            cboScale.SelectedIndex = _cfg.ScaleMode < 0 ? 0 : Math.Min(_cfg.ScaleMode + 1, cboScale.Items.Count - 1);
            cboRoot.SelectedIndex = Math.Max(0, Math.Min(11, _cfg.ScaleRoot));
            sldOnset.Value = _cfg.OnsetSensitivity;
            sldVelocity.Value = _cfg.MicVelocity;
            cboOctave.SelectedIndex = Math.Max(0, Math.Min(2 * OctaveRange, _cfg.OctaveShift + OctaveRange));
            sldThreshold.Value = Math.Max(sldThreshold.Minimum, Math.Min(sldThreshold.Maximum, _cfg.SilenceThreshold));
            lblThreshold.Text = sldThreshold.Value.ToString("0.000");
            sldMinDuration.Value = Math.Max(sldMinDuration.Minimum, Math.Min(sldMinDuration.Maximum, _cfg.MinNoteSeconds));
            lblMinDuration.Text = Math.Round(sldMinDuration.Value * 1000) + " ms";
            sldMaxLeap.Value = Math.Max(sldMaxLeap.Minimum, Math.Min(sldMaxLeap.Maximum, _cfg.MaxLeapSemitones));
            lblMaxLeap.Text = ((int)Math.Round(sldMaxLeap.Value)).ToString();
            cboSmoothing.SelectedIndex = Math.Max(0, Array.IndexOf(SmoothingChoices, _cfg.MedianFrames));
            cboFrameSize.SelectedIndex = Math.Max(0, Array.IndexOf(FrameSizeChoices, _cfg.AnalysisFrameSize));
            sldSnap.Value = Math.Max(sldSnap.Minimum, Math.Min(sldSnap.Maximum, _cfg.SnapHysteresisCents));
            lblSnap.Text = ((int)Math.Round(sldSnap.Value)) + " ¢";
            cboLowestNote.SelectedIndex = Math.Max(0, Math.Min(LowestNoteMax - LowestNoteMin, _cfg.LowestNoteMidi - LowestNoteMin));
            sldOctaveBias.Value = Math.Max(0, Math.Min(1, _cfg.OctaveBias));
            chkMonitor.IsChecked = _cfg.MonitorInput;

            cboInstrumentKind.SelectedIndex = (int)_cfg.InstrumentKind;
            cboProgram.SelectedIndex = Math.Max(0, Math.Min(InstrumentCatalog.Count - 1, _cfg.Program));
            _pluginRef = _cfg.InstrumentRef ?? "";

            sldInGain.Value = _cfg.InputGain;
            sldOutGain.Value = _cfg.OutputGain;

            _engine.Inserts.Clear();
            if (_cfg.Inserts != null) _engine.Inserts.AddRange(_cfg.Inserts);
        }

        static void SelectById(ComboBox combo, List<LiveDeviceInfo> devices, string id)
        {
            if (devices.Count == 0) return;
            for (int i = 0; i < devices.Count; i++)
                if (string.Equals(devices[i].Id, id, StringComparison.Ordinal)) { combo.SelectedIndex = i; return; }
            combo.SelectedIndex = 0;   // périphérique disparu : on retombe sur le premier (= défaut Windows)
        }

        static void SelectByText(ComboBox combo, string text)
        {
            if (combo.Items.Count == 0) return;
            if (!string.IsNullOrEmpty(text))
            {
                for (int i = 0; i < combo.Items.Count; i++)
                    if (string.Equals(combo.Items[i] as string, text, StringComparison.Ordinal)) { combo.SelectedIndex = i; return; }
            }
            combo.SelectedIndex = 0;
        }

        // ---- état de l'UI ---------------------------------------------------------------------------------

        bool IsAsio => cboBackend.SelectedIndex == 1;
        bool IsInstrumentMode => rbInstrument.IsChecked == true;
        bool UsesMic => rbMic.IsChecked == true;

        /// <summary>Le moteur ASIO ne sépare pas entrée et sortie : un seul pilote fournit les deux, et sa
        /// taille de buffer se règle dans SON panneau. L'UI se replie donc sur « pilote + canal d'entrée ».</summary>
        void UpdateBackendUi()
        {
            bool asio = IsAsio;
            lblOutput.Text = asio ? Loc.T("LiveAsioDriver") : Loc.T("LiveOutput");
            UpdateInputRow();
            pnlAsioChannel.Visibility = asio ? Visibility.Visible : Visibility.Collapsed;
            btnAsioPanel.Visibility = asio ? Visibility.Visible : Visibility.Collapsed;
            lblLatency.Visibility = cboLatency.Visibility = asio ? Visibility.Collapsed : Visibility.Visible;

            string previous = cboOutput.SelectedItem as string;
            cboOutput.Items.Clear();
            if (asio)
            {
                if (_asioDrivers.Count == 0) { cboOutput.Items.Add(Loc.T("LiveNoAsio")); cboOutput.IsEnabled = false; }
                else { cboOutput.IsEnabled = true; foreach (var d in _asioDrivers) cboOutput.Items.Add(d); }
                if (cboAsioChannel.Items.Count == 0)
                    for (int i = 0; i < 16; i += 2) cboAsioChannel.Items.Add((i + 1) + "/" + (i + 2));
                SelectByText(cboOutput, _cfg.AsioDriver);
            }
            else
            {
                if (_outputs.Count == 0) { cboOutput.Items.Add(Loc.T("LiveNoDevice")); cboOutput.IsEnabled = false; }
                else { cboOutput.IsEnabled = true; foreach (var d in _outputs) cboOutput.Items.Add(d.Name); }
                SelectById(cboOutput, _outputs, _cfg.OutputDeviceId);
            }
            if (previous != null && cboOutput.SelectedIndex < 0) SelectByText(cboOutput, previous);
        }

        /// <summary>La ligne « Entrée » ne concerne que le signal que le MOTEUR capture. En mode instrument
        /// sans écoute de l'entrée, il ne capture rien du tout : afficher un sélecteur de micro là est le
        /// meilleur moyen de faire croire que c'est lui qui alimente la détection de hauteur (laquelle a son
        /// propre sélecteur, dans le panneau Instrument). On le masque donc quand il ne sert pas — et en ASIO,
        /// où c'est le pilote qui fournit entrée et sortie.</summary>
        void UpdateInputRow()
        {
            bool captures = !IsInstrumentMode || (UsesMic && chkMonitor.IsChecked == true);
            var vis = !IsAsio && captures ? Visibility.Visible : Visibility.Collapsed;
            lblInput.Visibility = cboInput.Visibility = vis;
        }

        void UpdateUiState()
        {
            if (_loading) return;
            pnlInstrument.Visibility = IsInstrumentMode ? Visibility.Visible : Visibility.Collapsed;
            pnlMic.Visibility = UsesMic && IsInstrumentMode ? Visibility.Visible : Visibility.Collapsed;
            cboMidiDevice.Visibility = UsesMic ? Visibility.Collapsed : Visibility.Visible;
            UpdateInputRow();

            bool soundFont = cboInstrumentKind.SelectedIndex == 0;
            cboProgram.Visibility = soundFont ? Visibility.Visible : Visibility.Collapsed;
            btnPickPlugin.Visibility = soundFont ? Visibility.Collapsed : Visibility.Visible;
            btnEditInstrument.IsEnabled = !soundFont;
            if (!soundFont)
                btnPickPlugin.Content = string.IsNullOrEmpty(_pluginRef) ? Loc.T("LiveChoose") : PluginLabel(_pluginRef);

            var inst = _engine.Instrument;
            lblInstrument.Text = inst == null ? "" : (string.IsNullOrEmpty(inst.LoadError) ? inst.DisplayName : "⚠ " + inst.LoadError);

            btnStart.Content = _engine.IsRunning ? Loc.T("LiveStop") : Loc.T("LiveStart");
            cboBackend.IsEnabled = !_engine.IsRunning;
            cboInput.IsEnabled = !_engine.IsRunning && _inputs.Count > 0;
            cboOutput.IsEnabled = !_engine.IsRunning && cboOutput.Items.Count > 0;
            cboLatency.IsEnabled = !_engine.IsRunning;
        }

        string PluginLabel(string reference)
        {
            if (cboInstrumentKind.SelectedIndex == 2)
            {
                foreach (var p in KotonPluginRegistry.Instruments)
                    if (string.Equals(p.Id, reference, StringComparison.Ordinal)) return p.DisplayName;
                return "⚠ " + reference;
            }
            return System.IO.Path.GetFileNameWithoutExtension(reference);
        }

        // ---- réglages → moteur ------------------------------------------------------------------------------

        void PushConfig()
        {
            _engine.Backend = IsAsio ? LiveBackend.Asio : LiveBackend.Wasapi;
            _engine.Mode = IsInstrumentMode ? LiveMode.Instrument : LiveMode.Insert;
            _engine.InputDeviceId = DeviceIdAt(_inputs, cboInput.SelectedIndex);
            _engine.OutputDeviceId = IsAsio ? null : DeviceIdAt(_outputs, cboOutput.SelectedIndex);
            _engine.AsioDriver = IsAsio ? cboOutput.SelectedItem as string : null;
            _engine.AsioInputChannel = Math.Max(0, cboAsioChannel.SelectedIndex * 2);
            _engine.LatencyMs = LatencyChoices[Math.Max(0, Math.Min(LatencyChoices.Length - 1, cboLatency.SelectedIndex))];
            _engine.InputGain = sldInGain.Value;
            _engine.OutputGain = sldOutGain.Value;
            // Le monitoring d'entrée n'a de sens qu'en mode instrument avec le micro : en mode effets
            // l'entrée passe DÉJÀ, et avec un clavier MIDI il n'y a rien à écouter.
            _engine.MonitorInput = IsInstrumentMode && UsesMic && chkMonitor.IsChecked == true;
        }

        static string DeviceIdAt(List<LiveDeviceInfo> devices, int index)
            => index >= 0 && index < devices.Count ? devices[index].Id : null;

        void PersistInserts()
        {
            _cfg.Inserts = new List<TrackEffectData>(_engine.Inserts);
            _cfg.Save();
        }

        void SaveSettings()
        {
            _cfg.Backend = IsAsio ? LiveBackend.Asio : LiveBackend.Wasapi;
            _cfg.InputDeviceId = DeviceIdAt(_inputs, cboInput.SelectedIndex) ?? "";
            _cfg.OutputDeviceId = IsAsio ? _cfg.OutputDeviceId : (DeviceIdAt(_outputs, cboOutput.SelectedIndex) ?? "");
            _cfg.AsioDriver = IsAsio ? (cboOutput.SelectedItem as string ?? "") : _cfg.AsioDriver;
            _cfg.AsioInputChannel = Math.Max(0, cboAsioChannel.SelectedIndex * 2);
            _cfg.LatencyMs = LatencyChoices[Math.Max(0, Math.Min(LatencyChoices.Length - 1, cboLatency.SelectedIndex))];
            _cfg.Mode = IsInstrumentMode ? LiveMode.Instrument : LiveMode.Insert;
            _cfg.NoteSource = UsesMic ? LiveNoteSource.Microphone : LiveNoteSource.Midi;
            _cfg.MidiDevice = cboMidiDevice.SelectedItem as string ?? "";
            _cfg.MicDevice = cboMicDevice.SelectedItem as string ?? "";
            _cfg.ScaleMode = cboScale.SelectedIndex - 1;
            _cfg.ScaleRoot = Math.Max(0, cboRoot.SelectedIndex);
            _cfg.OnsetSensitivity = sldOnset.Value;
            _cfg.MicVelocity = (int)Math.Round(sldVelocity.Value);
            _cfg.OctaveShift = cboOctave.SelectedIndex - OctaveRange;
            _cfg.SilenceThreshold = sldThreshold.Value;
            _cfg.MinNoteSeconds = sldMinDuration.Value;
            _cfg.MaxLeapSemitones = (int)Math.Round(sldMaxLeap.Value);
            _cfg.MedianFrames = CurrentSmoothing;
            _cfg.AnalysisFrameSize = CurrentFrameSize;
            _cfg.SnapHysteresisCents = sldSnap.Value;
            _cfg.LowestNoteMidi = CurrentLowestNote;
            _cfg.OctaveBias = sldOctaveBias.Value;
            _cfg.MonitorInput = chkMonitor.IsChecked == true;
            _cfg.InstrumentKind = (LiveInstrumentKind)Math.Max(0, cboInstrumentKind.SelectedIndex);
            _cfg.InstrumentRef = _pluginRef ?? "";
            _cfg.Program = Math.Max(0, cboProgram.SelectedIndex);
            _cfg.InputGain = sldInGain.Value;
            _cfg.OutputGain = sldOutGain.Value;
            _cfg.Inserts = new List<TrackEffectData>(_engine.Inserts);
            _cfg.Save();
        }

        // ---- démarrage / arrêt -------------------------------------------------------------------------------

        void StartStop_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.IsRunning) StopEngine();
            else StartEngine();
            UpdateUiState();
        }

        void StartEngine()
        {
            PushConfig();
            try { _engine.Start(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, string.Format(Loc.T("LiveStartFailed"), ex.Message), Loc.T("LiveTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            // L'instrument ne peut être créé qu'APRÈS le démarrage : sa fréquence d'échantillonnage est celle
            // que le backend a effectivement obtenue, pas celle qu'on espérait.
            if (IsInstrumentMode)
            {
                RebuildInstrument();
                OpenNoteSource();
            }
            SaveSettings();
        }

        void StopEngine()
        {
            CloseNoteSource();
            _engine.Stop();
            _engine.Instrument = null;   // libère le VSTi / plugin Koton (le setter dispose l'ancien)
            SaveSettings();
        }

        void OnEngineFailed(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StopEngine();
                UpdateUiState();
                MessageBox.Show(this, string.Format(Loc.T("LiveStartFailed"), message), Loc.T("LiveTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }));
        }

        // ---- instrument ---------------------------------------------------------------------------------------

        void RebuildInstrument()
        {
            int rate = _engine.SampleRate;
            LiveInstrument inst;
            switch (cboInstrumentKind.SelectedIndex)
            {
                case 1: inst = string.IsNullOrEmpty(_pluginRef) ? null : LiveInstrument.CreateVst(_pluginRef, rate); break;
                case 2: inst = string.IsNullOrEmpty(_pluginRef) ? null : LiveInstrument.CreateKoton(_pluginRef, rate); break;
                default:
                    // Le SoundFont se charge en tâche de fond depuis l'ouverture de la fenêtre : on l'attend
                    // ici (première fois seulement — ensuite la table de presets est en mémoire).
                    try { _soundFontReady?.Wait(TimeSpan.FromSeconds(30)); } catch { }
                    inst = LiveInstrument.CreateSoundFont(Math.Max(0, cboProgram.SelectedIndex), rate);
                    break;
            }
            _engine.Instrument = inst;
            if (inst != null && !string.IsNullOrEmpty(inst.LoadError))
                MessageBox.Show(this, inst.LoadError, Loc.T("LiveInstrumentSection"), MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateUiState();
        }

        void PickPlugin_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = btnPickPlugin, Placement = PlacementMode.Bottom };
            if (cboInstrumentKind.SelectedIndex == 2)
            {
                var list = KotonPluginRegistry.Instruments;
                if (list.Count == 0) menu.Items.Add(new MenuItem { Header = Loc.T("KotonNoPluginsFound"), IsEnabled = false });
                // Regroupés par catégorie : la liste livrée dépasse la vingtaine de plugins.
                KotonPluginMenu.AddGroupedByCategory(menu, list,
                    p => p.Category, p => p.DisplayName,
                    p => string.Equals(p.Id, _pluginRef, StringComparison.Ordinal),
                    p => SelectPlugin(p.Id),
                    p => p.Id);   // écoute au survol
                menu.Items.Add(new Separator());
                var rescan = new MenuItem { Header = Loc.T("KotonRescan") };
                rescan.Click += (s, a) => KotonPluginRegistry.Rescan();
                menu.Items.Add(rescan);
            }
            else
            {
                var list = VstPluginScanner.GetInstruments();
                if (list.Count == 0) menu.Items.Add(new MenuItem { Header = Loc.T("VstiNoInstrumentsFound"), IsEnabled = false });
                foreach (var p in list)
                {
                    string path = p.Path;
                    var it = new MenuItem { Header = p.DisplayName, IsCheckable = true, IsChecked = path == _pluginRef };
                    it.Click += (s, a) => SelectPlugin(path);
                    menu.Items.Add(it);
                }
                menu.Items.Add(new Separator());
                var rescan = new MenuItem { Header = Loc.T("VstRescan") };
                rescan.Click += (s, a) => VstPluginScanner.ForceRescan();
                menu.Items.Add(rescan);
            }
            // Fermeture par Échap ou clic ailleurs : pas toujours de MouseLeave sur l'item survolé.
            menu.Closed += (s, a) => Screens.KotonInstrumentAudition.Stop();
            menu.IsOpen = true;
        }

        void SelectPlugin(string reference)
        {
            _pluginRef = reference;
            if (_engine.IsRunning && IsInstrumentMode) RebuildInstrument();
            UpdateUiState();
            SaveSettings();
        }

        /// <summary>Ouvre l'éditeur du plugin qui JOUE (pas une copie) : bouger un réglage s'entend
        /// immédiatement, ce qui est tout l'intérêt d'un rack live.</summary>
        void EditInstrument_Click(object sender, RoutedEventArgs e)
        {
            var inst = _engine.Instrument;
            if (inst?.Host == null)
            {
                MessageBox.Show(this, Loc.T("LiveStartFirst"), Loc.T("LiveTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (inst.Host is KotonInstrumentAdapter koton)
            {
                new KotonPluginEditorDialog(koton.Plugin, this).Show();
                return;
            }
            if (!VstRuntimeCheck.IsVcRedistInstalled())
            {
                MessageBox.Show(this, Loc.T("VstVcRedistRequired"), Loc.T("FxVstMenu"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            new VstPluginWindow(inst.Host, this).Show();
        }

        // ---- source des notes ---------------------------------------------------------------------------------

        void OpenNoteSource()
        {
            CloseNoteSource();
            if (UsesMic)
            {
                // Le micro qui pilote les notes capture POUR SON COMPTE (WinMM), indépendamment du moteur de
                // sortie : c'est exactement le chemin déjà réglé de l'enregistrement de l'éditeur de riff, et
                // ça évite de contraindre le périphérique de détection à être celui du moteur.
                CacheDetectionParams();
                _mic = new WaveNoteSourceProvider(
                    a => a(),                                   // pas de marshalling UI : les notes vont droit à l'instrument
                    () => _scaleMaskCache,                      // copies, JAMAIS les contrôles : appelé depuis le thread de capture
                    () => _cfg.PitchHold,
                    () => _onsetCache,
                    44100)
                {
                    SilenceThreshold = sldThreshold.Value,
                    MinNoteSeconds = sldMinDuration.Value,
                    MaxLeapSemitones = (int)Math.Round(sldMaxLeap.Value),
                    MedianFrames = CurrentSmoothing,
                    FrameSize = CurrentFrameSize,
                    SnapHysteresisCents = sldSnap.Value,
                    MinFrequency = CurrentMinFrequency,
                    OctaveBias = sldOctaveBias.Value,
                };
                _mic.NoteOn += n =>
                {
                    _lastNote = n + 12; _lastNoteAt = DateTime.UtcNow;   // lu par le vumètre de détection
                    PlaySourceNoteOn(n + 12, _velocityCache);
                };
                _mic.NoteOff += n => PlaySourceNoteOff(n + 12);
                _mic.SetDevice(cboMicDevice.SelectedIndex);
                _mic.Start();
            }
            else
            {
                _midi = new LiveMidiInput();
                _midi.NoteOn += (note, vel) => PlaySourceNoteOn(note, vel);
                _midi.NoteOff += note => PlaySourceNoteOff(note);
                _midi.ControlChange += (cc, v) => _engine.Instrument?.MidiCC(cc, v);
                _midi.PitchBend += v => _engine.Instrument?.PitchBend(v);
                _midi.Open(_midiDevices.Count > 0 ? cboMidiDevice.SelectedIndex : -1);
            }
        }

        void CloseNoteSource()
        {
            lock (_sounding) _sounding.Clear();
            if (_mic != null) { try { _mic.Stop(); _mic.Dispose(); } catch { } _mic = null; }
            if (_midi != null) { try { _midi.Dispose(); } catch { } _midi = null; }
        }

        int CurrentScaleMask()
        {
            int idx = cboScale.SelectedIndex;
            if (idx <= 0) return AudioPitch.Chromatic;
            return AudioPitch.ScaleMask(Math.Max(0, cboRoot.SelectedIndex), idx - 1);
        }

        // ---- événements UI --------------------------------------------------------------------------------------

        void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            // Changer de mode change ce que le moteur capture : on le relance pour que la config prenne.
            bool wasRunning = _engine.IsRunning;
            if (wasRunning) StopEngine();
            UpdateUiState();
            if (wasRunning) StartEngine();
            UpdateUiState();
        }

        void NoteSource_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            UpdateUiState();
            if (_engine.IsRunning && IsInstrumentMode)
            {
                // MonitorInput dépend de la source : en MIDI il n'y a pas d'entrée à écouter, donc le moteur
                // doit repartir pour ouvrir (ou fermer) sa capture.
                bool needsRestart = _engine.MonitorInput != (UsesMic && chkMonitor.IsChecked == true);
                if (needsRestart) { StopEngine(); StartEngine(); }
                else OpenNoteSource();
            }
        }

        void Backend_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            UpdateBackendUi();
            UpdateUiState();
        }

        void MidiDevice_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _midi == null) return;
            _midi.Open(cboMidiDevice.SelectedIndex);
        }

        void MicDevice_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _mic == null) return;
            _mic.SetDevice(cboMicDevice.SelectedIndex);
        }

        void InstrumentKind_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _pluginRef = "";           // un chemin VST n'a aucun sens comme Id Koton : on repart de zéro
            UpdateUiState();
            if (_engine.IsRunning && IsInstrumentMode) RebuildInstrument();
        }

        void Program_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (_engine.IsRunning && IsInstrumentMode && cboInstrumentKind.SelectedIndex == 0) RebuildInstrument();
        }

        void Monitor_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            UpdateUiState();   // écouter l'entrée fait apparaître (ou disparaître) le sélecteur d'entrée du moteur
            if (_engine.IsRunning) { StopEngine(); StartEngine(); UpdateUiState(); }
        }

        void Gain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            _engine.InputGain = sldInGain.Value;
            _engine.OutputGain = sldOutGain.Value;
        }

        void ResetFx_Click(object sender, RoutedEventArgs e) => _engine.ResetEffects();

        void Panic_Click(object sender, RoutedEventArgs e) => _engine.Instrument?.AllNotesOff();

        void AsioPanel_Click(object sender, RoutedEventArgs e)
        {
            string driver = cboOutput.SelectedItem as string;
            if (string.IsNullOrEmpty(driver) || _asioDrivers.Count == 0) return;
            try
            {
                // Le panneau appartient au pilote : on ouvre une instance jetable juste pour l'afficher
                // (impossible sur l'instance qui joue, NAudio ne l'expose pas après Init).
                using (var probe = new NAudio.Wave.AsioOut(driver)) probe.ShowControlPanel();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Loc.T("LiveAsioPanel"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>Nom français d'une note MIDI (Do0 = MIDI 12, comme dans tout le reste de l'app).</summary>
        static string NoteName(int midi) => midi < 0 ? "" : RootNames[((midi % 12) + 12) % 12] + (midi / 12 - 1);

        static double MidiToHz(int midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

        /// <summary>Note la plus grave choisie, en numéro MIDI.</summary>
        int CurrentLowestNote => LowestNoteMin + Math.Max(0, cboLowestNote.SelectedIndex);

        /// <summary>Borne grave passée à l'analyseur : un quart de ton SOUS la note choisie, pour qu'une note
        /// jouée un peu basse reste détectable au lieu de tomber hors plage.</summary>
        double CurrentMinFrequency => MidiToHz(CurrentLowestNote) * Math.Pow(2, -0.5 / 12);

        void UpdateMeters()
        {
            double w = Math.Max(1, ((FrameworkElement)meterIn.Parent).ActualWidth);
            meterIn.Width = w * Math.Min(1.0, _engine.ReadInputPeak());
            meterOut.Width = w * Math.Min(1.0, _engine.ReadOutputPeak());

            // Vumètre de la DÉTECTION : son entrée est un flux séparé (WinMM), invisible des crêtes du
            // moteur. Échelle × 6 pour que le seuil de voisement tombe dans le premier tiers de la barre.
            var mic = _mic;
            if (pnlMic.Visibility == Visibility.Visible)
            {
                double mw = Math.Max(1, ((FrameworkElement)meterMic.Parent).ActualWidth);
                micThreshold.Margin = new Thickness(mw * Math.Min(1.0, sldThreshold.Value * MicMeterScale), 0, 0, 0);
                meterMic.Width = mic == null ? 0 : mw * Math.Min(1.0, mic.InputLevel * MicMeterScale);
                lblDetected.Text = mic == null ? Loc.T("LiveStatusStopped")
                    : (DateTime.UtcNow - _lastNoteAt).TotalSeconds < 1.5 ? NoteName(_lastNote) : "—";
            }
            lblStatus.Text = _engine.IsRunning
                ? string.Format(Loc.T("LiveStatusRunning"), _engine.SampleRate, _engine.ReportedLatencyMs)
                : Loc.T("LiveStatusStopped");
            lblUnderruns.Text = _engine.Underruns > 0 ? string.Format(Loc.T("LiveUnderruns"), _engine.Underruns) : "";
        }

        // ---- fenêtre --------------------------------------------------------------------------------------------

        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            CloseNoteSource();
            SaveSettings();
            try { _engine.Dispose(); } catch { }
            base.OnClosed(e);
        }
    }
}
