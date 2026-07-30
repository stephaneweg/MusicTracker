using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;

namespace MusicTracker.Engine.BugReport
{
    /// <summary>
    /// Filet de sécurité global. Toute exception non gérée est d'abord CONSIGNÉE dans
    /// <c>%AppData%\MusicTracker\crash.log</c>, puis proposée à l'utilisateur sous forme d'issue GitHub de
    /// type « Exception » (<see cref="Dialogs.ReportBugDialog"/>). Le journal passe en premier : si le
    /// dialogue lui-même échoue, la trace est déjà sur le disque.
    ///
    /// Trois points d'entrée, parce qu'ils n'ont pas les mêmes conséquences :
    /// <list type="bullet">
    /// <item>Dispatcher — casse sur le thread UI. On marque Handled, donc l'application SURVIT ; c'est le
    /// cas courant (un clic, un rendu) et fermer serait plus destructeur que continuer.</item>
    /// <item>AppDomain — casse ailleurs (thread audio, tâche de fond). Le CLR va tuer le processus quoi
    /// qu'on fasse : on ne peut que consigner et montrer le dialogue avant la fin.</item>
    /// <item>TaskScheduler — Task dont personne n'a lu le résultat. Déclenché par le GC, donc LONGTEMPS
    /// après le fait : on consigne SANS dialogue, sinon l'utilisateur voit surgir une erreur sans rapport
    /// avec ce qu'il est en train de faire.</item>
    /// </list>
    /// </summary>
    public static class CrashGuard
    {
        // Une exception levée pendant le traitement d'un crash ne doit pas rouvrir un dialogue : sans ce
        // garde, un plantage dans le dialogue lui-même boucle jusqu'à l'épuisement de la pile.
        static int prompting;

        // WPF redélivre la MÊME instance d'exception au gestionnaire quand celui-ci a ouvert une fenêtre
        // modale pendant son traitement — ce qui est exactement notre cas, le dialogue de rapport. Constaté :
        // deux entrées de journal et deux dialogues pour un seul plantage. On filtre donc par référence, dans
        // une fenêtre de temps recomptée à la FERMETURE du dialogue (l'utilisateur peut le laisser ouvert).
        static readonly object dedupLock = new object();
        static Exception lastHandled;
        static DateTime lastHandledUtc;
        static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(30);

        // Journal borné : au-delà, on repart de zéro. Garder le récent vaut mieux qu'un fichier qui gonfle
        // sans fin dans le profil de l'utilisateur.
        const long MaxLogBytes = 512 * 1024;

        /// <summary>Chemin du journal des plantages (créé à la demande).</summary>
        public static string LogPath => AppPaths.Roaming("crash.log");

        /// <summary>Branche les trois gestionnaires. À appeler une seule fois, au démarrage.</summary>
        public static void Install()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Handle(e.ExceptionObject as Exception, "AppDomain", fatal: true);

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                // SetObserved évite que le GC escalade en arrêt du processus.
                e.SetObserved();
                Log(e.Exception, "TaskScheduler (non observée)");
            };

