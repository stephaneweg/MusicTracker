using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using MusicTracker.Engine.AI;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// "Composer avec l'IA (Mistral)" — collects a style + length, asks Mistral for a JSON arrangement
    /// (chords/measure, sections, articulations, melodic-line motifs), shows it, and hands the parsed
    /// <see cref="AiArrangement"/> back to the caller to lay out on the timeline.
    /// </summary>
    public partial class AiComposeDialog : Window
    {
        /// <summary>The parsed arrangement, valid once the user clicked "Appliquer".</summary>
        public AiArrangement Result { get; private set; }

        /// <summary>Riff mode: snap out-of-harmony notes to chord/scale tones.</summary>
        public bool FixNotes => chkFixNotes.IsChecked == true;

        /// <summary>Chords silent on the Accords track (empty custom motif — harmonic marker only); the AI voices the
        /// chord content freely in a dedicated "Accords" voice.</summary>
        public bool ChordVoice => chkChordVoice.IsChecked == true;

        /// <summary>When set, "develop this theme" mode: the theme (notes + chords) is prepended to the prompt and the
        /// caller applies the result in APPEND mode (after the existing content).</summary>
        public string ThemeContext { get; set; }

        string currentProvider = "mistral";
        bool ready;

        public AiComposeDialog()
        {
            InitializeComponent();
            var s = AppSettings.Instance;
            currentProvider = NormProvider(s.AiProvider);
            LoadProviderFields(currentProvider);
            cboProvider.SelectedIndex = IndexForProvider(currentProvider);
            // Restore the last-used inputs.
            txtStyle.Text = s.AiStyle ?? "";
            txtMeasures.Text = (s.AiMeasures > 0 ? s.AiMeasures : 32).ToString();
            txtIntention.Text = s.AiIntention ?? "";
            tglRiffMode.IsChecked = s.AiRiffMode;
            chkFixNotes.IsChecked = s.AiFixNotes;
            chkDrums.IsChecked = s.AiDrums;
            chkChordVoice.IsChecked = s.AiChordVoice;
            sldThinking.Value = Math.Max(-1, Math.Min(24576, s.AiThinkingBudget));
            UpdateThinkingLabel();
            UpdateModelSummary();
            expModel.IsExpanded = !AiProviders.KeysFor(currentProvider).Any(); // no key yet → open so it's noticed
            ready = true;

            Loaded += (a, b) =>
            {
                if (!string.IsNullOrWhiteSpace(ThemeContext)) this.Title = "Varier le thème avec l'IA";
                txtStyle.Focus();
            };
        }

        // Provider ↔ ComboBox index: 0 Mistral, 1 Gemini, 2 Groq, 3 DeepSeek, 4 Claude, 5 Grok, 6 Qwen.
        static string NormProvider(string p) => (p == "gemini" || p == "groq" || p == "deepseek" || p == "claude" || p == "grok" || p == "qwen") ? p : "mistral";
        static string ProviderForIndex(int i) => i == 1 ? "gemini" : i == 2 ? "groq" : i == 3 ? "deepseek" : i == 4 ? "claude" : i == 5 ? "grok" : i == 6 ? "qwen" : "mistral";
        static int IndexForProvider(string p) => p == "gemini" ? 1 : p == "groq" ? 2 : p == "deepseek" ? 3 : p == "claude" ? 4 : p == "grok" ? 5 : p == "qwen" ? 6 : 0;
        static string ProviderLabel(string p) => p == "gemini" ? "Gemini" : p == "groq" ? "Groq" : p == "deepseek" ? "DeepSeek" : p == "claude" ? "Claude" : p == "grok" ? "Grok" : p == "qwen" ? "Qwen" : "Mistral";
        static string DefaultModel(string p) => p == "gemini" ? "gemini-2.0-flash" : p == "groq" ? "llama-3.3-70b-versatile" : p == "deepseek" ? "deepseek-chat" : p == "claude" ? "claude-opus-4-8" : p == "grok" ? "grok-4" : p == "qwen" ? "qwen3.7-max" : "mistral-small-latest";

        // Suggested models per provider (the ComboBox is editable → a custom id can still be typed).
        static readonly string[] MistralModels = { "mistral-large-latest", "mistral-medium-latest", "mistral-small-latest","open-mistral-nemo", "magistral-small-latest", "magistral-medium-latest" };
        static readonly string[] GeminiModels = { "gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview", "gemini-2.5-flash", "gemini-2.0-flash-lite", "gemini-1.5-flash", "gemini-1.5-pro" };
        static readonly string[] GroqModels = { "llama-3.3-70b-versatile", "llama-3.1-8b-instant", "openai/gpt-oss-120b", "qwen/qwen3.6-27b", "gemma2-9b-it" };
        static readonly string[] DeepSeekModels = { "deepseek-chat", "deepseek-reasoner" };
        static readonly string[] ClaudeModels = { "claude-sonnet-5", "claude-opus-4-8", "claude-haiku-4-5", "claude-opus-4-7", "claude-fable-5" };
        static readonly string[] GrokModels = { "grok-4", "grok-3", "grok-3-mini", "grok-2-latest" };
        static readonly string[] QwenModels = { "qwen3.7-max", "qwen-max", "qwen-plus", "qwen-turbo" };
        static string[] ModelsFor(string p) => p == "gemini" ? GeminiModels : p == "groq" ? GroqModels : p == "deepseek" ? DeepSeekModels : p == "claude" ? ClaudeModels : p == "grok" ? GrokModels : p == "qwen" ? QwenModels : MistralModels;

        void LoadProviderFields(string p)
        {
            var s = AppSettings.Instance;
            cboKey.Items.Clear();
            foreach (var k in AiProviders.KeysFor(p)) cboKey.Items.Add(k.Name);
            string selName = AiProviders.GetSelectedKeyName(p);
            if (selName != null && cboKey.Items.Contains(selName)) cboKey.SelectedItem = selName;
            else if (cboKey.Items.Count > 0) cboKey.SelectedIndex = 0;
            cboModel.Items.Clear();
            foreach (var m in ModelsFor(p)) cboModel.Items.Add(m);
            string model = p == "gemini" ? s.GeminiModel : p == "groq" ? s.GroqModel : p == "deepseek" ? s.DeepSeekModel : p == "claude" ? s.ClaudeModel : p == "grok" ? s.GrokModel : p == "qwen" ? s.QwenModel : s.MistralModel;
            cboModel.Text = string.IsNullOrWhiteSpace(model) ? DefaultModel(p) : model;
        }

        void SaveProviderFields(string p)
        {
            var s = AppSettings.Instance;
            string model = cboModel.Text?.Trim() ?? "";
            if (cboKey.SelectedItem is string kn) AiProviders.SetSelectedKeyName(p, kn);   // keys managed via ApiKeysDialog
            if (p == "gemini") s.GeminiModel = model;
            else if (p == "groq") s.GroqModel = model;
            else if (p == "deepseek") s.DeepSeekModel = model;
            else if (p == "claude") s.ClaudeModel = model;
            else if (p == "grok") s.GrokModel = model;
            else if (p == "qwen") s.QwenModel = model;
            else s.MistralModel = model;
        }

        void btnManageKeys_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ApiKeysDialog { Owner = this };
            if (dlg.ShowDialog() == true) LoadProviderFields(currentProvider);
        }

        int ThinkingBudget => (int)Math.Round(sldThinking.Value);
        void UpdateThinkingLabel() { if (txtThinking != null) txtThinking.Text = ThinkingBudget < 0 ? "Auto" : ThinkingBudget.ToString(); }
        void sldThinking_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e) => UpdateThinkingLabel();

        void cboProvider_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!ready) return;
            SaveProviderFields(currentProvider);                       // keep the key/model of the provider we're leaving
            currentProvider = ProviderForIndex(cboProvider.SelectedIndex);
            LoadProviderFields(currentProvider);
            UpdateModelSummary();
        }

        void UpdateModelSummary()
        {
            if (txtModelSummary == null) return;
            txtModelSummary.Text = ProviderLabel(currentProvider) + " · " + (cboModel.Text ?? "").Trim();
        }

        async void btnGenerate_Click(object sender, RoutedEventArgs e)
        {
            string provider = ProviderForIndex(cboProvider.SelectedIndex);
            string keyName = cboKey.SelectedItem as string;
            if (keyName != null) AiProviders.SetSelectedKeyName(provider, keyName);
            string apiKey = AiProviders.KeyByName(provider, keyName)?.Trim() ?? "";
            string model = cboModel.Text?.Trim();
            if (string.IsNullOrWhiteSpace(model)) model = DefaultModel(provider);
            string style = txtStyle.Text ?? "";
            if (string.IsNullOrWhiteSpace(apiKey)) { Status($"Aucune clé {ProviderLabel(provider)}. Ajoute-en une via « Gérer… ».", true); expModel.IsExpanded = true; return; }
            if (!int.TryParse(txtMeasures.Text?.Trim(), out int measures) || measures < 1) measures = 32;

            // Persist the key/model for THIS provider + the chosen provider + the dialog inputs so all are remembered.
            var s = AppSettings.Instance;
            SaveProviderFields(provider); s.AiProvider = provider;
            s.AiStyle = style; s.AiMeasures = measures; s.AiIntention = txtIntention.Text ?? "";
            s.AiRiffMode = tglRiffMode.IsChecked == true; s.AiFixNotes = chkFixNotes.IsChecked == true;
            s.AiDrums = chkDrums.IsChecked == true;
            s.AiChordVoice = chkChordVoice.IsChecked == true;
            s.AiThinkingBudget = ThinkingBudget;
            s.Save();

            SetBusy(true);
            Status("Génération en cours…", false);
            Result = null; btnApply.IsEnabled = false; txtResult.Clear();
            try
            {
                bool riffMode = tglRiffMode.IsChecked == true;
                string sys = AiArrangementPrompt.SystemPrompt(riffMode, chkDrums.IsChecked == true, chkChordVoice.IsChecked == true), usr = AiArrangementPrompt.UserPrompt(style, measures, txtIntention.Text, ThemeContext);
                string json =
                    provider == "gemini" ? await GeminiClient.CompleteJsonAsync(apiKey, model, sys, usr, ThinkingBudget)
                    : provider == "groq" ? await GroqClient.CompleteJsonAsync(apiKey, model, sys, usr)
                    : provider == "deepseek" ? await DeepSeekClient.CompleteJsonAsync(apiKey, model, sys, usr)
                    : provider == "claude" ? await ClaudeClient.CompleteJsonAsync(apiKey, model, sys, usr)
                    : provider == "grok" ? await GrokClient.CompleteJsonAsync(apiKey, model, sys, usr)
                    : provider == "qwen" ? await QwenClient.CompleteJsonAsync(apiKey, model, sys, usr)
                    : await MistralClient.CompleteJsonAsync(apiKey, model, sys, usr);
                txtResult.Text = Pretty(json);
                var arr = AiArrangement.Parse(json);
                Result = arr;
                btnApply.IsEnabled = true;
                Status(Summary(arr), false);
            }
            catch (Exception ex)
            {
                Result = null; btnApply.IsEnabled = false;
                Status("Échec : " + ex.Message, true);
            }
            finally { SetBusy(false); }
        }

        // Build the full prompt (system + user) from the current dialog inputs, ready to paste into any AI chat.
        string BuildFullPrompt()
        {
            string style = txtStyle.Text ?? "";
            if (!int.TryParse(txtMeasures.Text?.Trim(), out int measures) || measures < 1) measures = 32;
            bool riffMode = tglRiffMode.IsChecked == true;
            string sys = AiArrangementPrompt.SystemPrompt(riffMode, chkDrums.IsChecked == true, chkChordVoice.IsChecked == true);
            string usr = AiArrangementPrompt.UserPrompt(style, measures, txtIntention.Text, ThemeContext);
            return sys + "\n\n" + usr;
        }

        // "Copier le prompt" — no API call: put the prompt on the clipboard so the user can paste it into a chat
        // (ChatGPT, Claude.ai, Gemini…) and bring the answer back with "Coller la réponse". Works without any API key.
        void btnCopyPrompt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Remember the dialog inputs (not the provider key) so a later "Coller la réponse" matches this prompt.
                var s = AppSettings.Instance;
                if (int.TryParse(txtMeasures.Text?.Trim(), out int m) && m > 0) s.AiMeasures = m;
                s.AiStyle = txtStyle.Text ?? ""; s.AiIntention = txtIntention.Text ?? "";
                s.AiRiffMode = tglRiffMode.IsChecked == true; s.AiDrums = chkDrums.IsChecked == true;
                s.AiChordVoice = chkChordVoice.IsChecked == true;
                s.Save();

                Clipboard.SetText(BuildFullPrompt());
                Status("Prompt copié. Colle-le dans un chat IA, puis reviens avec « Coller la réponse ».", false);
            }
            catch (Exception ex) { Status("Impossible de copier le prompt : " + ex.Message, true); }
        }

        // "Coller la réponse" — parse the JSON the user copied from a chat and treat it as a generated arrangement.
        void btnPasteResponse_Click(object sender, RoutedEventArgs e)
        {
            string clip;
            try { clip = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (Exception ex) { Status("Presse-papiers illisible : " + ex.Message, true); return; }
            if (string.IsNullOrWhiteSpace(clip)) { Status("Le presse-papiers est vide — copie d'abord la réponse JSON de l'IA.", true); return; }

            Result = null; btnApply.IsEnabled = false;
            try
            {
                var arr = AiArrangement.Parse(clip);
                txtResult.Text = Pretty(clip);
                Result = arr;
                btnApply.IsEnabled = true;
                Status(Summary(arr), false);
            }
            catch (Exception ex)
            {
                txtResult.Text = clip;
                Status("JSON collé invalide : " + ex.Message, true);
            }
        }

        void btnApply_Click(object sender, RoutedEventArgs e)
        {
            if (Result == null) { Status("Génère d'abord un morceau.", true); return; }
            DialogResult = true;
        }

        void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        void SetBusy(bool busy)
        {
            btnGenerate.IsEnabled = !busy;
            if (progGen != null) progGen.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Cursor = busy ? Cursors.Wait : Cursors.Arrow;
        }

        void Status(string text, bool error)
        {
            txtStatus.Text = text;
            txtStatus.Foreground = error ? System.Windows.Media.Brushes.IndianRed : System.Windows.Media.Brushes.Gray;
        }

        static string Summary(AiArrangement a)
        {
            int chords = a.chords?.Count ?? 0, secs = a.sections?.Count ?? 0, lines = a.melodicLines?.Count ?? 0;
            return $"OK — {secs} section(s), {chords} accord(s), {lines} ligne(s) mélodique(s).";
        }

        static string Pretty(string json)
        {
            try { using (var doc = JsonDocument.Parse(json)) return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }); }
            catch { return json; }
        }
    }
}
