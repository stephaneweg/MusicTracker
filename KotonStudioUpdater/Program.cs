using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace KotonStudioUpdater
{
    /// <summary>
    /// Petit updater console pour le MODE PORTABLE. Il s'exécute DEPUIS le dossier temporaire
    /// <c>&lt;app&gt;\.update\KotonStudio-&lt;ver&gt;\</c> (extrait du zip par Koton Studio avant de quitter),
    /// et remplace en place les fichiers de l'installation portable puis relance l'appli.
    ///
    /// Args (positional-less, ordre libre, tous obligatoires) :
    ///   --pid    &lt;PID Koton Studio parent, à attendre&gt;
    ///   --source &lt;dossier contenant les nouveaux fichiers, tel qu'extrait du zip&gt;
    ///   --target &lt;dossier de l'app à mettre à jour (contient le KotonStudio.exe actuel)&gt;
    ///   --launch &lt;chemin de l'exe à relancer après la MàJ&gt;
    ///
    /// Séquence :
    ///   1. Attendre que le PID parent se termine (30 s max, sinon kill).
    ///   2. Copier récursivement source → target en ÉCRASANT les fichiers existants (retry sur locks/AV).
    ///      Le fichier <c>.portable</c> présent dans source garantit que la cible reste marquée portable.
    ///   3. Relancer l'exe cible et quitter — le nettoyage du dossier <c>.update</c> est fait par Koton
    ///      Studio au démarrage suivant, pour ne pas essayer de se supprimer nous-mêmes.
    ///
    /// Le mode installé (Inno) continue de passer par UpdateChecker.LaunchInstaller ; cet exe n'est jamais
    /// invoqué là-bas.
    /// </summary>
    static class Program
    {
        static int Main(string[] args)
        {
            try
            {
                // Journalisé À CHAQUE FOIS, pas seulement en cas d'échec : l'updater tourne alors que
                // l'application s'est déjà fermée, donc rien à l'écran ne peut dire ce qu'il a reçu. Sans
                // cette trace, un échec d'arguments est invisible et indiagnosticable.
                Log("Lancement : " + string.Join(" | ", args));

                var opts = ParseArgs(args);
                if (opts == null) { Log("Arguments invalides. Attendu : --pid N --source DIR --target DIR --launch EXE"); return 2; }

                WaitForParent(opts.ParentPid);
                CopyOverwriting(opts.SourceDir, opts.TargetDir);

                string launch = ResolveLaunchExe(opts.LaunchExe, opts.TargetDir);
                if (!string.IsNullOrEmpty(launch) && File.Exists(launch))
                {
                    Process.Start(new ProcessStartInfo(launch)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = opts.TargetDir,
                    });
                }
                else Log("Rien à relancer : " + (launch ?? "(vide)"));
                Log("Mise à jour appliquée : " + opts.SourceDir + " -> " + opts.TargetDir);
                return 0;
            }
            catch (Exception ex)
            {
                Log("Echec updater : " + ex);
                return 1;
            }
        }

        sealed class Options
        {
            public int ParentPid;
            public string SourceDir;
            public string TargetDir;
            public string LaunchExe;
        }

        /// <summary>
        /// Répare les arguments mutilés par les versions ≤ 2.0.0.3 de Koton Studio.
        ///
        /// Elles construisaient la ligne de commande à la main, avec <c>--target "…\KotonStudio\"</c> :
        /// sous Windows, la barre oblique inverse finale ÉCHAPPE le guillemet fermant, si bien que la
        /// valeur ne se termine pas et absorbe la suite de la ligne. L'updater recevait alors un
        /// <c>--target</c> qui n'existe pas, rejetait tout et ne mettait rien à jour.
        ///
        /// Le correctif côté application (ArgumentList) ne peut pas atteindre une installation DÉJÀ
        /// déployée : c'est ce binaire-ci, livré dans la nouvelle archive, qui s'exécute. On répare donc
        /// ici, sans quoi ces installations resteraient bloquées pour toujours sur une mise à jour
        /// manuelle. À retirer quand plus personne ne tourne sous 2.0.0.3 ou antérieur.
        /// </summary>
        static string[] RepairMangledArgs(string[] args)
        {
            var outp = new System.Collections.Generic.List<string>();
            foreach (string a in args)
            {
                int q = a.IndexOf("\" --", StringComparison.Ordinal);
                if (q < 0) { outp.Add(a); continue; }

                outp.Add(a.Substring(0, q));                  // la vraie valeur, avant le guillemet parasite
                string rest = a.Substring(q + 1).Trim();      // « --clé valeur » recollé derrière
                while (rest.StartsWith("--", StringComparison.Ordinal))
                {
                    int sp = rest.IndexOf(' ');
                    if (sp < 0) { outp.Add(rest); break; }
                    outp.Add(rest.Substring(0, sp));          // la clé
                    rest = rest.Substring(sp + 1).Trim();
                    // La valeur court jusqu'à la clé suivante — pas jusqu'au prochain espace : un chemin
                    // d'installation contient très souvent des espaces.
                    int next = rest.IndexOf(" --", StringComparison.Ordinal);
                    if (next < 0) { outp.Add(rest.Trim('"')); break; }
                    outp.Add(rest.Substring(0, next).Trim('"'));
                    rest = rest.Substring(next + 1).Trim();
                }
            }
            return outp.ToArray();
        }

        /// <summary>Choisit l'exécutable à relancer. Les versions ≤ 2.0.0.3 passaient
        /// <c>Assembly.Location</c>, c'est-à-dire l'assembly managé <c>KotonStudio.dll</c> et non l'exe :
        /// Windows ne sait pas lancer une DLL. On rétablit l'exe voisin, et à défaut celui de la cible.</summary>
        static string ResolveLaunchExe(string launch, string targetDir)
        {
            if (!string.IsNullOrEmpty(launch) && launch.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                string exe = Path.ChangeExtension(launch, ".exe");
                if (File.Exists(exe)) return exe;
            }
            if (string.IsNullOrEmpty(launch) || !File.Exists(launch))
            {
                string fallback = Path.Combine(targetDir, "KotonStudio.exe");
                if (File.Exists(fallback)) return fallback;
            }
            return launch;
        }

        static Options ParseArgs(string[] args)
        {
            args = RepairMangledArgs(args);
            var o = new Options();
            for (int i = 0; i + 1 < args.Length; i += 2)
            {
                string k = args[i], v = args[i + 1];
                if (k == "--pid") { int p; if (int.TryParse(v, out p)) o.ParentPid = p; }
                else if (k == "--source") o.SourceDir = v;
                else if (k == "--target") o.TargetDir = v;
                else if (k == "--launch") o.LaunchExe = v;
            }
            if (string.IsNullOrEmpty(o.SourceDir) || string.IsNullOrEmpty(o.TargetDir)) return null;
            if (!Directory.Exists(o.SourceDir) || !Directory.Exists(o.TargetDir)) return null;
            return o;
        }

        // Le parent envoie son PID et se ferme AVANT de nous lancer, mais on attend quand même : le
        // OS met parfois quelques ms à libérer le lock d'un exe qui vient de sortir, et Process.Start
        // asynchrone côté parent peut décaler l'ordre observé.
        static void WaitForParent(int pid)
        {
            if (pid <= 0) return;
            try
            {
                var p = Process.GetProcessById(pid);
                if (!p.WaitForExit(30_000))
                {
                    try { p.Kill(); p.WaitForExit(5_000); } catch { /* on tente la MàJ malgré tout */ }
                }
            }
            catch (ArgumentException) { /* déjà terminé, cas nominal */ }
            catch (InvalidOperationException) { /* idem */ }
        }

        // Copie récursive, écrase, RETRY sur locks/AV. Ne supprime jamais rien dans la cible :
        // les fichiers qui n'existent plus dans la nouvelle version restent (accumulation acceptée
        // sur plusieurs versions ; simplicité + zéro risque d'écraser un fichier de l'utilisateur).
        static void CopyOverwriting(string sourceRoot, string targetRoot)
        {
            sourceRoot = Path.GetFullPath(sourceRoot);
            targetRoot = Path.GetFullPath(targetRoot);
            int prefix = sourceRoot.Length + (sourceRoot[sourceRoot.Length - 1] == Path.DirectorySeparatorChar ? 0 : 1);

            foreach (string file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(prefix);
                string dst = Path.Combine(targetRoot, rel);
                string dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                CopyWithRetry(file, dst);
            }
        }

        static void CopyWithRetry(string src, string dst)
        {
            const int Attempts = 12;
            for (int i = 0; i < Attempts; i++)
            {
                try
                {
                    File.Copy(src, dst, overwrite: true);
                    return;
                }
                catch (IOException) when (i < Attempts - 1) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) when (i < Attempts - 1) { Thread.Sleep(300); }
            }
        }

        static void Log(string msg)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "kotonstudio-updater.log");
                File.AppendAllText(path, DateTime.Now.ToString("O") + "  " + msg + Environment.NewLine);
            }
            catch { /* le log est best-effort */ }
        }
    }
}
