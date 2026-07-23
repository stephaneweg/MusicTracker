using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using MusicTracker.Engine.AI;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// Small reusable "generate one element with the AI" dialog: pick a provider (collapsed once a key is present),
    /// type an INTENTION, and get back the model's raw JSON. The caller supplies the prompt (system + user, built
    /// from the intention + its own context) and parses/applies the <see cref="ResultJson"/>. Used by the per-module
    /// generators (drum groove, riff, melodic line). Shares provider/key/model handling via <see cref="AiProviders"/>.
    /// </summary>
    public partial class AiElementDialog : Window
    {
        /// <summary>The model's raw JSON reply — valid once the dialog closed with true.</summary>
        public string ResultJson { get; private set; }

        readonly Func<string, string[]> buildPrompt;   // intention -> [system, user]
        string currentProvider;
        bool ready;

        public AiElementDialog(string title, string context, Func<string, string[]> buildPrompt)
        {
            InitializeComponent();
            this.buildPrompt = buildPrompt;
            Title = title;
            txtContext.Text = context ?? "";

            foreach (var id in AiProviders.Ids) cboProvider.Items.Add(AiProviders.Label(id));
            currentProvider = AiProviders.Norm(AppSettings.Instance.AiProvider);
            cboProvider.SelectedIndex = AiProviders.IndexOf(currentProvider);
            LoadProviderFields(currentProvider);
            UpdateModelSummary();
            expModel.IsExpanded = !AiProviders.KeysFor(currentProvider).Any(); // no key yet → open so it's noticed
            ready = true;

            Loaded += (a, b) => txtIntention.Focus();
        }

        void LoadProviderFields(string p)
        {
            cboKey.Items.Clear();
            foreach (var k in AiProviders.KeysFor(p)) cboKey.Items.Add(k.Name);
            string sel = AiProviders.GetSelectedKeyName(p);
            if (sel != null && cboKey.Items.Contains(sel)) cboKey.SelectedItem = sel;
            else if (cboKey.Items.Count > 0) cboKey.SelectedIndex = 0;

            cboModel.Items.Clear();
            foreach (var m in AiProviders.ModelsFor(p)) cboModel.Items.Add(m);
            string model = AiProviders.GetModel(p);
            cboModel.Text = string.IsNullOrWhiteSpace(model) ? AiProviders.DefaultModel(p) : model;
        }

        void SaveProviderFields(string p)
        {
            if (cboKey.SelectedItem is string kn) AiProviders.SetSelectedKeyName(p, kn);
            AiProviders.SetModel(p, cboModel.Text?.Trim() ?? "");
        }

        void btnManageKeys_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ApiKeysDialog { Owner = this };
            if (dlg.ShowDialog() == true) LoadProviderFields(currentProvider);
        }

        void UpdateModelSummary()
        {
            if (txtModelSummary != null) txtModelSummary.Text = AiProviders.Label(currentProvider) + " · " + (cboModel.Text ?? "").Trim();
        }

        void cboProvider_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!ready) return;
            SaveProviderFields(currentProvider);
            currentProvider = AiProviders.ForIndex(cboProvider.SelectedIndex);
            LoadProviderFields(currentProvider);
            UpdateModelSummary();
        }

        async void btnGenerate_Click(object sender, RoutedEventArgs e)
        {
            string provider = AiProviders.ForIndex(cboProvider.SelectedIndex);
            string keyName = cboKey.SelectedItem as string;
            if (keyName != null) AiProviders.SetSelectedKeyName(provider, keyName);
            string key = AiProviders.KeyByName(provider, keyName)?.Trim() ?? "";
            string model = cboModel.Text?.Trim(); if (string.IsNullOrWhiteSpace(model)) model = AiProviders.DefaultModel(provider);
            if (string.IsNullOrWhiteSpace(key)) { Status("Aucune clé " + AiProviders.Label(provider) + ". Ajoute-en une via « Gérer… ».", true); expModel.IsExpanded = true; return; }

            SaveProviderFields(provider); AppSettings.Instance.AiProvider = provider; AppSettings.Instance.Save();

            string[] pr = buildPrompt(txtIntention.Text ?? "");
            SetBusy(true); Status("Génération en cours…", false);
            ResultJson = null; btnApply.IsEnabled = false; txtResult.Clear();
            try
            {
                string json = await AiProviders.CompleteJsonAsync(provider, key, model, pr[0], pr[1]);
                txtResult.Text = Pretty(json);
                ResultJson = json;
                btnApply.IsEnabled = true;
                Status("Réponse reçue. Vérifie le JSON (Avancé) puis clique Confirmer.", false);
            }
            catch (Exception ex) { Status("Échec : " + ex.Message, true); }
            finally { SetBusy(false); }
        }

        // "Copier le prompt" — no API call: build the prompt from the intention and put it on the clipboard.
        void btnCopyPrompt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string[] pr = buildPrompt(txtIntention.Text ?? "");
                Clipboard.SetText(pr[0] + "\n\n" + pr[1]);
                Status("Prompt copié. Colle-le dans un chat IA, puis reviens avec « Coller la réponse ».", false);
            }
            catch (Exception ex) { Status("Impossible de copier le prompt : " + ex.Message, true); }
        }

        // "Coller la réponse" — treat the clipboard JSON as the result and hand it to the caller to apply.
        void btnPasteResponse_Click(object sender, RoutedEventArgs e)
        {
            string clip;
            try { clip = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (Exception ex) { Status("Presse-papiers illisible : " + ex.Message, true); return; }
            if (string.IsNullOrWhiteSpace(clip)) { Status("Le presse-papiers est vide — copie d'abord la réponse JSON de l'IA.", true); return; }
            txtResult.Text = Pretty(clip);
            ResultJson = clip;
            btnApply.IsEnabled = true;
            Status("Réponse collée. Vérifie le JSON (Avancé) puis clique Confirmer.", false);
        }

        // "Confirmer" — hand the generated/pasted JSON to the caller to parse and apply.
        void btnApply_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ResultJson)) { Status("Génère ou colle d'abord une réponse.", true); return; }
            DialogResult = true;   // caller parses/applies ResultJson
        }

        void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        void SetBusy(bool b)
        {
            btnGenerate.IsEnabled = !b;
            if (progGen != null) progGen.Visibility = b ? Visibility.Visible : Visibility.Collapsed;
            Cursor = b ? Cursors.Wait : Cursors.Arrow;
        }

        void Status(string t, bool err)
        {
            txtStatus.Text = t;
            txtStatus.Foreground = err ? System.Windows.Media.Brushes.IndianRed : System.Windows.Media.Brushes.Gray;
        }

        static string Pretty(string json)
        {
            try { using (var d = System.Text.Json.JsonDocument.Parse(json)) return System.Text.Json.JsonSerializer.Serialize(d.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }); }
            catch { return json; }
        }
    }
}
