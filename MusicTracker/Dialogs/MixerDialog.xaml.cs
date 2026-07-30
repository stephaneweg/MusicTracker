using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MusicTracker.Engine.Timeline;
using MusicTracker.Engine.Timeline.Effects;
using MusicTracker.Localization;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// Mixeur type console : une strip verticale par piste (vol/pan/mute/solo, chaîne d'inserts, vu-mètre),
    /// plus une strip Master à droite (inserts globaux + vu-mètre master). Les strips sont construites
    /// PROGRAMMATIQUEMENT (l'ItemsControl du XAML n'est qu'un conteneur) pour garder la main sur les
    /// rectangles de vu-mètre — un DataTemplate imposerait une gymnastique de bindings pour ce qui n'est
    /// finalement qu'une largeur/hauteur en fonction d'un peak float lu par timer.
    /// Non modal (Show), la lecture continue et les faders sont pris en compte en direct par le player.
    /// </summary>
    public partial class MixerDialog : Window
    {
        readonly TimelineProject project;
        readonly Screens.TimelineScreen screen;   // pour lire les peaks du player pendant la lecture (peut être null)
        readonly DispatcherTimer meterTimer;
        readonly Dictionary<TimelineTrack, (Rectangle L, Rectangle R)> meters = new Dictionary<TimelineTrack, (Rectangle, Rectangle)>();
        readonly Dictionary<TimelineTrack, StackPanel> insertsHosts = new Dictionary<TimelineTrack, StackPanel>();

        public MixerDialog(TimelineProject project, Screens.TimelineScreen owner)
        {
            InitializeComponent();
            Owner = Window.GetWindow(owner);
            this.project = project;
            this.screen = owner;
            RefreshTracks();
            meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(45) };
            meterTimer.Tick += (s, e) => UpdateMeters();
            meterTimer.Start();
        }

        /// <summary>Reconstruire les strips après un ajout/retrait/réorganisation de pistes côté timeline.</summary>
        public void RefreshTracks()
        {
            list.Items.Clear();
            meters.Clear();
            insertsHosts.Clear();
            if (project?.Tracks == null) return;
            foreach (var tr in project.Tracks) list.Items.Add(BuildStrip(tr));
            RefreshMasterInserts();
        }

        // Une strip par piste : vertical, ~86 px de large — largeur d'une console typique.
        FrameworkElement BuildStrip(TimelineTrack tr)
        {
            var host = new Border
            {
                Background = (Brush)FindResource("CommonBackground"),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x40)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 8, 6, 8),
                Margin = new Thickness(3),
                Width = 86,
            };
            var col = new StackPanel();

            var name = new TextBlock
            {
                FontSize = 11, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4),
                Foreground = (Brush)FindResource("CommonForeground"),
            };
            name.SetBinding(TextBlock.TextProperty, new Binding("Name") { Source = tr });
            col.Children.Add(name);

            // Pan (slider fin horizontal).
            var panRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var panLbl = new TextBlock { Text = Loc.T("Pan"), Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), FontSize = 9, VerticalAlignment = VerticalAlignment.Center };
            var panVal = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), FontSize = 9, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MinWidth = 32 };
            panVal.SetBinding(TextBlock.TextProperty, new Binding("Pan") { Source = tr, Converter = (IValueConverter)FindResource("panText") });
            DockPanel.SetDock(panLbl, Dock.Left);
            DockPanel.SetDock(panVal, Dock.Right);
            panRow.Children.Add(panLbl); panRow.Children.Add(panVal);
            var panSlider = new Slider { Minimum = -1, Maximum = 1, SmallChange = 0.05, VerticalAlignment = VerticalAlignment.Center };
            panSlider.SetBinding(Slider.ValueProperty, new Binding("Pan") { Source = tr, Mode = BindingMode.TwoWay });
            panSlider.MouseDoubleClick += (s, e) => { tr.Pan = 0; e.Handled = true; };
            panRow.Children.Add(panSlider);
            col.Children.Add(panRow);

            // Fader (vertical) + vu-mètre à sa droite.
            var faderMeter = new Grid { Height = 150, Margin = new Thickness(0, 4, 0, 4) };
            faderMeter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            faderMeter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var fader = new Slider { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 1.5, SmallChange = 0.05, HorizontalAlignment = HorizontalAlignment.Center };
            fader.SetBinding(Slider.ValueProperty, new Binding("Volume") { Source = tr, Mode = BindingMode.TwoWay });
            fader.ToolTip = Loc.T("Vol");
            Grid.SetColumn(fader, 0);
            faderMeter.Children.Add(fader);

            var meterBox = new StackPanel { Orientation = Orientation.Horizontal, Width = 14, Margin = new Thickness(3, 0, 0, 0) };
            Grid.SetColumn(meterBox, 1);
            var meterL = MakeMeter(); var meterR = MakeMeter();
            meterBox.Children.Add(meterL); meterBox.Children.Add(meterR);
            faderMeter.Children.Add(meterBox);
            col.Children.Add(faderMeter);
            meters[tr] = (meterL, meterR);

            // Mute / Solo.
            var ms = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 4) };
            var muteBtn = new ToggleButton { Content = "M", Width = 22, Height = 20, Style = (Style)FindResource("MixToggle"), ToolTip = Loc.T("MuetSilenceCettePiste") };
            muteBtn.SetBinding(ToggleButton.IsCheckedProperty, new Binding("Mute") { Source = tr, Mode = BindingMode.TwoWay });
            var soloBtn = new ToggleButton { Content = "S", Width = 22, Height = 20, Style = (Style)FindResource("MixToggle"), Margin = new Thickness(3, 0, 0, 0), ToolTip = Loc.T("SoloNEntendreQueLesPistes") };
            soloBtn.SetBinding(ToggleButton.IsCheckedProperty, new Binding("Solo") { Source = tr, Mode = BindingMode.TwoWay });
            ms.Children.Add(muteBtn); ms.Children.Add(soloBtn);
            col.Children.Add(ms);

            // Inserts (chaîne d'effets).
            var fxLbl = new TextBlock { Text = Loc.T("Inserts"), Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), FontSize = 9, Margin = new Thickness(0, 4, 0, 2) };
            col.Children.Add(fxLbl);
            var fxHost = new StackPanel();
            col.Children.Add(fxHost);
            insertsHosts[tr] = fxHost;
            RefreshInsertsForTrack(tr);
            var addFx = new Button { Content = Loc.T("AddFxDots"), Style = (Style)FindResource("TinyButton"), Margin = new Thickness(0, 2, 0, 0) };
            addFx.Click += (a, b) => ShowAddFxMenu(addFx, tr.Inserts, () => { RefreshInsertsForTrack(tr); CaptureUndo(); });
            col.Children.Add(addFx);

            host.Child = col;
            return host;
        }

        static Rectangle MakeMeter() => new Rectangle
        {
            Width = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Fill = new SolidColorBrush(Color.FromRgb(0x1F, 0xB6, 0xC3)),
            Height = 0,
            Margin = new Thickness(1, 0, 1, 0),
        };

        void RefreshInsertsForTrack(TimelineTrack tr)
        {
            if (!insertsHosts.TryGetValue(tr, out var host)) return;
            host.Children.Clear();
            if (tr.Inserts == null) return;
            for (int i = 0; i < tr.Inserts.Count; i++)
            {
                var d = tr.Inserts[i];
                host.Children.Add(BuildInsertRow(d, tr.Inserts, () => { RefreshInsertsForTrack(tr); CaptureUndo(); }));
            }
        }

        void RefreshMasterInserts()
        {
            masterInsertsList.Items.Clear();
            if (project?.MasterInserts == null) return;
            for (int i = 0; i < project.MasterInserts.Count; i++)
            {
                var d = project.MasterInserts[i];
                masterInsertsList.Items.Add(BuildInsertRow(d, project.MasterInserts, () => { RefreshMasterInserts(); CaptureUndo(); }));
            }
        }

        // Une ligne d'insert : bouton étiquette (édition), toggle actif, bouton suppression.
        FrameworkElement BuildInsertRow(TrackEffectData d, List<TrackEffectData> owner, Action onChanged)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Le libellé d'un VST = nom du fichier (le vrai « nice name » n'est connu qu'après chargement du plugin).
            string label = d.Kind == EffectFactory.VstKind && !string.IsNullOrEmpty(d.PluginPath)
                ? System.IO.Path.GetFileNameWithoutExtension(d.PluginPath)
                : Loc.T(EffectFactory.LocKey(d.Kind));
            var edit = new Button { Content = label, Style = (Style)FindResource("TinyButton"), HorizontalContentAlignment = HorizontalAlignment.Left };
            edit.Opacity = d.Enabled ? 1.0 : 0.5;
            edit.ToolTip = Loc.T("EditEffect");
            edit.Click += (s, e) => { OpenEffectEditor(d); onChanged(); };
            Grid.SetColumn(edit, 0);
            row.Children.Add(edit);

            var onoff = new ToggleButton { Content = "•", Width = 16, Height = 18, Style = (Style)FindResource("MixToggle"), Margin = new Thickness(3, 0, 0, 0), IsChecked = d.Enabled, ToolTip = Loc.T("ToggleEffect") };
            onoff.Checked += (s, e) => { d.Enabled = true; edit.Opacity = 1.0; onChanged(); };
            onoff.Unchecked += (s, e) => { d.Enabled = false; edit.Opacity = 0.5; onChanged(); };
            Grid.SetColumn(onoff, 1);
            row.Children.Add(onoff);

            var del = new Button { Content = "×", Width = 16, Height = 18, Style = (Style)FindResource("TinyButton"), Margin = new Thickness(3, 0, 0, 0), ToolTip = Loc.T("RemoveEffect") };
            del.Click += (s, e) => { owner.Remove(d); onChanged(); };
            Grid.SetColumn(del, 2);
            row.Children.Add(del);

            return row;
        }

        // Menu contextuel « Ajouter un effet » → un item par type déclaré dans EffectFactory.Kinds + un sous-menu VST.
        void ShowAddFxMenu(FrameworkElement anchor, List<TrackEffectData> owner, Action onChanged)
        {
            var m = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            foreach (var kind in EffectFactory.Kinds)
            {
                var it = new MenuItem { Header = Loc.T(EffectFactory.LocKey(kind)) };
                string k = kind;
                it.Click += (s, e) => { owner.Add(new TrackEffectData { Kind = k, Enabled = true }); onChanged(); };
                m.Items.Add(it);
            }
            m.Items.Add(new Separator());
            BuildVstSubmenu(m, owner, onChanged);
            m.IsOpen = true;
        }

        /// <summary>Sous-menu « VST ▸ ... » listant les plugins scannés, avec un item « Re-scanner ». Vide = message explicatif.</summary>
        void BuildVstSubmenu(ContextMenu parent, List<TrackEffectData> owner, Action onChanged)
        {
            var vstMenu = new MenuItem { Header = Loc.T("FxVstMenu") };
            var list = VstPluginScanner.GetPlugins();
            if (list.Count == 0)
            {
                var empty = new MenuItem { Header = Loc.T("VstNoPluginsFound"), IsEnabled = false };
                vstMenu.Items.Add(empty);
            }
            else
            {
                foreach (var p in list)
                {
                    var it = new MenuItem { Header = p.DisplayName };
                    var path = p.Path;
                    it.Click += (s, e) => TryAddVst(path, owner, onChanged);
                    vstMenu.Items.Add(it);
                }
            }
            vstMenu.Items.Add(new Separator());
            var rescan = new MenuItem { Header = Loc.T("VstRescan") };
            rescan.Click += (s, e) => { VstPluginScanner.ForceRescan(); };
            vstMenu.Items.Add(rescan);
            parent.Items.Add(vstMenu);
        }

        /// <summary>Ajoute un insert VST après avoir vérifié le runtime VC++ 2012 — sinon propose le lien de téléchargement.</summary>
        void TryAddVst(string path, List<TrackEffectData> owner, Action onChanged)
        {
            if (!VstRuntimeCheck.IsVcRedistInstalled())
            {
                var msg = Loc.T("VstVcRedistRequired");
                var res = MessageBox.Show(this, msg, Loc.T("FxVstMenu"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res == MessageBoxResult.Yes)
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(VstRuntimeCheck.VcRedistDownloadUrl) { UseShellExecute = true }); }
                    catch { }
                }
                return;
            }
            owner.Add(new TrackEffectData { Kind = EffectFactory.VstKind, Enabled = true, PluginPath = path });
            onChanged();
        }

        /// <summary>Ouvre l'éditeur adapté à l'effet : GUI native pour un VST, dialogue de sliders génériques sinon.</summary>
        void OpenEffectEditor(TrackEffectData d)
        {
            if (d.Kind == EffectFactory.VstKind)
            {
                if (!VstRuntimeCheck.IsVcRedistInstalled())
                {
                    MessageBox.Show(this, Loc.T("VstVcRedistRequired"), Loc.T("FxVstMenu"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // Instance VstEffect dédiée à l'UI (indépendante de celle du renderer, qui vit dans un thread audio).
                // Ce n'est PAS l'instance qui joue — l'UI ouvre sa propre copie pour afficher/éditer les paramètres
                // via la GUI native, et on récupère le nouveau chunk d'état à la fermeture pour le pousser dans le
                // TrackEffectData → au prochain Start de lecture, le renderer relit ce chunk.
                var fx = new VstEffect(44100) { PluginPath = d.PluginPath };
                if (!string.IsNullOrEmpty(d.StateBlob)) fx.LoadState(d.StateBlob);
                if (!fx.EnsureOpenedSync(512))
                {
                    MessageBox.Show(this, Loc.T("VstPluginFailedToLoad"), fx.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
                    try { fx.Dispose(); } catch { }
                    return;
                }
                var w = new VstPluginWindow(fx, this);
                w.Closed += (s, e) =>
                {
                    // Récupérer l'état du plugin AVANT de le disposer, puis le pousser dans le TrackEffectData.
                    try { d.StateBlob = fx.SaveState(); } catch { }
                    try { fx.Dispose(); } catch { }
                    CaptureUndo();
                };
                w.Show();
                return;
            }
            new EffectConfigDialog(d, this).ShowDialog();
        }

        void MasterAddFx_Click(object sender, RoutedEventArgs e)
        {
            if (project == null) return;
            ShowAddFxMenu((FrameworkElement)sender, project.MasterInserts, () => { RefreshMasterInserts(); CaptureUndo(); });
        }

        // Rafraîchit tous les vu-mètres à partir des peaks du player en cours de lecture (0 sinon).
        void UpdateMeters()
        {
            var p = screen?.CurrentPlayer;
            if (project?.Tracks == null) return;
            for (int i = 0; i < project.Tracks.Count; i++)
            {
                var tr = project.Tracks[i];
                if (!meters.TryGetValue(tr, out var mm)) continue;
                float pl = p != null ? p.TrackPeakL(i) : 0f;
                float pr = p != null ? p.TrackPeakR(i) : 0f;
                mm.L.Height = ScaleMeter(pl, mm.L);
                mm.R.Height = ScaleMeter(pr, mm.R);
            }
            float mpl = p?.MasterPeakL ?? 0f;
            float mpr = p?.MasterPeakR ?? 0f;
            masterMeterL.Height = ScaleMeter(mpl, masterMeterL);
            masterMeterR.Height = ScaleMeter(mpr, masterMeterR);
        }

        static double ScaleMeter(float peak, Rectangle r)
        {
            // Peak logarithmique compressé : 0 dBFS = 1.0 → hauteur max, -60 dBFS ≈ 0. Fill "safe" en cyan
            // jusqu'à -6 dB, jaune au-dessus, rouge en clip. Pour rester simple v1, une seule couleur.
            var host = r.Parent as FrameworkElement;
            double h = host?.ActualHeight ?? 0;
            if (h < 1 || peak <= 0.0005f) return 0;
            double db = 20.0 * Math.Log10(Math.Min(1.5f, peak));
            double norm = Math.Max(0, Math.Min(1, (db + 60.0) / 60.0));
            return h * norm;
        }

        // Capture un snapshot dans l'undo (ajout/retrait d'effet — édition des paramètres, non capturée v1).
        void CaptureUndo()
        {
            try { screen?.CaptureUndoFromMixer(); } catch { }
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            try { meterTimer.Stop(); } catch { }
            base.OnClosed(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
            base.OnKeyDown(e);
        }
    }

    /// <summary>Pan value (-1..+1) → a short label: "G xx%" (left) / "Centre" / "D xx%" (right).</summary>
    public sealed class PanTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double pan = value is double d ? d : 0;
            if (pan < -0.02) return Loc.T("PanL") + " " + Math.Round(-pan * 100) + "%";
            if (pan > 0.02) return Loc.T("PanR") + " " + Math.Round(pan * 100) + "%";
            return Loc.T("PanCentre");
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
