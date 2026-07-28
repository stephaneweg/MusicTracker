using MusicTracker.Dialogs;
using MusicTracker.Localization;
using MusicTracker.Screens;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MusicTracker
{
    /// <summary>
    /// Application shell: a custom TAB STRIP of buttons (permanent "Accueil" + one closable button per open music) over a
    /// single content host. Opening / creating / AI-composing a piece adds a tab, so several pieces stay open side by side.
    /// </summary>
    public partial class MainWindow : Window
    {
        HomeScreen homeScreen;
        Button homeBtn;
        readonly List<(IMusicEditor editor, Button btn)> editorTabs = new List<(IMusicEditor, Button)>();
        object current; // the content shown in `host` (homeScreen or an editor)

        public MainWindow()
        {
            InitializeComponent();
            FileAssociations.EnsureRegistered();
            Engine.AudioFormat.SampleRate = AppSettings.Instance.SampleRate;

            homeScreen = new HomeScreen();
            homeScreen.NewSequencerRequested += () => OpenEditor(new TimelineScreen(), null);
            homeScreen.OpenRequested += OpenDialog;
            homeScreen.OpenRecentRequested += (entry) => OpenPath(entry.Path);
            homeScreen.ComposeAiRequested += ComposeWithAiNewTab;
            homeScreen.TemplateSpecRequested += OpenTemplateSpec;
            homeScreen.TutorialRequested += StartTutorialInNewTab;
            homeBtn = new Button { Style = (Style)Resources["TabButton"], Padding = new Thickness(16, 0, 16, 0), Content = Loc.T("Home"),
                                   Background = new SolidColorBrush(Color.FromRgb(0x24, 0x25, 0x2C)) }; // a bit darker than the music tabs
            System.Windows.Automation.AutomationProperties.SetAutomationId(homeBtn, "BtnHomeTab");
            homeBtn.Click += (s, e) => Select(homeScreen);
            tabStrip.Children.Add(homeBtn);
            Select(homeScreen);

            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && !string.IsNullOrEmpty(args[1]) && System.IO.File.Exists(args[1]))
            {
                string file = args[1];
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() => OpenPath(file)));
            }
        }

        // ===== Custom window chrome (title bar buttons + drag/resize) ================

        private void btnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void btnMaxRestore_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void btnClose_Click(object sender, RoutedEventArgs e) => Close();

        // Keep the maximize/restore glyph in sync, and compensate the WindowChrome overflow
        // that would otherwise push a few pixels of content off-screen when maximized.
        private void Window_StateChanged(object sender, EventArgs e)
        {
            bool max = WindowState == WindowState.Maximized;
            if (btnMaxRestore != null)
            {
                btnMaxRestore.Content = max ? "" : "";   // Restore : Maximize (Segoe MDL2 Assets)
                btnMaxRestore.ToolTip = max ? Loc.T("Restore") : Loc.T("Maximize");
            }
            if (rootDock != null)
                rootDock.Margin = max ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left + 1) : new Thickness(0);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var src = (HwndSource)PresentationSource.FromVisual(this);
            src?.AddHook(WindowProc);
            Window_StateChanged(this, EventArgs.Empty); // apply the maximized margin/glyph on first show
        }

        // Constrain a maximized window to the current monitor's WORK AREA (respects the taskbar,
        // DPI-safe on multi-monitor setups). Without this a WindowChrome maximize covers the taskbar.
        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg == WM_GETMINMAXINFO)
            {
                var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                IntPtr monitor = MonitorFromWindow(hwnd, 0x00000002 /*MONITOR_DEFAULTTONEAREST*/);
                if (monitor != IntPtr.Zero)
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    GetMonitorInfo(monitor, ref info);
                    RECT work = info.rcWork, mon = info.rcMonitor;
                    mmi.ptMaxPosition.X = work.Left - mon.Left;
                    mmi.ptMaxPosition.Y = work.Top - mon.Top;
                    mmi.ptMaxSize.X = work.Right - work.Left;
                    mmi.ptMaxSize.Y = work.Bottom - work.Top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var dlg = new Dialogs.ImportProgressDialog { Owner = this };
            dlg.SetBusy(Loc.T("LoadingInstrumentsSoundFont"));
            dlg.Show();
            try { await System.Threading.Tasks.Task.Run(() => AppSettings.Instance.Apply()); }
            catch (Exception ex) { MessageBox.Show("SoundFont load error : " + ex.Message); }
            finally { dlg.Close(); }

            // Fresh install with no SoundFont: offer to download the MuseScore default (≈206 MB) so audio works out
            // of the box. If the user declines or it fails, say so up front instead of letting them discover it as
            // unexplained silence on the first Play (SoundFonts ship separately — see SoundFontGuard).
            if (!SoundFontGuard.IsReady && !await TryDownloadDefaultSoundFontAsync())
                SoundFontGuard.CheckAtStartup(this);

            // Then, best-effort, see whether a newer build has been published.
            await CheckForUpdatesAsync();

            // First launch: offer the guided tour once (relaunchable anytime from the home screen's "Tutoriel" tile).
            if (!UserData.Instance.TutorialShown)
            {
                UserData.Instance.TutorialShown = true;
                UserData.Instance.Save();
                if (Dialogs.ConfirmDialog.Ask(this, Loc.T("TourOfferTitle"), Loc.T("TourOfferText"),
                                              Loc.T("TourOfferYes"), Loc.T("TourOfferNo")))
                    StartTutorialInNewTab();
            }
        }

        // Open a fresh timeline tab and run the interactive guided tour over it (once it has laid out).
        void StartTutorialInNewTab()
        {
            var editor = new TimelineScreen();
            OpenEditor(editor, null);
            Dispatcher.BeginInvoke(new Action(() => editor.StartTutorial()),
                                   System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Startup update check (best-effort, non-blocking of the UI thread's responsiveness): read the manifest, and
        /// if a newer version exists, offer to download + launch the installer and quit. Any failure is swallowed so a
        /// flaky network never nags the user.
        /// </summary>
        private async System.Threading.Tasks.Task CheckForUpdatesAsync()
        {
            Engine.Update.UpdateInfo info;
            try { info = await Engine.Update.UpdateChecker.CheckAsync(System.Threading.CancellationToken.None); }
            catch { return; }
            if (info == null || !info.IsNewer) return;

            var prompt = new Dialogs.UpdateDialog(info) { Owner = this };
            if (prompt.ShowDialog() != true) return;

            var dlg = new Dialogs.ImportProgressDialog { Owner = this };
            dlg.SetBusy(Loc.T("DownloadingTheUpdate"));
            dlg.Show();
            var progress = new Progress<Engine.Update.UpdateChecker.DownloadProgress>(p =>
            {
                long got = p.Received / (1024 * 1024);
                if (p.Total > 0)
                    dlg.Set((double)p.Received / p.Total, Loc.T("DownloadingTheUpdate2") + got + " / " + (p.Total / (1024 * 1024)) + Loc.T("MB"));
                else
                    dlg.SetBusy(Loc.T("DownloadingTheUpdate2") + got + Loc.T("MB"));
            });

            string dest = System.IO.Path.Combine(System.IO.Path.GetTempPath(), info.InstallerFileName);
            try { await Engine.Update.UpdateChecker.DownloadInstallerAsync(info, dest, progress, System.Threading.CancellationToken.None); }
            catch (Exception ex)
            {
                dlg.Close();
                MessageBox.Show(this, Loc.T("TheUpdateDownloadFailed") + ex.Message,
                                Loc.T("Update2"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            dlg.Close();

            try { Engine.Update.UpdateChecker.LaunchInstaller(dest); }
            catch (Exception ex)
            {
                MessageBox.Show(this, Loc.T("CouldNotLaunchTheInstaller") + ex.Message,
                                Loc.T("Update2"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Application.Current.Shutdown(); // quit so the installer can replace the running app
        }

        /// <summary>
        /// No SoundFont installed: ask, then download the MuseScore default with a progress dialog, select it as the
        /// default and hot-reload the engine. Returns true only when playback is ready afterwards.
        /// </summary>
        private async System.Threading.Tasks.Task<bool> TryDownloadDefaultSoundFontAsync()
        {
            long mb = SoundFontDownloader.ApproxBytes / (1024 * 1024);
            var ask = MessageBox.Show(this,
                Loc.T("NoSoundFontIsInstalledNoSound") +
                Loc.T("DownloadMuseScoreSDefaultSoundFont") + SoundFontDownloader.FileName + ", ≈ " + mb + Loc.T("MBNowItWillBeInstalled"),
                Loc.T("SoundFontMissing"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes) return false;

            var dlg = new Dialogs.ImportProgressDialog { Owner = this };
            dlg.SetBusy(Loc.T("DownloadingTheSoundFont"));
            dlg.Show();
            var progress = new Progress<SoundFontDownloader.Progress>(p =>
            {
                long got = p.Received / (1024 * 1024);
                if (p.Total > 0)
                    dlg.Set((double)p.Received / p.Total, Loc.T("DownloadingTheSoundFont2") + got + " / " + (p.Total / (1024 * 1024)) + Loc.T("MB"));
                else
                    dlg.SetBusy(Loc.T("DownloadingTheSoundFont2") + got + Loc.T("MB"));
            });

            try { await SoundFontDownloader.DownloadAsync(progress); }
            catch (Exception ex)
            {
                dlg.Close();
                MessageBox.Show(this,
                    Loc.T("TheSoundFontDownloadFailed") + ex.Message +
                    Loc.T("YouCanTryAgainAtThe"),
                    "SoundFont", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // Installed: make it the default and (re)load the engine at the current sample rate.
            dlg.SetBusy(Loc.T("LoadingInstrumentsSoundFont"));
            AppSettings.Instance.SoundFont = SoundFontDownloader.FileName;
            AppSettings.Instance.Save();
            try { await System.Threading.Tasks.Task.Run(() => AppSettings.Instance.Apply()); }
            catch (Exception ex) { MessageBox.Show("SoundFont load error : " + ex.Message); }
            finally { dlg.Close(); }

            return SoundFontGuard.IsReady;
        }

        // ===== Tabs ==================================================================

        void OpenEditor(IMusicEditor editor, string path)
        {
            if (editor is TimelineScreen ts)
            {
                ts.ComposeInNewTabRequested += ComposeWithAiNewTab;   // its AI menu spawns a new tab
                ts.ComposePolyInNewTabRequested += ComposePolyWithAiNewTab;
                ts.SaveRequested += () => SaveEditor(editor);          // its toolbar "Enregistrer" button
            }
            var btn = new Button { Style = (Style)Resources["TabButton"], Padding = new Thickness(14, 0, 10, 0) };
            SetTabButtonContent(btn, editor, TabTitle(path), true);
            btn.Click += (s, e) => Select(editor);
            tabStrip.Children.Add(btn);
            editorTabs.Add((editor, btn));
            Select(editor);
        }

        // Tab button content = name + a ✕ (close). The ✕ closes without triggering the button's select (Preview + Handled).
        void SetTabButtonContent(Button btn, IMusicEditor editor, string title, bool closable)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
            if (closable)
            {
                var close = new TextBlock
                {
                    Text = "✕",
                    FontSize = 11,
                    Margin = new Thickness(10, 0, 0, 0),
                    Padding = new Thickness(2, 0, 2, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xAA, 0xAA)),
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = Loc.T("Close"),
                };
                close.PreviewMouseLeftButtonDown += (s, e) => { e.Handled = true; CloseEditor(editor); };
                sp.Children.Add(close);
            }
            btn.Content = sp;
        }

        void Select(object content)
        {
            current = content;
            host.Content = content;
            // Highlight the active MUSIC tab; the Accueil button stays at its darker shade (never highlighted).
            // Stop audio on every editor that isn't the one shown.
            foreach (var (editor, btn) in editorTabs)
            {
                bool active = ReferenceEquals(editor, content);
                btn.Tag = active ? "sel" : null;
                if (!active) editor.StopAudio();
            }
        }

        void CloseEditor(IMusicEditor editor)
        {
            int i = editorTabs.FindIndex(t => ReferenceEquals(t.editor, editor));
            if (i < 0) return;
            editor.StopAudio();
            tabStrip.Children.Remove(editorTabs[i].btn);
            editorTabs.RemoveAt(i);
            if (ReferenceEquals(current, editor))
                Select(editorTabs.Count > 0 ? (object)editorTabs[Math.Min(i, editorTabs.Count - 1)].editor : homeScreen);
        }

        static string TabTitle(string path) => string.IsNullOrEmpty(path) ? Loc.T("New") : System.IO.Path.GetFileName(path);

        void SetEditorTitle(IMusicEditor editor, string path)
        {
            int i = editorTabs.FindIndex(t => ReferenceEquals(t.editor, editor));
            if (i >= 0) SetTabButtonContent(editorTabs[i].btn, editor, TabTitle(path), true);
        }

        // ===== Actions ===============================================================

        private void btnSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

        private void OpenSettings()
        {
            var dlg = new Dialogs.SettingsDialog { Owner = this };
            dlg.ShowDialog();
        }

        // "Aide": open the help menu (Signaler un bug + À propos) anchored under the button.
        private void btnHelp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.ContextMenu != null)
            {
                fe.ContextMenu.PlacementTarget = fe;
                fe.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                fe.ContextMenu.IsOpen = true;
            }
        }

        // "Signaler un bug": files a GitHub issue with an optional attachment of the current project (+ its template).
        private void miReportBug_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Dialogs.ReportBugDialog(current as TimelineScreen) { Owner = this };
            dlg.ShowDialog();
        }

        // "À propos": app name/version, author and third-party credits (MeltySynth, …).
        private void miAbout_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Dialogs.AboutDialog { Owner = this };
            dlg.ShowDialog();
        }

        // The editor's own "Enregistrer" button (in its toolbar) calls back here.
        void SaveEditor(IMusicEditor editor)
        {
            if (editor == null) return;
            string path = editor.CurrentPath;
            if (string.IsNullOrEmpty(path))
            {
                var dlg = new Dialogs.FileBrowserDialog
                {
                    SaveMode = true,
                    Owner = this,
                    Filter = editor.ModeName + " (*" + editor.FileExtension + ")|*" + editor.FileExtension,
                    DefaultExt = editor.FileExtension,
                };
                if (dlg.ShowDialog() != true) return;
                path = dlg.FileName;
            }
            try { editor.Save(path); }
            catch (Exception ex) { MessageBox.Show("Save error : " + ex.Message); return; }
            RecentFiles.Instance.Add(path, editor.ModeName);
            SetEditorTitle(editor, path);
        }

        // Open a section-based AI template (TemplateLibrary / Data/templates) in a new UNSAVED tab, expanded to a bar count.
        private void OpenTemplateSpec(string name)
        {
            var spec = Engine.Timeline.TemplateLibrary.Find(name);
            if (spec == null) return;
            var ask = new Dialogs.TemplateMeasuresDialog(spec.Name) { Owner = this };
            if (ask.ShowDialog() != true) return;
            int measures = ask.Measures;

            var editor = new TimelineScreen();
            OpenEditor(editor, null);
            Defer(() => editor.LoadTemplateSpec(spec, measures));
        }

        private void OpenDialog()
        {
            var dlg = new Dialogs.FileBrowserDialog
            {
                Owner = this,
                Filter = Loc.T("MusicSqMidMsczMscxSq"),
            };
            if (dlg.ShowDialog() == true) OpenPath(dlg.FileName);
        }

        private void OpenPath(string path)
        {
            string ext = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".sq": case ".mid": case ".midi": case ".mscz": case ".mscx":
                    break;
                default:
                    MessageBox.Show(Loc.T("UnrecognisedFileType") + ext);
                    return;
            }
            var editor = new TimelineScreen();
            OpenEditor(editor, path);
            Defer(() => { editor.LoadFile(path); SetEditorTitle(editor, path); });
            RecentFiles.Instance.Add(path, editor.ModeName);
        }

        // "Composer avec l'IA" (from Home or an editor's menu) → the AI SOURCE builds a document (owning its dialog);
        // the shell just loads it on a NEW tab, agnostic to how it was produced.
        void ComposeWithAiNewTab() => OpenFromSource(new Sources.AiComposeSource());
        // Pendant polyrythmique : ouvre AiPolyDialog puis pose une timeline avec les deux modules poly (percussion + mélodique).
        void ComposePolyWithAiNewTab() => OpenFromSource(new Sources.AiPolySource());

        // Run a project source (it may show its own dialog), and if it produced a document, load it on a new tab.
        void OpenFromSource(Sources.IProjectSource source)
        {
            var doc = source.Produce(this);
            if (doc == null) return;
            var editor = new TimelineScreen();
            OpenEditor(editor, null);
            Defer(() =>
            {
                try { editor.LoadDocument(doc); }
                catch (Exception ex) { MessageBox.Show(Loc.T("CouldNotLoad") + ex.Message); }
            });
        }

        private void Defer(Action action)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                try { action(); }
                catch (Exception ex) { MessageBox.Show("Open error : " + ex.Message); }
            }));
        }
    }
}
