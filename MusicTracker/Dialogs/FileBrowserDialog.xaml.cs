using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicTracker.Localization;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// On-brand replacement for the native Open/Save file dialogs. The public surface mirrors
    /// <see cref="Microsoft.Win32.FileDialog"/> (<see cref="Filter"/>, <see cref="DefaultExt"/>,
    /// <see cref="FileName"/>, <see cref="InitialDirectory"/>, <see cref="Window.ShowDialog"/>) so call
    /// sites only swap the constructor. Set <see cref="SaveMode"/> for a save dialog.
    /// </summary>
    public partial class FileBrowserDialog : Window
    {
        const string RECENT = "::recent";   // sentinel "location" for the Récents list

        // --- Microsoft.Win32-compatible surface -------------------------------------------------
        /// <summary>WinForms/Win32-style filter, e.g. "MIDI (*.mid)|*.mid|Tous (*.*)|*.*".</summary>
        public string Filter { get; set; }
        /// <summary>Extension appended in save mode when the typed name has none (".wav" or "wav").</summary>
        public string DefaultExt { get; set; }
        /// <summary>In: an optional initial name/path. Out: the chosen full path once ShowDialog() returned true.</summary>
        public string FileName { get; set; }
        /// <summary>Optional starting folder.</summary>
        public string InitialDirectory { get; set; }
        /// <summary>True = "Enregistrer sous" (overwrite confirm + DefaultExt); false = "Ouvrir".</summary>
        public bool SaveMode { get; set; }

        // --- internals --------------------------------------------------------------------------
        class FilterSpec { public string Desc; public string[] Pats; public bool All; }
        // Name/Icon/Ext/Modified are properties (WPF data binding ignores fields). Modified est
        // stocké en DateTime pour le tri chronologique fiable ; ModifiedDisplay est la version
        // lisible pour la GridView.
        class FsEntry
        {
            public bool IsDir;
            public string FullPath;
            public string Name { get; set; }
            public string Icon { get; set; }
            public string Ext { get; set; }        // ".mp3", ".sq", "" pour dossiers
            public DateTime Modified { get; set; } // clé de tri
            public string ModifiedDisplay { get; set; }
            public long? Size { get; set; }
            public string SizeDisplay { get; set; }
        }

        readonly List<FilterSpec> filters = new List<FilterSpec>();
        readonly Stack<string> back = new Stack<string>();
        List<Regex> patterns = new List<Regex>();
        bool allMatch = true;
        string current;         // current location (a directory path, or RECENT)
        FsEntry selEntry;       // last selected file entry
        bool ready;

        static readonly HashSet<string> MusicExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".sq", ".mid", ".midi", ".mscz", ".mscx", ".wav", ".mp3", ".ogg", ".flac" };

        public FileBrowserDialog()
        {
            InitializeComponent();
            Loaded += FileBrowserDialog_Loaded;
        }

        void FileBrowserDialog_Loaded(object sender, RoutedEventArgs e)
        {
            // Honour a caller-supplied Title; otherwise use the default for the mode.
            string def = SaveMode ? Loc.T("EnregistrerSous") : Loc.T("OuvrirUnFichier");
            string t = (string.IsNullOrWhiteSpace(Title) || Title == Loc.T("OuvrirUnFichier")) ? def : Title;
            txtTitle.Text = t; Title = t;
            txtIcon.Text = SaveMode ? "💾" : "📂";
            btnAccept.Content = SaveMode ? Loc.T("Enregistrer") : Loc.T("Ouvrir");

            ParseFilter();
            cboFilter.ItemsSource = filters.Select(f => f.Desc).ToList();
            cboFilter.SelectedIndex = 0;   // fires SelectionChanged -> sets patterns (ready is false, no refresh yet)

            if (!string.IsNullOrEmpty(FileName)) txtFileName.Text = Path.GetFileName(FileName);

            string start = FirstExistingDir(
                InitialDirectory,
                SafeDir(FileName),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

            BuildPlaces();
            GoTo(start, record: false);
            ready = true;
            txtFileName.Focus();
        }

        // ---- filter parsing --------------------------------------------------------------------
        void ParseFilter()
        {
            filters.Clear();
            if (!string.IsNullOrWhiteSpace(Filter))
            {
                var parts = Filter.Split('|');
                for (int i = 0; i + 1 < parts.Length; i += 2)
                {
                    var pats = parts[i + 1].Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                    filters.Add(new FilterSpec { Desc = parts[i], Pats = pats, All = pats.Any(IsAllPattern) });
                }
            }
            if (filters.Count == 0)
                filters.Add(new FilterSpec { Desc = Loc.T("TousLesFichiers"), Pats = new[] { "*.*" }, All = true });
        }

        static bool IsAllPattern(string p) => p == "*.*" || p == "*";

        void UpdatePatterns()
        {
            int i = cboFilter.SelectedIndex;
            var spec = (i >= 0 && i < filters.Count) ? filters[i] : filters[0];
            allMatch = spec.All;
            patterns = spec.Pats.Select(WildcardToRegex).ToList();
        }

        static Regex WildcardToRegex(string pat)
            => new Regex("^" + Regex.Escape(pat).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);

        bool MatchFilter(string name) => allMatch || patterns.Any(r => r.IsMatch(name));

        /// <summary>L'extension à donner à un nom tapé sans extension, déduite du type sélectionné dans la liste :
        /// la première de ses extensions. Renvoie null quand le type n'en impose aucune — c'est alors au
        /// <see cref="DefaultExt"/> de trancher. Deux cas rendent null, et ils comptent :
        /// un type ATTRAPE-TOUT (« Tous les fichiers (*.*) », présent dans plusieurs sélecteurs d'enregistrement)
        /// ne veut rien dire comme extension, et le prendre au mot produirait un fichier nommé « truc.* » —
        /// impossible sous Windows ; un motif sans point (« * ») ou une entrée sans motif ne donnent rien non plus.</summary>
        string FilterExtension()
        {
            int i = cboFilter.SelectedIndex;
            if (i < 0 || i >= filters.Count) return null;
            var spec = filters[i];
            if (spec.All || spec.Pats == null || spec.Pats.Length == 0) return null;
            string ext = Path.GetExtension(spec.Pats[0]);
            if (string.IsNullOrEmpty(ext) || ext.IndexOfAny(new[] { '*', '?' }) >= 0) return null;
            return ext;
        }

        // ---- navigation ------------------------------------------------------------------------
        void GoTo(string loc, bool record = true)
        {
            if (string.IsNullOrEmpty(loc)) return;
            if (record && current != null && !string.Equals(current, loc, StringComparison.OrdinalIgnoreCase))
                back.Push(current);
            current = loc;
            selEntry = null;
            btnBack.IsEnabled = back.Count > 0;

            if (loc == RECENT) { txtPath.Text = Loc.T("Recents"); btnUp.IsEnabled = false; PopulateRecents(); }
            else { txtPath.Text = loc; btnUp.IsEnabled = SafeParent(loc) != null; PopulateDir(loc); }
        }

        void PopulateDir(string dir)
        {
            var items = new List<FsEntry>();
            try
            {
                var di = new DirectoryInfo(dir);
                foreach (var d in di.GetDirectories())
                {
                    if (IsHidden(d.Attributes)) continue;
                    DateTime wt = SafeLastWrite(() => d.LastWriteTime);
                    items.Add(new FsEntry
                    {
                        IsDir = true, Name = d.Name, FullPath = d.FullName, Icon = "📁", Ext = "",
                        Modified = wt, ModifiedDisplay = FormatDate(wt),
                        Size = null, SizeDisplay = "",
                    });
                }
                foreach (var f in di.GetFiles())
                {
                    if (IsHidden(f.Attributes) || !MatchFilter(f.Name)) continue;
                    DateTime wt = SafeLastWrite(() => f.LastWriteTime);
                    long sz = 0; try { sz = f.Length; } catch { }
                    items.Add(new FsEntry
                    {
                        IsDir = false, Name = f.Name, FullPath = f.FullName, Icon = IconFor(f.Name),
                        Ext = (Path.GetExtension(f.Name) ?? "").ToLowerInvariant(),
                        Modified = wt, ModifiedDisplay = FormatDate(wt),
                        Size = sz, SizeDisplay = SizeStr(sz),
                    });
                }
            }
            catch { /* inaccessible folder → empty */ }
            ShowEntries(items);
        }

        void PopulateRecents()
        {
            var items = new List<FsEntry>();
            foreach (var r in RecentFiles.Instance.Entries)
            {
                if (string.IsNullOrEmpty(r.Path)) continue;
                try { if (!File.Exists(r.Path)) continue; } catch { continue; }
                string name = Path.GetFileName(r.Path);
                if (!MatchFilter(name)) continue;
                DateTime wt = DateTime.MinValue;
                long sz = 0;
                try { var fi = new FileInfo(r.Path); wt = fi.LastWriteTime; sz = fi.Length; } catch { }
                items.Add(new FsEntry
                {
                    IsDir = false, Name = name, FullPath = r.Path, Icon = IconFor(name),
                    Ext = (Path.GetExtension(name) ?? "").ToLowerInvariant(),
                    Modified = wt, ModifiedDisplay = string.IsNullOrEmpty(r.Folder) ? FormatDate(wt) : r.Folder,
                    Size = sz, SizeDisplay = SizeStr(sz),
                });
            }
            ShowEntries(items);
        }

        static DateTime SafeLastWrite(Func<DateTime> get) { try { return get(); } catch { return DateTime.MinValue; } }
        static string FormatDate(DateTime dt) => dt == DateTime.MinValue ? "" : dt.ToString("yyyy-MM-dd HH:mm");

        // Tri courant de la GridView. IsDir desc garde toujours les dossiers en haut ; la 2e clé
        // est celle choisie par l'user au clic sur en-tête.
        string sortKey = "Name";
        System.ComponentModel.ListSortDirection sortDir = System.ComponentModel.ListSortDirection.Ascending;

        void ShowEntries(List<FsEntry> items)
        {
            ApplySort(items);
            listFiles.ItemsSource = items;
            txtEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Tri en place : dossiers toujours au-dessus, puis par la colonne courante (Name /
        /// Ext / Modified) dans la direction sortDir.</summary>
        void ApplySort(List<FsEntry> items)
        {
            int dirDelta(FsEntry a, FsEntry b) => (b.IsDir ? 1 : 0).CompareTo(a.IsDir ? 1 : 0);
            Comparison<FsEntry> cmp;
            switch (sortKey)
            {
                case "Ext":
                    cmp = (a, b) => { int d = dirDelta(a, b); if (d != 0) return d;
                        return string.Compare(a.Ext ?? "", b.Ext ?? "", StringComparison.OrdinalIgnoreCase); };
                    break;
                case "Modified":
                    cmp = (a, b) => { int d = dirDelta(a, b); if (d != 0) return d;
                        return a.Modified.CompareTo(b.Modified); };
                    break;
                default:
                    cmp = (a, b) => { int d = dirDelta(a, b); if (d != 0) return d;
                        return string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase); };
                    break;
            }
            items.Sort(cmp);
            if (sortDir == System.ComponentModel.ListSortDirection.Descending)
            {
                // On garde dossiers en haut malgré desc — donc on reverse chaque groupe séparément.
                int split = 0;
                for (int i = 0; i < items.Count && items[i].IsDir; i++) split = i + 1;
                var dirs = items.GetRange(0, split);
                var files = items.GetRange(split, items.Count - split);
                dirs.Reverse(); files.Reverse();
                items.Clear();
                items.AddRange(dirs); items.AddRange(files);
            }
        }

        /// <summary>Clic sur en-tête colonne : identifie la clé de tri via Header i18n (Nom /
        /// Extension / ModifieLe), toggle asc/desc si même colonne, sinon reset asc.</summary>
        void ListHeader_Click(object sender, RoutedEventArgs e)
        {
            var header = e.OriginalSource as System.Windows.Controls.GridViewColumnHeader;
            if (header?.Content == null) return;
            string content = header.Content.ToString();
            string newKey =
                content == Loc.T("Extension") ? "Ext" :
                content == Loc.T("ModifieLe") ? "Modified" :
                "Name";
            if (newKey == sortKey)
                sortDir = sortDir == System.ComponentModel.ListSortDirection.Ascending
                    ? System.ComponentModel.ListSortDirection.Descending
                    : System.ComponentModel.ListSortDirection.Ascending;
            else { sortKey = newKey; sortDir = System.ComponentModel.ListSortDirection.Ascending; }
            RefreshCurrent();
        }

        void RefreshCurrent() { if (current != null) GoTo(current, record: false); }

        // ---- places sidebar --------------------------------------------------------------------
        void BuildPlaces()
        {
            placesPanel.Children.Clear();
            AddPlace(Loc.T("Recents2"), RECENT);
            AddSeparator();
            AddKnown(Loc.T("Bureau"), Environment.SpecialFolder.DesktopDirectory);
            AddKnown(Loc.T("Documents"), Environment.SpecialFolder.MyDocuments);
            AddKnownPath(Loc.T("Téléchargements"), ResolveDownloadsFolder());
            AddKnown(Loc.T("Musique"), Environment.SpecialFolder.MyMusic);
            AddSeparator();
            foreach (var drv in SafeDrives())
            {
                AddPlace("💽  " + DriveLabel(drv), drv.RootDirectory.FullName);
            }
        }

        void AddKnown(string label, Environment.SpecialFolder f)
        {
            string p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) AddPlace(label, p);
        }

        /// <summary>Comme AddKnown mais avec un chemin déjà résolu (utile pour Downloads qui n'a
        /// pas de SpecialFolder en .NET Framework).</summary>
        void AddKnownPath(string label, string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) AddPlace(label, path);
        }

        /// <summary>Résout le dossier Téléchargements de l'user. Ordre : (1) registre
        /// User Shell Folders (source authoritative Windows, gère les Downloads déplacés vers
        /// D:\... par l'user), (2) fallback %USERPROFILE%\Downloads.</summary>
        static string ResolveDownloadsFolder()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                {
                    var v = k?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
                    if (!string.IsNullOrEmpty(v))
                    {
                        v = Environment.ExpandEnvironmentVariables(v);
                        if (Directory.Exists(v)) return v;
                    }
                }
            }
            catch { }
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(fallback) ? fallback : null;
        }

        void AddPlace(string label, string path)
        {
            var b = new Button { Content = label, Tag = path, Style = (Style)FindResource("PlaceBtn"), HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += (s, e) => GoTo((string)((Button)s).Tag);
            placesPanel.Children.Add(b);
        }

        void AddSeparator()
            => placesPanel.Children.Add(new Border { Height = 1, Margin = new Thickness(8, 6, 8, 6), Background = (System.Windows.Media.Brush)FindResource("SubtleBorderBrush") });

        // ---- accept / cancel -------------------------------------------------------------------
        void Accept()
        {
            string name = txtFileName.Text?.Trim();
            string full = null;

            if (selEntry != null && !selEntry.IsDir && string.Equals(selEntry.Name, name, StringComparison.OrdinalIgnoreCase))
                full = selEntry.FullPath;
            else if (!string.IsNullOrEmpty(name))
                full = Path.IsPathRooted(name) ? name : Path.Combine(CombineBase(), name);

            if (string.IsNullOrEmpty(full)) return;

            // Typing a folder name navigates into it instead of accepting.
            try { if (Directory.Exists(full)) { GoTo(full); txtFileName.Clear(); return; } } catch { }

            if (SaveMode)
            {
                // Un nom tapé sans extension prend celle du TYPE choisi dans la liste, et non le DefaultExt :
                // avec un sélecteur multi-formats (l'export), choisir « MP3 » puis taper « morceau » doit donner
                // morceau.mp3, pas morceau + l'extension par défaut du dialogue.
                string currentExt = FilterExtension() ?? DefaultExt;
                if (string.IsNullOrEmpty(Path.GetExtension(full)) && !string.IsNullOrEmpty(currentExt))
                    full += currentExt.StartsWith(".") ? currentExt : "." + currentExt;

                string dir = SafeDir(full);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                { MessageBox.Show(this, Loc.T("DossierIntrouvable"), Loc.T("Enregistrer"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }

                bool exists; try { exists = File.Exists(full); } catch { exists = false; }
                if (exists && MessageBox.Show(this, $"« {Path.GetFileName(full)} » existe déjà.\nLe remplacer ?", Loc.T("Enregistrer"),
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }
            else
            {
                bool exists; try { exists = File.Exists(full); } catch { exists = false; }
                if (!exists) { MessageBox.Show(this, Loc.T("FichierIntrouvable"), Loc.T("Ouvrir"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            }

            FileName = full;
            DialogResult = true;
        }

        string CombineBase() => (current == null || current == RECENT)
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) : current;

        // ---- event handlers --------------------------------------------------------------------
        void listFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            selEntry = listFiles.SelectedItem as FsEntry;
            if (selEntry != null && !selEntry.IsDir) txtFileName.Text = selEntry.Name;
        }

        void listFiles_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            var it = listFiles.SelectedItem as FsEntry;
            if (it == null) return;
            if (it.IsDir) GoTo(it.FullPath);
            else { selEntry = it; txtFileName.Text = it.Name; Accept(); }
        }

        void cboFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdatePatterns();
            if (ready) RefreshCurrent();
        }

        void btnUp_Click(object sender, RoutedEventArgs e)
        {
            if (current == null || current == RECENT) return;
            var parent = SafeParent(current);
            if (parent != null) GoTo(parent);
        }

        void btnBack_Click(object sender, RoutedEventArgs e)
        {
            if (back.Count == 0) return;
            GoTo(back.Pop(), record: false);
        }

        void txtPath_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            string p = txtPath.Text?.Trim();
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) GoTo(p);
            else txtPath.Text = current == RECENT ? Loc.T("Recents") : current;
        }

        void txtFileName_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Accept(); }

        void btnAccept_Click(object sender, RoutedEventArgs e) => Accept();
        void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
        void btnClose_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        // ---- helpers ---------------------------------------------------------------------------
        static bool IsHidden(FileAttributes a) => (a & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        static string IconFor(string name) => MusicExt.Contains(Path.GetExtension(name ?? "")) ? "🎵" : "📄";

        static string SizeStr(long bytes)
        {
            if (bytes < 1024) return bytes + " o";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#") + " Ko";
            return (kb / 1024.0).ToString("0.#") + " Mo";
        }

        static string SafeDate(Func<DateTime> get) { try { return get().ToString("dd/MM/yyyy"); } catch { return ""; } }
        static string SafeDir(string path) { try { return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path); } catch { return null; } }
        static string SafeParent(string dir) { try { return Directory.GetParent(dir)?.FullName; } catch { return null; } }

        static string FirstExistingDir(params string[] candidates)
        {
            foreach (var c in candidates)
                try { if (!string.IsNullOrEmpty(c) && Directory.Exists(c)) return c; } catch { }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyComputer) is string mc && Directory.Exists(mc)
                ? mc : Directory.GetCurrentDirectory();
        }

        static List<DriveInfo> SafeDrives()
        {
            DriveInfo[] all; try { all = DriveInfo.GetDrives(); } catch { return new List<DriveInfo>(); }
            return all.Where(d=>d.IsReady).ToList();
        }

        static string DriveLabel(DriveInfo d)
        {
            try { var v = d.VolumeLabel; return string.IsNullOrEmpty(v) ? d.Name.TrimEnd('\\') : v + " (" + d.Name.TrimEnd('\\') + ")"; }
            catch { return d.Name.TrimEnd('\\'); }
        }
    }
}