            var app = Application.Current;
            if (app != null)
                app.DispatcherUnhandledException += (s, e) =>
                {
                    // Handled AVANT toute UI : si le dialogue casse à son tour, l'exception d'origine ne
                    // doit pas repartir en cascade.
                    e.Handled = true;
                    Handle(e.Exception, "Dispatcher", fatal: false);
                };
        }

        static void Handle(Exception ex, string source, bool fatal)
        {
            if (ex == null) return;
            if (AlreadySeen(ex)) return;
            Log(ex, source);                                            // le journal, toujours
            if (Interlocked.Exchange(ref prompting, 1) == 1) return;    // un dialogue est déjà ouvert
            try { Prompt(ex, fatal); }
            catch { /* dernier recours : ne jamais relancer depuis le filet de sécurité */ }
            finally
            {
                Interlocked.Exchange(ref prompting, 0);
                MarkSeen(ex);   // la fenêtre de dédoublonnage repart de la fermeture du dialogue
            }
        }

        static bool AlreadySeen(Exception ex)
        {
            lock (dedupLock)
            {
                if (ReferenceEquals(ex, lastHandled) && DateTime.UtcNow - lastHandledUtc < DedupWindow)
                    return true;
                lastHandled = ex;
                lastHandledUtc = DateTime.UtcNow;
                return false;
            }
        }

        static void MarkSeen(Exception ex)
        {
            lock (dedupLock) { lastHandled = ex; lastHandledUtc = DateTime.UtcNow; }
        }

        static void Prompt(Exception ex, bool fatal)
        {
            var app = Application.Current;
            var disp = app?.Dispatcher;
            if (disp == null || disp.HasShutdownStarted) return;  // plus d'UI : le journal suffit

            // Invoke est BLOQUANT à dessein : sur un crash fatal, le thread qui meurt doit attendre que
            // l'utilisateur ait répondu, sinon le processus disparaît sous le dialogue.
            if (disp.CheckAccess()) Show(ex, fatal);
            else disp.Invoke(new Action(() => Show(ex, fatal)));
        }

        static void Show(Exception ex, bool fatal)
        {
            var dlg = new Dialogs.ReportBugDialog(ActiveEditor(), ex, fatal);
            try
            {
                var owner = Application.Current?.MainWindow;
                // Une fenêtre non chargée ou déjà en cours de fermeture refuse d'être Owner.
                if (owner != null && owner.IsLoaded && owner.IsVisible) dlg.Owner = owner;
            }
            catch { /* sans Owner le dialogue s'ouvre centré écran : acceptable */ }
            dlg.ShowDialog();
        }

        // Le morceau ouvert, pour pouvoir joindre son JSON. Tout est en try/catch : l'état de l'UI est
        // justement ce qui vient de casser.
        static Screens.TimelineScreen ActiveEditor()
        {
            try { return (Application.Current?.MainWindow as MainWindow)?.CurrentEditor; }
            catch { return null; }
        }

        /// <summary>
        /// Met l'exception à plat : la CHAÎNE des messages (externe → interne), puis les piles d'appels.
        /// Utilisé tel quel dans le journal et dans le corps de l'issue.
        /// </summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return "(exception nulle)";
            var sb = new StringBuilder();
            try
            {
                int level = 0;
                for (var e = ex; e != null && level < 8; e = e.InnerException, level++)
                {
                    string indent = level == 0 ? "" : new string(' ', level * 2) + "↳ ";
                    sb.AppendLine(indent + e.GetType().FullName + " : " + OneLine(e.Message));

                    // InnerException ne donne que la PREMIÈRE d'une AggregateException ; on liste les autres,
                    // sinon la vraie cause peut rester invisible.
                    var agg = e as AggregateException;
                    if (agg?.InnerExceptions != null && agg.InnerExceptions.Count > 1)
                        for (int i = 0; i < agg.InnerExceptions.Count && i < 8; i++)
                            sb.AppendLine(new string(' ', (level + 1) * 2) + "• "
                                          + agg.InnerExceptions[i].GetType().Name + " : "
                                          + OneLine(agg.InnerExceptions[i].Message));
                }

                sb.AppendLine();
                sb.AppendLine("--- Pile d'appels ---");
                sb.AppendLine(string.IsNullOrWhiteSpace(ex.StackTrace) ? "(aucune)" : ex.StackTrace);

                // La pile de l'exception la plus interne est celle du vrai point de rupture : celle de
                // l'exception externe s'arrête au rethrow et masque l'origine.
                var deepest = Deepest(ex);
                if (!ReferenceEquals(deepest, ex) && !string.IsNullOrWhiteSpace(deepest.StackTrace))
                {
                    sb.AppendLine();
                    sb.AppendLine("--- Pile d'appels (" + deepest.GetType().Name + ", origine) ---");
                    sb.AppendLine(deepest.StackTrace);
                }
            }
            catch (Exception inner)
            {
                sb.AppendLine("(mise à plat incomplète : " + inner.Message + ")");
            }
            return sb.ToString().TrimEnd();
        }

        static Exception Deepest(Exception ex)
        {
            int guard = 0;
            while (ex?.InnerException != null && guard++ < 8) ex = ex.InnerException;
            return ex;
        }

        static string OneLine(string s)
            => string.IsNullOrEmpty(s) ? "(sans message)"
             : s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();

        static void Log(Exception ex, string source)
        {
            try
            {
                string path = LogPath;
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxLogBytes) fi.Delete();
                }
                catch { /* journal verrouillé ou illisible : on tente quand même l'écriture */ }

                var sb = new StringBuilder();
                sb.AppendLine("======== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                              + "  [" + source + "]  v" + AppVersion());
                sb.AppendLine(Describe(ex));
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { /* un filet de sécurité qui lève n'est plus un filet */ }
        }

        internal static string AppVersion()
        {
            try { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?"; }
            catch { return "?"; }
        }
    }
}
