using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MusicTracker.Engine.ComposerV2;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// "Analyser un nouveau modèle": pick one or more folders of scores/MIDI, choose the per-dimension Markov ORDERS
    /// (harmony + melody), analyze them (off the UI thread, with a progress bar), and write the resulting corpus model
    /// into Data\models\&lt;name&gt;.json — immediately available in the compose dialog. Can also RE-OPEN an existing
    /// model: its source folders + orders are read back and shown, so it can be re-analyzed with adjusted settings.
    /// Code-only WPF window.
    /// </summary>
    public sealed class NewModelDialog : Window
    {
        // configurable Markov orders — harmony + melody dimensions (key, label, default)
        static readonly string[] OrderKeys = { "melody", "rhythmCell", "harmonyRoot", "harmonicRhythm", "accompCell", "accompTone" };
        static readonly string[] OrderLabels = { "Mélodie — hauteur", "Mélodie — rythme", "Harmonie — accords", "Harmonie — rythme", "Accomp. — rythme", "Accomp. — hauteur" };
        static readonly int[] OrderDefaults = { 8, 8, 2, 4, 8, 3 };

        readonly ListBox folderList = new ListBox { Height = 88, Margin = new Thickness(0, 0, 0, 6) };
        readonly TextBox nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        readonly TextBox[] orderBoxes = new TextBox[OrderKeys.Length];
        readonly ProgressBar bar = new ProgressBar { Height = 16, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 2) };
        readonly TextBlock status = new TextBlock { Foreground = Brushes.Gray, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };
        readonly Button addBtn = new Button { Content = "＋", Width = 28, Padding = new Thickness(0, 1, 0, 1), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Ajouter un dossier…" };
        readonly Button loadBtn = new Button { Content = "Charger un modèle…", Padding = new Thickness(10, 3, 10, 3), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Ré-ouvrir un modèle existant (dossiers + ordres) pour le ré-analyser" };
        readonly Button analyzeBtn = new Button { Content = "Analyser", Padding = new Thickness(16, 4, 16, 4), Cursor = System.Windows.Input.Cursors.Hand };
        readonly Button cancelBtn = new Button { Content = "Annuler", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand };
        readonly List<string> folders = new List<string>();

        /// <summary>Set on success: the model file name created in Data\models\ (e.g. "chopin.json").</summary>
        public string CreatedModelFile { get; private set; }

        static Brush Res(string key) => (Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null) ?? Brushes.Gray;
        static void ThemeBox(TextBox tb) { tb.Foreground = Res("CommonForeground"); tb.Background = Res("TextBoxOuterBackground"); tb.BorderBrush = Res("TextBoxOuterBorder"); }

        public NewModelDialog()
        {
            Title = "Analyser un nouveau modèle";
            Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ThemeBox(nameBox);

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            root.Children.Add(new TextBlock { Text = "＋ Analyser un nouveau modèle", Foreground = Res("CommonForeground"), FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });

            // header row: "Dossiers à analyser…"  +  a "＋" button on the right
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            DockPanel.SetDock(addBtn, Dock.Right);
            header.Children.Add(addBtn);
            header.Children.Add(new TextBlock { Text = "Dossiers à analyser (partitions / MIDI, récursif) :", Foreground = Res("CommonForeground"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
            root.Children.Add(header);
            root.Children.Add(folderList);

            root.Children.Add(new TextBlock { Height = 6 });
            root.Children.Add(Label("Nom du modèle (le nom détermine le style : « bach », « vivaldi », « clavier », sinon mélodique) :"));
            root.Children.Add(nameBox);

            // order controls — two columns of (label + small number box)
            root.Children.Add(Label("Ordres des chaînes de Markov (mélodie & harmonie) — contexte plus long = plus de caractère, mais plus proche du corpus :"));
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < OrderKeys.Length; i++)
            {
                int rowsPerCol = (OrderKeys.Length + 1) / 2;
                int col = i / rowsPerCol, rowIdx = i % rowsPerCol;
                while (grid.RowDefinitions.Count <= rowIdx) grid.RowDefinitions.Add(new RowDefinition());
                var cellDock = new DockPanel { Margin = new Thickness(0, 1, 12, 1) };
                var box = new TextBox { Text = OrderDefaults[i].ToString(), Width = 34, TextAlignment = TextAlignment.Center };
                ThemeBox(box);
                orderBoxes[i] = box;
                DockPanel.SetDock(box, Dock.Right);
                cellDock.Children.Add(box);
                cellDock.Children.Add(new TextBlock { Text = OrderLabels[i], Foreground = Res("CommonForeground"), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
                Grid.SetColumn(cellDock, col); Grid.SetRow(cellDock, rowIdx);
                grid.Children.Add(cellDock);
            }
            root.Children.Add(grid);

            root.Children.Add(bar);
            root.Children.Add(status);

            var btnRow = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            DockPanel.SetDock(loadBtn, Dock.Left);
            btnRow.Children.Add(loadBtn);
            var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            rightBtns.Children.Add(cancelBtn);
            rightBtns.Children.Add(analyzeBtn);
            btnRow.Children.Add(rightBtns);
            root.Children.Add(btnRow);

            Content = new Border
            {
                Background = Res("CommonBackground"),
                BorderBrush = Res("OutlineColorBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = root,
            };

            addBtn.Click += (s, e) => PickFolder();
            loadBtn.Click += (s, e) => LoadExisting();
            cancelBtn.Click += (s, e) => { DialogResult = false; };
            analyzeBtn.Click += async (s, e) => await RunAnalyze();
        }

        static TextBlock Label(string t) => new TextBlock { Text = t, Foreground = Res("CommonForeground"), Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap, FontSize = 11 };

        void PickFolder()
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Choisir un dossier de partitions / MIDI à analyser";
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                {
                    if (!folders.Contains(dlg.SelectedPath)) { folders.Add(dlg.SelectedPath); RefreshFolders(); }
                    if (string.IsNullOrWhiteSpace(nameBox.Text)) nameBox.Text = new DirectoryInfo(dlg.SelectedPath).Name;
                }
            }
        }

        // Re-open an existing model: restore its source folders + orders + name so it can be re-analyzed.
        void LoadExisting()
        {
            using (var dlg = new System.Windows.Forms.OpenFileDialog())
            {
                dlg.Title = "Charger un modèle existant";
                if (Directory.Exists(ComposerV2Runtime.ModelsDir)) dlg.InitialDirectory = ComposerV2Runtime.ModelsDir;
                dlg.Filter = "Modèles V2 (*.json)|*.json";
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                try
                {
                    var model = ComposerV2Runtime.ReadFromPath(dlg.FileName);
                    folders.Clear();
                    if (model.SourceFolders != null) folders.AddRange(model.SourceFolders);
                    RefreshFolders();
                    nameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
                    for (int i = 0; i < OrderKeys.Length; i++)
                    {
                        int v;
                        orderBoxes[i].Text = (model.Orders != null && model.Orders.TryGetValue(OrderKeys[i], out v) ? v : OrderDefaults[i]).ToString();
                    }
                }
                catch (Exception ex) { MessageBox.Show(this, "Lecture du modèle : " + ex.Message, "Charger"); }
            }
        }

        // rebuild the folder list: one row per folder = "－" remove button (left) + path
        void RefreshFolders()
        {
            folderList.Items.Clear();
            foreach (var path in folders)
            {
                var row = new DockPanel();
                var del = new Button { Content = "－", Width = 22, Margin = new Thickness(0, 0, 6, 0), Tag = path, ToolTip = "Retirer ce dossier", Cursor = System.Windows.Input.Cursors.Hand };
                del.Click += (s, e) => { folders.Remove((string)((Button)s).Tag); RefreshFolders(); };
                DockPanel.SetDock(del, Dock.Left);
                row.Children.Add(del);
                row.Children.Add(new TextBlock { Text = path, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                folderList.Items.Add(row);
            }
        }

        // collect the chosen orders from the UI (MUST be called on the UI thread, before Task.Run)
        Dictionary<string, int> CollectOrders()
        {
            var d = new Dictionary<string, int>();
            for (int i = 0; i < OrderKeys.Length; i++)
            {
                int v;
                d[OrderKeys[i]] = (int.TryParse(orderBoxes[i].Text.Trim(), out v) && v >= 1 && v <= 12) ? v : OrderDefaults[i];
            }
            return d;
        }

        async Task RunAnalyze()
        {
            if (folders.Count == 0) { MessageBox.Show(this, "Choisis au moins un dossier.", "Nouveau modèle"); return; }
            string name = Sanitize(nameBox.Text);
            if (name.Length == 0) { MessageBox.Show(this, "Donne un nom au modèle.", "Nouveau modèle"); return; }

            Directory.CreateDirectory(ComposerV2Runtime.ModelsDir);
            string file = name + ".json";
            string json = Path.Combine(ComposerV2Runtime.ModelsDir, file);
            var dirs = folders.ToArray();
            var orders = CollectOrders();   // on the UI thread

            addBtn.IsEnabled = loadBtn.IsEnabled = analyzeBtn.IsEnabled = nameBox.IsEnabled = false;
            bar.Visibility = status.Visibility = Visibility.Visible;
            status.Text = "Préparation…";
            try
            {
                int analyzed = 0;
                await Task.Run(() =>
                {
                    var model = CorpusAnalyzerV2.AnalyzeManyWithProgress(dirs, json, "", (done, total, fname) =>
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            bar.Maximum = Math.Max(1, total);
                            bar.Value = done;
                            status.Text = total > 0 ? string.Format("{0}/{1}  {2}", done, total, fname) : "Finalisation…";
                        })), orders);
                    analyzed = model.FilesAnalyzed;
                });
                if (analyzed == 0)
                {
                    MessageBox.Show(this, "Aucun fichier exploitable trouvé dans les dossiers choisis.", "Nouveau modèle");
                    try { File.Delete(json); } catch { }
                    addBtn.IsEnabled = loadBtn.IsEnabled = analyzeBtn.IsEnabled = nameBox.IsEnabled = true;
                    bar.Visibility = status.Visibility = Visibility.Collapsed;
                    return;
                }
                ComposerV2Runtime.Invalidate(file);  // ensure a re-analyzed file is reloaded
                CreatedModelFile = file;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Erreur d'analyse : " + ex.Message, "Nouveau modèle");
                addBtn.IsEnabled = loadBtn.IsEnabled = analyzeBtn.IsEnabled = nameBox.IsEnabled = true;
                bar.Visibility = status.Visibility = Visibility.Collapsed;
            }
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
            return s.Trim();
        }
    }
}
