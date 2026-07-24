using System;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MusicTracker.Engine.BugReport;
using MusicTracker.Screens;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// "Signaler un bug" — collects a title + description, optionally attaches the current project (and its source
    /// template) as collapsed JSON, and files a GitHub issue via <see cref="GitHubIssueClient"/>. The GitHub token is
    /// injected at build time (see <see cref="BugReportConfig"/>); no token is stored in the repository.
    /// </summary>
    public partial class ReportBugDialog : Window
    {
        // GitHub rejects issue bodies over 65536 chars; keep a margin for the surrounding markdown.
        const int MaxBodyChars = 60000;

        readonly TimelineScreen editor; // the active piece, or null when reporting from the home screen
        bool sent;                      // true once the issue is filed (switches the buttons to OK / Ouvrir)
        string issueUrl;                // URL of the created issue, for "Ouvrir dans le navigateur"

        /// <summary>True when the user picked "Suggestion" rather than "Bug" in the type combo.</summary>
        bool IsSuggestion => cboType.SelectedIndex == 1;

        public ReportBugDialog(TimelineScreen activeEditor)
        {
            InitializeComponent();
            editor = activeEditor;
            titleBar.MouseLeftButtonDown += (a, b) => { if (b.ButtonState == MouseButtonState.Pressed) DragMove(); };

            // No project open, or nothing to attach → disable the attach options.
            if (editor == null)
            {
                chkAttachProject.IsChecked = false;
                chkAttachProject.IsEnabled = false;
                chkAttachProject.Content = "Joindre le projet en cours (aucun projet ouvert)";
            }
            bool hasTemplate = editor != null && editor.FromTemplate;
            if (!hasTemplate)
            {
                chkAttachTemplate.IsChecked = false;
                chkAttachTemplate.IsEnabled = false;
                chkAttachTemplate.Content = "Joindre aussi le modèle associé (aucun)";
            }

            Loaded += (a, b) => txtTitle.Focus();
        }

        // "Annuler" before sending → just close; "OK" after sending → close with success.
        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = sent;
            Close();
        }

        private async void btnSend_Click(object sender, RoutedEventArgs e)
        {
            // After a successful send this button becomes "Ouvrir dans le navigateur".
            if (sent)
            {
                if (!string.IsNullOrWhiteSpace(issueUrl))
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(issueUrl) { UseShellExecute = true }); }
                    catch { /* opening the browser is best-effort */ }
                return;
            }

            string title = (txtTitle.Text ?? "").Trim();
            string desc = (txtDescription.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(desc))
            {
                ShowStatus("Ajoute au moins un titre ou une description.", error: true);
                return;
            }
            if (string.IsNullOrWhiteSpace(title))
                title = desc.Length > 60 ? desc.Substring(0, 60).TrimEnd() + "…" : desc;

            if (!BugReportConfig.IsConfigured)
            {
                ShowStatus("Cette version n'a pas de jeton GitHub intégré : impossible d'envoyer le rapport.", error: true);
                return;
            }

            // The issue title carries the type so it stands out when triaging; labels drive filtering. "Suggestion"
            // maps to GitHub's default "enhancement" label.
            bool suggestion = IsSuggestion;
            string issueTitle = $"[{(suggestion ? "Suggestion" : "Bug")}] {title}";
            string[] labels = suggestion ? new[] { "enhancement", "in-app" } : new[] { "bug", "in-app" };

            SetBusy(true);
            ShowStatus("Envoi du rapport…", error: false);
            try
            {
                string body = BuildBody(desc);
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                    issueUrl = await GitHubIssueClient.CreateIssueAsync(issueTitle, body, labels, cts.Token);

                // Success: confirm to the user (no GitHub details shown), lock the form, and switch the buttons
                // to "OK" (close) + "Ouvrir dans le navigateur" (open the issue).
                sent = true;
                ShowStatus(suggestion
                    ? "✓ Merci ! Ta suggestion a été transmise à l'équipe de développement."
                    : "✓ Merci ! Le problème a été remonté à l'équipe de développement.", error: false);
                cboType.IsEnabled = false;
                txtTitle.IsEnabled = false;
                txtDescription.IsEnabled = false;
                chkAttachProject.IsEnabled = false;
                chkAttachTemplate.IsEnabled = false;
                btnCancel.Content = "OK";
                btnCancel.IsEnabled = true;
                btnSend.Content = "Ouvrir dans le navigateur";
                btnSend.IsEnabled = true;
                btnSend.Visibility = string.IsNullOrWhiteSpace(issueUrl) ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message, error: true);
                SetBusy(false);
            }
        }

        // Assemble the markdown issue body: environment + summary, the user's description, then the project / template
        // JSON in collapsed <details> blocks (truncated to stay under GitHub's body-size limit).
        string BuildBody(string description)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"**Type :** {(IsSuggestion ? "Suggestion" : "Bug")}");
            sb.AppendLine();
            sb.AppendLine("### Description");
            sb.AppendLine(string.IsNullOrWhiteSpace(description) ? "_(aucune)_" : description);
            sb.AppendLine();

            sb.AppendLine("### Environnement");
            sb.AppendLine($"- Version : {AppVersion()}");
            sb.AppendLine($"- OS : {Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")})");
            sb.AppendLine($"- .NET : {Environment.Version}");
            sb.AppendLine();

            var ctx = editor?.BuildBugReportContext();
            if (ctx != null)
            {
                sb.AppendLine("### Projet");
                sb.AppendLine(ctx.Summary);
                sb.AppendLine();
            }

            // Attachments last, so truncation eats the (regenerable) JSON rather than the human text.
            if (chkAttachProject.IsChecked == true && ctx != null)
                AppendDetails(sb, "Projet (JSON .sq)", "json", ctx.ProjectJson);

            if (chkAttachTemplate.IsChecked == true && ctx != null && !string.IsNullOrWhiteSpace(ctx.TemplateJson))
                AppendDetails(sb, "Modèle associé (JSON)", "json", ctx.TemplateJson);

            string body = sb.ToString();
            if (body.Length > MaxBodyChars)
                body = body.Substring(0, MaxBodyChars) + "\n\n_…rapport tronqué (limite de taille GitHub atteinte)._";
            return body;
        }

        static void AppendDetails(StringBuilder sb, string summary, string lang, string content)
        {
            sb.AppendLine($"<details><summary>{summary}</summary>");
            sb.AppendLine();
            sb.AppendLine("```" + lang);
            sb.AppendLine(content);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        static string AppVersion()
        {
            try { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?"; }
            catch { return "?"; }
        }

        void SetBusy(bool busy)
        {
            btnSend.IsEnabled = !busy;
            btnCancel.IsEnabled = !busy;
            cboType.IsEnabled = !busy;
            txtTitle.IsEnabled = !busy;
            txtDescription.IsEnabled = !busy;
            chkAttachProject.IsEnabled = !busy && editor != null;
            chkAttachTemplate.IsEnabled = !busy && editor != null && editor.FromTemplate;
        }

        void ShowStatus(string message, bool error)
        {
            txtStatus.Text = message;
            txtStatus.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x7A))
                                          : (Brush)FindResource("CommonForeground");
            txtStatus.Visibility = Visibility.Visible;
        }
    }
}
