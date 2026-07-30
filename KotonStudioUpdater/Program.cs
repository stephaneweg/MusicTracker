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
                var opts = ParseArgs(args);
                if (opts == null) { Log("Arguments invalides. Attendu : --pid N --source DIR --target DIR --launch EXE"); return 2; }

                WaitForParent(opts.ParentPid);
                CopyOverwriting(opts.SourceDir, opts.TargetDir);
                if (!string.IsNullOrEmpty(opts.LaunchExe) && File.Exists(opts.LaunchExe))
                {
                    Process.Start(new ProcessStartInfo(opts.LaunchExe)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = opts.TargetDir,
                    });
                }
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

        static Options ParseArgs(string[] args)
        {
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
