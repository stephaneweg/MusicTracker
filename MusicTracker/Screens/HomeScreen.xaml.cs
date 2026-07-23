using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MusicTracker.Screens
{
    /// <summary>Welcome screen: recent musics on the left, create/open actions on the right.</summary>
    public partial class HomeScreen : UserControl
    {
        public event Action NewSequencerRequested;
        public event Action OpenRequested;
        public event Action<RecentEntry> OpenRecentRequested;
        public event Action ComposeAiRequested;
        public event Action<string> TemplateSpecRequested;   // section-based template name (TemplateLibrary)

        public HomeScreen()
        {
            InitializeComponent();
            var entries = RecentFiles.Instance.Entries;
            listRecent.ItemsSource = entries;
            emptyState.Visibility = (entries == null || entries.Count == 0)
                ? Visibility.Visible : Visibility.Collapsed;

            bannerRiff.SizeChanged += (s, e) => BuildBanner();
            eqCanvas.SizeChanged += (s, e) => BuildEqualizer();
            BuildTemplateCards();
            BuildResources();
        }

        // ---- Resources widget: "Astuce du jour" (rotating tips) + "Nouveautés" (changelog) ----

        static readonly string[] Tips =
        {
            "Un modèle crée un morceau complet en un clic — ou génère-en un sur mesure avec « Ajouter avec l'IA ».",
            "Dans l'éditeur batterie, choisis un motif par catégorie, ou dessine le tien et enregistre-le pour le réutiliser partout.",
            "Stocke plusieurs clés API par fournisseur (Paramètres ▸ Clés API) et bascule de l'une à l'autre par leur nom.",
            "Une ligne mélodique ne fixe que le rythme : le moteur choisit les notes sur les accords en cours.",
            "Dans un motif rythmique, une durée négative = un silence — pour aérer et structurer les phrases.",
            "Le bouton ♫ d'une piste affiche sa partition ; M coupe la piste, S l'isole.",
            "Importe un MIDI ou un fichier MuseScore directement dans la timeline (Importer…).",
            "Les accords vont sur la piste « Accords » en bas ; insère une cadence pour une suite toute prête.",
            "Change un motif de batterie et les répétitions s'ajustent pour garder la même durée.",
        };

        // The "Nouveautés" list is downloaded from the repo's CHANGELOG.md (raw URL, key ChangelogUrl in App.config),
        // so the news can be updated by pushing that file instead of shipping a new build. This array is the OFFLINE
        // FALLBACK: it is shown immediately, then replaced if (and only if) the download succeeds and parses.
        const int NewsMaxItems = 5;

        static string ChangelogUrl
        {
            get
            {
                try { return System.Configuration.ConfigurationManager.AppSettings["ChangelogUrl"]; }
                catch { return null; } // a malformed App.config must never break the home screen
            }
        }

        // The downloaded changelog is cached next to the app and refreshed at most ONCE A DAY: opening the home
        // screen repeatedly (it is rebuilt on every visit) must not hammer GitHub. The file's timestamp IS the
        // expiry clock, so the cache also survives restarts — and keeps the news readable offline.
        static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(1);
        static string CachePath => AppPaths.Local("userdata\\changelog.md");

        // One hue per news line. WPF (.NET Framework) does NOT render colour emoji fonts: the glyph falls back to a
        // monochrome outline, which simply takes the Foreground — so tinting is the only way to get colour here, and
        // it is reliable whatever emoji the changelog uses. Saturated but not harsh, readable on the dark card.
        static readonly Brush[] NewsIconBrushes = MakeFrozen(
            Color.FromRgb(0x3B, 0xCE, 0xDA),   // teal (app accent)
            Color.FromRgb(0xF2, 0xB3, 0x3D),   // amber
            Color.FromRgb(0xFF, 0x7A, 0x6B),   // coral
            Color.FromRgb(0x7F, 0xD4, 0x8A),   // green
            Color.FromRgb(0xB4, 0x8C, 0xF2),   // violet
            Color.FromRgb(0x5A, 0xA9, 0xFF),   // blue
            Color.FromRgb(0xF5, 0x8B, 0xC0));  // pink

        static Brush[] MakeFrozen(params Color[] colors)
        {
            var b = new Brush[colors.Length];
            for (int i = 0; i < colors.Length; i++) { var s = new SolidColorBrush(colors[i]); s.Freeze(); b[i] = s; }
            return b;
        }

        /// <summary>Extracts "- &lt;icon&gt; text" entries from the changelog; everything else (headings, prose, blank
        /// lines) is ignored, so the file stays readable on GitHub. Returns an empty list when nothing matches.</summary>
        static System.Collections.Generic.List<(string icon, string text)> ParseChangelog(string md)
        {
            var list = new System.Collections.Generic.List<(string, string)>();
            if (string.IsNullOrWhiteSpace(md)) return list;
            foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length < 3 || (line[0] != '-' && line[0] != '*')) continue;
                line = line.Substring(1).Trim();
                if (line.Length == 0) continue;
                int sp = line.IndexOf(' ');
                if (sp <= 0) continue;                       // needs an icon AND a text
                string icon = line.Substring(0, sp).Trim();
                string text = line.Substring(sp + 1).Trim();
                if (icon.Length == 0 || text.Length == 0) continue;
                if (char.IsLetterOrDigit(icon[0])) continue; // a plain bulleted sentence, not an "<emoji> text" entry
                list.Add((icon, text));
            }
            return list;
        }

        static readonly (string icon, string text)[] News =
        {
            ("🎛️", "Modèles de projet : depuis un fichier, avec l'IA, ou à ajouter dans le dossier — avec suppression."),
            ("🥁", "Catalogue de motifs batterie (Standard, Afrique, Australie) + tes motifs enregistrés, réutilisables."),
            ("🔑", "Plusieurs clés API par fournisseur, choisies par nom dans les écrans de composition."),
            ("🎼", "Templates IA structurés (intro/thème/développement/outro), étendus à la longueur voulue."),
            ("🎨", "Interface sombre & teal, dialogues déplaçables, éditeurs enrichis."),
        };

        // The widget's LAYOUT lives in HomeScreen.xaml; only these two dynamic parts are driven from here.
        int tipIndex;

        void BuildResources()
        {
            tipIndex = Tips.Length > 0 ? DateTime.Now.DayOfYear % Tips.Length : 0; // start on today's tip
            ShowTip();
            FillNews(newsList, News);      // built-in list…
            LoadChangelogAsync(newsList);  // …then the repo's CHANGELOG.md; any failure leaves it untouched
        }

        void ShowTip() { if (Tips.Length > 0) txtTip.Text = Tips[tipIndex]; }

        void ShiftTip(int delta)
        {
            if (Tips.Length == 0) return;
            tipIndex = ((tipIndex + delta) % Tips.Length + Tips.Length) % Tips.Length;
            ShowTip();
        }

        private void btnTipPrev_Click(object sender, RoutedEventArgs e) => ShiftTip(-1);
        private void btnTipNext_Click(object sender, RoutedEventArgs e) => ShiftTip(+1);

        void FillNews(StackPanel host, System.Collections.Generic.IEnumerable<(string icon, string text)> items)
        {
            host.Children.Clear();
            int n = 0;
            foreach (var it in items)
            {
                if (n >= NewsMaxItems) break;
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
                var ic = new TextBlock
                {
                    Text = it.icon, FontSize = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top,
                    Foreground = NewsIconBrushes[n % NewsIconBrushes.Length], // one hue per line (see NewsIconBrushes)
                };
                DockPanel.SetDock(ic, Dock.Left);
                line.Children.Add(ic);
                line.Children.Add(new TextBlock { Text = it.text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("SecondaryForeground"), FontSize = 11.5 });
                host.Children.Add(line);
                n++;
            }
        }

        /// <summary>Fetch CHANGELOG.md from the repo and replace the news list with it. Deliberately silent and
        /// best-effort: no network, no config, an HTTP error or an unparsable file all just keep the built-in list —
        /// the home screen must never block or complain because GitHub is unreachable.</summary>
        async void LoadChangelogAsync(StackPanel host)
        {
            string url = ChangelogUrl;
            if (string.IsNullOrWhiteSpace(url)) return;

            // 1) Cached copy first: instant, and it is what keeps the news visible offline.
            string cache = CachePath;
            bool fresh = false;
            try
            {
                var fi = new System.IO.FileInfo(cache);
                if (fi.Exists)
                {
                    fresh = (DateTime.UtcNow - fi.LastWriteTimeUtc) < CacheLifetime;
                    var cached = ParseChangelog(System.IO.File.ReadAllText(cache));
                    if (cached.Count > 0) FillNews(host, cached);
                }
            }
            catch { fresh = false; } // unreadable cache → just refetch
            if (fresh) return;       // downloaded less than a day ago: nothing to do

            // 2) Refresh from GitHub. Best-effort and silent: offline, 404, timeout or an unparsable file all
            //    leave whatever is already displayed (cache, else the built-in list).
            try
            {
                // A fresh HttpClient per call, like AIClient: avoids a client left in a bad state after a failure.
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("MusicTracker");
                    string md = await http.GetStringAsync(url).ConfigureAwait(true);
                    var items = ParseChangelog(md);
                    if (items.Count == 0) return;                 // don't cache junk over a good copy
                    FillNews(host, items);                        // back on the UI thread (ConfigureAwait(true))
                    try
                    {
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cache));
                        System.IO.File.WriteAllText(cache, md);   // timestamp = the cache's expiry clock
                    }
                    catch { /* read-only install dir → just don't cache */ }
                }
            }
            catch { /* offline / 404 / timeout → keep what is displayed */ }
        }

        // (The Resources cards' shell, teal badge and ‹ › arrows now live in HomeScreen.xaml as styles.)

        // Faint equalizer strip along the bottom of the whole page: flat teal bars, deterministic heights
        // (two folded sines), rebuilt on resize to span the full width.
        void BuildEqualizer()
        {
            eqCanvas.Children.Clear();
            double w = eqCanvas.ActualWidth, h = eqCanvas.ActualHeight;
            if (w < 1 || h < 1) return;
            var fill = (Brush)FindResource("AccentBrush");
            const double barW = 3, pitch = 7;
            int n = (int)(w / pitch);
            for (int i = 0; i < n; i++)
            {
                double t = i * 0.28;
                double a = Math.Abs(Math.Sin(t) * 0.55 + Math.Sin(t * 0.37 + 1.3) * 0.45); // 0..1
                double bh = 4 + a * (h - 4);
                var bar = new Rectangle { Width = barW, Height = bh, Fill = fill, RadiusX = 1.5, RadiusY = 1.5 };
                Canvas.SetLeft(bar, i * pitch);
                Canvas.SetTop(bar, h - bh);
                eqCanvas.Children.Add(bar);
            }
        }

        // Build the "Modèles de projet" cards from the section-based TemplateLibrary (Data/templates/*.json).
        void BuildTemplateCards()
        {
            templatesPanel.Children.Clear();
            foreach (var spec in Engine.Timeline.TemplateLibrary.All)
            {
                string n = spec.Name, intention = spec.Intention;
                // "Régénérer le template" (right-click) only for AI-made templates — it re-runs the AI with the stored
                // intention and overwrites this template.
                Action onRegen = spec.IsAiGenerated ? (Action)(() => RegenerateTemplate(n, intention)) : null;
                AddTemplateCard(spec.Icon ?? "🎼", n, "Modèle — structure intro/thème/développement/outro.",
                                spec.Tags, "#1FB6C3", () => TemplateSpecRequested?.Invoke(n), () => DeleteTemplate(n), onRegen);
            }
        }

        void DeleteTemplate(string name)
        {
            if (MessageBox.Show("Supprimer le modèle « " + name + " » ?\nLe fichier .json sera effacé.", "Supprimer un modèle",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Engine.Timeline.TemplateLibrary.Delete(name);
            RefreshTemplateCards();
        }

        void AddTemplateCard(string icon, string name, string desc, string tags, string accentHex, Action onClick, Action onDelete = null, Action onRegenerate = null)
        {
            var card = new Button { Style = (Style)FindResource("TemplateCard"), ToolTip = desc };
            card.Click += (s, e) => onClick();

            // Right-click ▸ regenerate (AI templates only). A context menu, so it doesn't clutter the card face.
            if (onRegenerate != null)
            {
                var menu = new ContextMenu();
                var mi = new MenuItem { Header = "🎲 Régénérer le template (IA)…" };
                mi.Click += (s, e) => onRegenerate();
                menu.Items.Add(mi);
                card.ContextMenu = menu;
            }

            var body = new StackPanel();
            Color accent; try { accent = (Color)ColorConverter.ConvertFromString(accentHex ?? "#1FB6C3"); } catch { accent = Colors.Teal; }
            body.Children.Add(new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10),
                Child = new TextBlock { Text = icon, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            body.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("CommonForeground"), TextTrimming = TextTrimming.CharacterEllipsis });
            body.Children.Add(new TextBlock { Text = desc, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("SecondaryForeground"), Margin = new Thickness(0, 3, 0, 0) });
            body.Children.Add(new TextBlock { Text = tags, FontSize = 10, Foreground = (Brush)FindResource("AccentBrightBrush"), Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            card.Content = body;

            if (onDelete == null) { templatesPanel.Children.Add(card); return; }

            // Wrap the card so a small ✕ (delete) can overlay its top-right corner without triggering the card click.
            var wrap = new Grid { Margin = card.Margin };
            card.Margin = new Thickness(0);
            var del = new Button
            {
                Content = "✕", Style = (Style)FindResource("deleteIconButton"),
                Width = 20, Height = 20, FontSize = 11, Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                // Inset past the card's 1px border AND its 10px corner radius, so the ✕ sits clearly INSIDE the card
                // instead of straddling the rounded corner.
                Margin = new Thickness(0, 9, 9, 0), ToolTip = "Supprimer ce modèle",
            };
            del.Click += (s, e) => onDelete();
            wrap.Children.Add(card);
            wrap.Children.Add(del);
            templatesPanel.Children.Add(wrap);
        }

        // ---- hero backdrop: a riff piano-roll crossfading into a drum grid (both faint, behind the content) ----

        // Family colours for the decorative drum cells (evoke the drum editor's GarageBand-style palette).
        static readonly Color[] DrumFamily =
        {
            Color.FromRgb(0xE8, 0x89, 0x4A), // kick   — orange
            Color.FromRgb(0xE4, 0x57, 0x4C), // snare  — red
            Color.FromRgb(0xE6, 0xC3, 0x4A), // hats   — yellow
            Color.FromRgb(0x5F, 0xBF, 0x6F), // toms   — green
            Color.FromRgb(0x7E, 0x6B, 0xD6), // perc   — purple
            Color.FromRgb(0x4A, 0xA8, 0xE8), // cymbal — blue
        };

        void BuildBanner()
        {
            double w = bannerRiff.ActualWidth, h = bannerRiff.ActualHeight;
            if (w < 2 || h < 2) return;
            BuildRiffGrid(w, h);
            BuildDrumGrid(w, h);
        }

        // Left half: same grey step-grid as the drum side, with teal note rectangles on top spanning a few
        // cells each (their "length"), tracing a gentle melodic contour — a piano-roll over the grid.
        void BuildRiffGrid(double w, double h)
        {
            bannerRiff.Children.Clear();
            var teal = (Brush)FindResource("AccentBrush");
            var empty = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            const int rows = 11;
            const double pad = 2, cw = 20, pitch = 27;
            double cellH = (h - 2 * pad) / rows;
            int endCol = (int)(w * 0.85 / pitch) + 1;   // mask hides the right; don't draw past the fade

            // grey grid cells
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < endCol; c++)
                {
                    var cell = new Rectangle { Width = cw, Height = cellH - 5, RadiusX = 3, RadiusY = 3, Fill = empty };
                    Canvas.SetLeft(cell, c * pitch); Canvas.SetTop(cell, pad + r * cellH + 2);
                    bannerRiff.Children.Add(cell);
                }

            // teal notes on top, each spanning 1–3 cells
            int col = 0;
            for (int i = 0; col < endCol; i++)
            {
                double a = Math.Sin(i * 0.62) * 0.5 + Math.Sin(i * 0.27 + 0.8) * 0.5; // -1..1
                int row = (int)Math.Round((rows - 1) / 2.0 - a * ((rows - 1) / 2.0 - 1));
                row = Math.Max(0, Math.Min(rows - 1, row));
                int len = 1 + (i % 3);
                var note = new Rectangle { Width = len * pitch - (pitch - cw), Height = cellH - 5, RadiusX = 3, RadiusY = 3, Fill = teal };
                Canvas.SetLeft(note, col * pitch); Canvas.SetTop(note, pad + row * cellH + 2);
                bannerRiff.Children.Add(note);
                col += len + (i % 2);   // advance, with an occasional gap
            }
        }

        // Right half: a step grid coloured by percussion family, like the drum editor. Only the visible (right)
        // portion is drawn since the opacity mask hides the left; empty cells are barely-there outlines.
        void BuildDrumGrid(double w, double h)
        {
            bannerDrum.Children.Clear();
            const int rows = 6;
            double pad = 2;
            double cellH = (h - 2 * pad) / rows;
            const double cw = 20, pitch = 27;
            int cols = (int)(w / pitch) + 1;
            int startCol = (int)(w * 0.40 / pitch);
            var empty = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

            for (int r = 0; r < rows; r++)
                for (int c = startCol; c < cols; c++)
                {
                    bool lit = DrumLit(r, c);
                    var cell = new Rectangle
                    {
                        Width = cw,
                        Height = cellH - 5,
                        RadiusX = 3,
                        RadiusY = 3,
                        Fill = lit ? new SolidColorBrush(DrumFamily[r]) : empty,
                    };
                    Canvas.SetLeft(cell, c * pitch);
                    Canvas.SetTop(cell, pad + r * cellH + 2);
                    bannerDrum.Children.Add(cell);
                }
        }

        static bool DrumLit(int row, int col)
        {
            switch (row)
            {
                case 0: return col % 4 == 0;    // kick
                case 1: return col % 8 == 4;    // snare (backbeat)
                case 2: return col % 2 == 0;    // hats (eighths)
                case 3: return col % 16 == 10;  // tom fill
                case 4: return col % 16 == 7;   // perc accent
                case 5: return col % 16 == 0;   // crash
                default: return false;
            }
        }

        private void BtnNewSequencer_Click(object sender, System.Windows.RoutedEventArgs e) => NewSequencerRequested?.Invoke();
        private void BtnComposeAi_Click(object sender, System.Windows.RoutedEventArgs e) => ComposeAiRequested?.Invoke();
        private void BtnOpen_Click(object sender, System.Windows.RoutedEventArgs e) => OpenRequested?.Invoke();

        void RefreshTemplateCards() { Engine.Timeline.TemplateLibrary.Reload(); BuildTemplateCards(); }

        static readonly System.Text.Json.JsonSerializerOptions TemplateJsonOpts =
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };

        // "Ajouter depuis un fichier…" — pick a .json template, validate it, copy it into Data/templates, refresh.
        void btnAddFromFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Dialogs.FileBrowserDialog { Owner = Window.GetWindow(this), Title = "Ajouter un template", Filter = "Template (*.json)|*.json|Tous les fichiers (*.*)|*.*" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string json = System.IO.File.ReadAllText(dlg.FileName);
                var spec = System.Text.Json.JsonSerializer.Deserialize<Engine.Timeline.TemplateSpec>(json, TemplateJsonOpts);
                if (spec == null || string.IsNullOrWhiteSpace(spec.Name)) throw new Exception("Template invalide (« name » manquant).");
                Engine.Timeline.TemplateLibrary.Save(json, spec.Name);
                RefreshTemplateCards();
            }
            catch (Exception ex) { MessageBox.Show("Impossible d'ajouter ce template : " + ex.Message, "Ajouter un template", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        // "Ajouter avec l'IA…" — style + intention → the template prompt → provider call → validate → save (new template).
        void btnAddWithAi_Click(object sender, RoutedEventArgs e) => GenerateTemplateWithAi(null, null);

        // Right-click ▸ "Régénérer le template" — reuse the stored intention (editable) and OVERWRITE the same template.
        void RegenerateTemplate(string name, string intention)
            => GenerateTemplateWithAi(intention, name);

        // Shared AI-template flow. `initialIntention` pre-fills the dialog; `forceName` (non-null) overwrites that
        // existing template (keeps its file) instead of creating a new one. The user's intention is stamped onto the
        // saved template (IsAiGenerated=true) so it can be regenerated later.
        void GenerateTemplateWithAi(string initialIntention, string forceName)
        {
            string title = forceName != null ? "Régénérer le template — IA" : "Générer un template — IA";
            var dlg = new Dialogs.AiElementDialog(title,
                "Décris un STYLE et une intention (ex. « valse mélancolique de Chopin »). L'IA renvoie un template ; vérifie puis Confirme pour l'enregistrer.",
                desc => Engine.Timeline.TemplatePrompt.Build(desc), initialIntention) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ResultJson)) return;
            try
            {
                string json = Engine.AI.AiArrangement.CleanJson(dlg.ResultJson);
                var spec = System.Text.Json.JsonSerializer.Deserialize<Engine.Timeline.TemplateSpec>(json, TemplateJsonOpts);
                if (spec == null || string.IsNullOrWhiteSpace(spec.Name)) throw new Exception("Réponse sans template valide.");
                // Remember it was AI-made + the intention (so it can be regenerated), and keep the original file when
                // regenerating (force the name → same slug → TemplateLibrary.Save overwrites it).
                spec.IsAiGenerated = true;
                spec.Intention = dlg.Intention;
                if (!string.IsNullOrWhiteSpace(forceName)) spec.Name = forceName;
                Engine.Timeline.TemplateLibrary.Save(System.Text.Json.JsonSerializer.Serialize(spec, TemplateSaveOpts), spec.Name);
                RefreshTemplateCards();
            }
            catch (Exception ex) { MessageBox.Show("Template IA invalide : " + ex.Message, "Template IA", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        static readonly System.Text.Json.JsonSerializerOptions TemplateSaveOpts = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep emojis/accents readable
        };

        private void ListRecent_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (listRecent.SelectedItem is RecentEntry entry)
                OpenRecentRequested?.Invoke(entry);
        }
    }
}
