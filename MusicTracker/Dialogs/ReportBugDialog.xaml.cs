using System;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MusicTracker.Engine.BugReport;
using MusicTracker.Localization;
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

        /// <summary>The three issue types, in combo order. Also drives the title prefix and the GitHub labels.</summary>
        enum ReportKind { Bug = 0, Suggestion = 1, Exception = 2 }

        readonly TimelineScreen editor; // the active piece, or null when reporting from the home screen
        readonly Exception crash;       // non-null when opened by the crash guard
        bool sent;                      // true once the issue is filed (switches the buttons to OK / Ouvrir)
        string issueUrl;                // URL of the created issue, for "Ouvrir dans le navigateur"

        ReportKind Kind => (ReportKind)Math.Max(0, cboType.SelectedIndex);

        public ReportBugDialog(TimelineScreen activeEditor) : this(activeEditor, null, false) { }

        /// <summary>
        /// Crash-guard entry point: <paramref name="crashException"/> preselects the "Exception" type, prefills the
        /// title and shows the flattened exception. <paramref name="fatal"/> only changes the wording — a fatal crash
        /// means the CLR is tearing the process down whatever the user answers.
        /// </summary>
        public ReportBugDialog(TimelineScreen activeEditor, Exception crashException, bool fatal)
        {
            InitializeComponent();
            editor = activeEditor;
            crash = crashException;
            titleBar.MouseLeftButtonDown += (a, b) => { if (b.ButtonState == MouseButtonState.Pressed) DragMove(); };

            // No project open, or nothing to attach → disable the attach options.
            if (editor == null)
            {
                chkAttachProject.IsChecked = false;
                chkAttachProject.IsEnabled = false;
                chkAttachProject.Content = Loc.T("AttachTheCurrentProjectNoProject");
            }
            bool hasTemplate = editor != null && editor.FromTemplate;
            if (!hasTemplate)
            {
                chkAttachTemplate.IsChecked = false;
                chkAttachTemplate.IsEnabled = false;
                chkAttachTemplate.Content = Loc.T("AlsoAttachTheAssociatedModelNone");
            }

            if (crash != null) SetUpCrashMode(fatal);

            // Crash mode prefills the title, so the useful field is the description ("what were you doing").
            Loaded += (a, b) => { if (crash != null) txtDescription.Focus(); else txtTitle.Focus(); };
        }

        // Turn the report form into a crash report: type locked on "Exception", title derived from the exception,
        // and the flattened detail shown read-only so the user sees what leaves their machine.
        void SetUpCrashMode(bool fatal)
        {
            titleBar.Text = Loc.T("TheApplicationEncounteredAnError");
            txtIntro.Text = Loc.T("AnUnexpectedErrorOccurredYouCan")
                          + (fatal ? " " + Loc.T("TheApplicationWillClose") : "");

            cboType.SelectedIndex = (int)ReportKind.Exception;
            cboType.IsEnabled = false;

            txtTitle.Text = CrashTitle(crash);
            panException.Visibility = Visibility.Visible;
            txtException.Text = CrashGuard.Describe(crash);

            // "Annuler" is ambiguous here — the question being asked is whether to send.
            btnCancel.Content = Loc.T("DoNotSend");
        }

        // "InvalidOperationException : la collection a été modifiée" — the type name alone is too vague to triage,
        // the full message is often several lines. First line, capped.
        static string CrashTitle(Exception ex)
        {
            if (ex == null) return "";
            string msg = (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (msg.Length > 90) msg = msg.Substring(0, 90).TrimEnd() + "…";
            return ex.GetType().Name + (msg.Length > 0 ? " : " + msg : "");
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
                ShowStatus(Loc.T("AddAtLeastATitleOr"), error: true);
                return;
            }
            if (string.IsNullOrWhiteSpace(title))
                title = desc.Length > 60 ? desc.Substring(0, 60).TrimEnd() + "…" : desc;

            if (!BugReportConfig.IsConfigured)
            {
                ShowStatus(Loc.T("ThisBuildHasNoEmbeddedGitHub"), error: true);
                return;
            }

            // The issue title carries the type so it stands out when triaging; labels drive filtering. "Suggestion"
            // maps to GitHub's default "enhancement" label; a crash is a bug with an extra "crash" label so the
            // unhandled exceptions can be listed on their own.
            ReportKind kind = Kind;
            string issueTitle = $"[{KindTag(kind)}] {title}";
            string[] labels =
                kind == ReportKind.Suggestion ? new[] { "enhancement", "in-app" } :
                kind == ReportKind.Exception ? new[] { "bug", "crash", "in-app" } :
                                               new[] { "bug", "in-app" };

            SetBusy(true);
            ShowStatus(Loc.T("SendingTheReport"), error: false);
            try
            {
                string body = BuildBody(desc);
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                    issueUrl = await GitHubIssueClient.CreateIssueAsync(issueTitle, body, labels, cts.Token);

                // Success: confirm to the user (no GitHub details shown), lock the form, and switch the buttons
                // to "OK" (close) + "Ouvrir dans le navigateur" (open the issue).
                sent = true;
                ShowStatus(kind == ReportKind.Suggestion ? Loc.T("ThankYouYourSuggestionHasBeen")
                         : kind == ReportKind.Exception ? Loc.T("ThankYouTheErrorHasBeen")
                                                        : Loc.T("ThankYouTheProblemHasBeen"), error: false);
                cboType.IsEnabled = false;
                txtTitle.IsEnabled = false;
                txtDescription.IsEnabled = false;
                chkAttachProject.IsEnabled = false;
                chkAttachTemplate.IsEnabled = false;
                btnCancel.Content = Loc.T("OK");
                btnCancel.IsEnabled = true;
                btnSend.Content = Loc.T("OpenInTheBrowser");
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
            sb.AppendLine($"**Type :** {KindTag(Kind)}");
            sb.AppendLine();
            sb.AppendLine("### Description");
            sb.AppendLine(string.IsNullOrWhiteSpace(description) ? "_(aucune)_" : description);
            sb.AppendLine();

            // Pas dans un <details> replié, contrairement aux JSON : sur un plantage, la pile d'appels EST le
            // rapport, elle doit être visible dès l'ouverture de l'issue.
            if (crash != null)
            {
                sb.AppendLine("### Exception");
                sb.AppendLine("```");
                sb.AppendLine(CrashGuard.Describe(crash));
                sb.AppendLine("```");
                sb.AppendLine();
            }

            sb.AppendLine("### Environnement");
            sb.AppendLine($"- Version : {AppVersion()}");
            sb.AppendLine($"- OS : {Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")})");
            sb.AppendLine($"- .NET : {Environment.Version}");
            sb.AppendLine();

            // Sérialiser le projet peut échouer, et c'est PARTICULIÈREMENT vrai après un plantage : l'état qu'on
            // essaie de joindre est peut-être justement celui qui a cassé. Un rapport sans pièce jointe reste
            // utile ; un rapport qu'on n'arrive pas à envoyer, non.
            BugReportContext ctx = null;
            try { ctx = editor?.BuildBugReportContext(); }
            catch (Exception ex) { sb.AppendLine("_(contexte du projet indisponible : " + ex.Message + ")_"); sb.AppendLine(); }

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

        static string AppVersion() => CrashGuard.AppVersion();

        // Prefix used in the issue title and the body's "Type" line. Untranslated on purpose: issues are triaged
        // in one place, whatever UI language the reporter runs.
        static string KindTag(ReportKind kind)
            => kind == ReportKind.Suggestion ? "Suggestion"
             : kind == ReportKind.Exception ? "Exception"
                                            : "Bug";

        void SetBusy(bool busy)
        {
            btnSend.IsEnabled = !busy;
            btnCancel.IsEnabled = !busy;
            cboType.IsEnabled = !busy && crash == null;   // en mode plantage le type reste verrouillé
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
