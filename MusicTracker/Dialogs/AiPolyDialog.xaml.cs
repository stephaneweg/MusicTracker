using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using MusicTracker.Engine.AI;
using MusicTracker.Localization;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// « Composer avec l'IA (polyrythmique) » — même squelette que <see cref="AiComposeDialog"/> : sélection du
    /// fournisseur (Mistral/Gemini/Groq/DeepSeek/Claude/Grok/Qwen), clé nommée, modèle, style + durée en temps +
    /// intention. Deux voies : appeler l'API (bouton Générer) ou copier le prompt pour aller le coller ailleurs
    /// (« Copier prompt » / « Coller la réponse »). Le résultat parsé est exposé via <see cref="Result"/> et le
    /// shell l'ouvre dans un NOUVEL onglet.
    /// </summary>
    public partial class AiPolyDialog : Window
    {
        public AiPolyrhythm Result { get; private set; }

        string currentProvider = "mistral";
        bool ready;

        public AiPolyDialog()
        {
            InitializeComponent();
            var s = AppSettings.Instance;
            currentProvider = AiProviders.Norm(s.AiProvider);
            LoadProviderFields(currentProvider);
            cboProvider.SelectedIndex = AiProviders.IndexOf(currentProvider);

            txtStyle.Text = s.AiStyle ?? "";
            // Durée en TEMPS pour un poly : par défaut on garde le dernier « nombre de mesures » × 4 quand on n'a rien.
            txtDuration.Text = (s.AiPolyDurationBeats > 0 ? s.AiPolyDurationBeats : Math.Max(16, (s.AiMeasures > 0 ? s.AiMeasures : 8) * 4)).ToString();
            txtIntention.Text = s.AiIntention ?? "";

            UpdateModelSummary();
            expModel.IsExpanded = !AiProviders.KeysFor(currentProvider).Any();   // pas de clé → on ouvre pour que ça se voie
            ready = true;

            Loaded += (_, __) => txtStyle.Focus();
        }

        void LoadProviderFields(string p)
        {
            cboKey.Items.Clear();
            foreach (var k in AiProviders.KeysFor(p)) cboKey.Items.Add(k.Name);
            string selName = AiProviders.GetSelectedKeyName(p);
            if (selName != null && cboKey.Items.Contains(selName)) cboKey.SelectedItem = selName;
            else if (cboKey.Items.Count > 0) cboKey.SelectedIndex = 0;
            cboModel.Items.Clear();
            foreach (var m in AiProviders.ModelsFor(p)) cboModel.Items.Add(m);
            string model = AiProviders.GetModel(p);
            cboModel.Text = string.IsNullOrWhiteSpace(model) ? AiProviders.DefaultModel(p) : model;
        }

        void SaveProviderFields(string p)
        {
            if (cboKey.SelectedItem is string kn) AiProviders.SetSelectedKeyName(p, kn);
            string model = cboModel.Text?.Trim() ?? "";
            AiProviders.SetModel(p, model);
        }

        void btnManageKeys_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ApiKeysDialog { Owner = this };
            if (dlg.ShowDialog() == true) LoadProviderFields(currentProvider);
        }

        void cboProvider_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!ready) return;
            SaveProviderFields(currentProvider);
            currentProvider = AiProviders.ForIndex(cboProvider.SelectedIndex);
            LoadProviderFields(currentProvider);
            UpdateModelSummary();
        }

        void UpdateModelSummary()
        {
            if (txtModelSummary == null) return;
            txtModelSummary.Text = AiProviders.Label(currentProvider) + " · " + (cboModel.Text ?? "").Trim();
        }

        int DurationBeats
        {
            get { return int.TryParse(txtDuration.Text?.Trim(), out int v) && v > 0 ? v : 32; }
        }

        string BuildFullPrompt()
        {
            string sys = AiPolyrhythmPrompt.SystemPrompt();
            // On propose 4/4 par défaut — l'IA reste libre de nuancer, mais le placer force la mesure à 4/4.
            int measures = Math.Max(1, DurationBeats / 4);
            string usr = AiPolyrhythmPrompt.UserPrompt(txtStyle.Text, measures, txtIntention.Text, 4);
            return sys + "\n\n" + usr;
        }

        async void btnGenerate_Click(object sender, RoutedEventArgs e)
        {
            string provider = AiProviders.ForIndex(cboProvider.SelectedIndex);
            string keyName = cboKey.SelectedItem as string;
            if (keyName != null) AiProviders.SetSelectedKeyName(provider, keyName);
            string apiKey = AiProviders.KeyByName(provider, keyName)?.Trim() ?? "";
            string model = cboModel.Text?.Trim();
            if (string.IsNullOrWhiteSpace(model)) model = AiProviders.DefaultModel(provider);

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Status($"Aucune clé {AiProviders.Label(provider)}. Ajoute-en une via « Gérer… ».", true);
                expModel.IsExpanded = true;
                return;
            }

            // Persistance des inputs (comme AiComposeDialog).
            var s = AppSettings.Instance;
            SaveProviderFields(provider); s.AiProvider = provider;
            s.AiStyle = txtStyle.Text ?? "";
            s.AiIntention = txtIntention.Text ?? "";
            s.AiPolyDurationBeats = DurationBeats;
            s.Save();

            SetBusy(true);
            Status(Loc.T("Generating"), false);
            Result = null; btnApply.IsEnabled = false; txtResult.Clear();
            try
            {
                string sys = AiPolyrhythmPrompt.SystemPrompt();
                int measures = Math.Max(1, DurationBeats / 4);
                string usr = AiPolyrhythmPrompt.UserPrompt(txtStyle.Text, measures, txtIntention.Text, 4);
                string json = await AiProviders.CompleteJsonAsync(provider, apiKey, model, sys, usr);
                txtResult.Text = Pretty(json);
                Result = AiPolyrhythm.Parse(json);
                Result.durationBeats = DurationBeats;                              // on force la durée voulue par l'utilisateur
                btnApply.IsEnabled = true;
                Status(Summary(Result), false);
            }
            catch (Exception ex)
            {
                Result = null; btnApply.IsEnabled = false;
                Status(Loc.T("Failed") + ex.Message, true);
            }
            finally { SetBusy(false); }
        }

        void btnCopyPrompt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var s = AppSettings.Instance;
                s.AiStyle = txtStyle.Text ?? "";
                s.AiIntention = txtIntention.Text ?? "";
                s.AiPolyDurationBeats = DurationBeats;
                s.Save();

                Clipboard.SetText(BuildFullPrompt());
                Status(Loc.T("PromptCopiedPasteItIntoAn"), false);
            }
            catch (Exception ex) { Status(Loc.T("CouldNotCopyThePrompt") + ex.Message, true); }
        }

        void btnPasteResponse_Click(object sender, RoutedEventArgs e)
        {
            string clip;
            try { clip = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (Exception ex) { Status(Loc.T("ClipboardIsUnreadable") + ex.Message, true); return; }
            if (string.IsNullOrWhiteSpace(clip)) { Status(Loc.T("TheClipboardIsEmptyCopyThe"), true); return; }

            Result = null; btnApply.IsEnabled = false;
            try
            {
                Result = AiPolyrhythm.Parse(clip);
                Result.durationBeats = DurationBeats;
                txtResult.Text = Pretty(clip);
                btnApply.IsEnabled = true;
                Status(Summary(Result), false);
            }
            catch (Exception ex)
            {
                txtResult.Text = clip;
                Status(Loc.T("PastedJSONIsInvalid") + ex.Message, true);
            }
        }

        void btnApply_Click(object sender, RoutedEventArgs e)
        {
            if (Result == null) { Status(Loc.T("GenerateAPieceFirst"), true); return; }
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

        static string Summary(AiPolyrhythm a)
        {
            int nd = a?.drum?.layers?.Count ?? 0;
            int nm = a?.melodic?.layers?.Count ?? 0;
            return $"OK — {nd} calque(s) percussion, {nm} voix mélodique(s), {a?.durationBeats ?? 0} temps.";
        }
        static string Pretty(string json)
        {
            try { using (var doc = JsonDocument.Parse(json)) return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }); }
            catch { return json; }
        }
    }
}
