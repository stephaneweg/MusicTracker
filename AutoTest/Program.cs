using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace MusicTracker.AutoTest
{
    // UI smoke test harness: launches KotonStudio.exe, drives a handful of core scenarios through
    // UIA, and reports pass/fail/crash per scenario as JSON. Meant to be run headless by the
    // musictracker-daily-test scheduled task, which turns failures into GitHub issues.
    class Program
    {
        static Application app;
        static UIA3Automation automation;
        static Window window;

        static int Main(string[] args)
        {
            string exePath = GetArg(args, "--exe");
            string outPath = GetArg(args, "--out") ?? "results.json";

            if (string.IsNullOrEmpty(exePath))
            {
                var candidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MusicTracker", "bin", "Release", "KotonStudio.exe"),
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MusicTracker", "bin", "Debug", "KotonStudio.exe"),
                };
                exePath = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
            }

            var report = new RunReport
            {
                StartedAtUtc = DateTime.UtcNow.ToString("o"),
                ExePath = exePath,
            };

            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                report.Scenarios.Add(new ScenarioResult { Name = "AppLaunch", Status = "fail", Message = "KotonStudio.exe introuvable (chemin: " + exePath + ")" });
                WriteReport(report, outPath);
                return 1;
            }

            try
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    app = Application.Launch(exePath);
                    automation = new UIA3Automation();
                    window = app.GetMainWindow(automation, TimeSpan.FromSeconds(25));
                    if (window == null) throw new Exception("Fenêtre principale introuvable après 25s.");
                    report.Scenarios.Add(Pass("AppLaunch", sw));
                }
                catch (Exception ex)
                {
                    report.Scenarios.Add(Fail("AppLaunch", sw, ex));
                    WriteReport(report, outPath);
                    return 1;
                }

                // Blank timeline: the shortest path into the editor, no modal dialog involved.
                RunScenario(report, "NewMusicOpensEditor", () =>
                {
                    Click(Require("BtnNewSequencer", TimeSpan.FromSeconds(10), "la tuile « Nouvelle musique » de l'accueil"));
                    Require("BtnPlay", TimeSpan.FromSeconds(15), "la barre de transport de l'éditeur (l'éditeur ne s'est pas ouvert)");
                });

                RunScenario(report, "PlayThenStop", () =>
                {
                    Click(Require("BtnPlay", TimeSpan.FromSeconds(5), "le bouton Lecture"));
                    Thread.Sleep(2500);
                    if (app.HasExited) throw new Exception("L'application a quitté pendant la lecture.");
                    Click(Require("BtnStop", TimeSpan.FromSeconds(5), "le bouton Stop"));
                    Thread.Sleep(300);
                });

                RunScenario(report, "SaveDialogOpensAndCancels", () =>
                {
                    ClickModal(Require("BtnSaveMusic", TimeSpan.FromSeconds(5), "le bouton Sauvegarder"));
                    var dialog = WaitForNewWindow(TimeSpan.FromSeconds(8));
                    if (dialog == null) throw new Exception("Aucune boîte de dialogue d'enregistrement n'est apparue après clic sur Sauvegarder.");
                    CloseWindow(dialog);
                    if (app.HasExited) throw new Exception("L'application a quitté après fermeture de la boîte de dialogue.");
                });

                // Generative templates: card ▸ "combien de mesures" modal ▸ project built on the timeline.
                RunScenario(report, "OpenTemplateProject", () =>
                {
                    Click(Require("BtnHomeTab", TimeSpan.FromSeconds(5), "l'onglet Accueil"));

                    var card = Retry.WhileNull(
                        () => window.FindAllDescendants().FirstOrDefault(e =>
                            (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("TemplateCard_")),
                        TimeSpan.FromSeconds(10)).Result;
                    if (card == null) throw new Exception("Aucune carte de modèle (TemplateCard_*) trouvée sur l'accueil.");
                    ClickModal(card);

                    var measures = WaitForNewWindow(TimeSpan.FromSeconds(8));
                    if (measures == null) throw new Exception("La boîte « combien de mesures » n'est pas apparue après clic sur un modèle.");
                    var ok = measures.FindFirstDescendant(cf => cf.ByAutomationId("BtnTemplateMeasuresOk"));
                    if (ok == null) throw new Exception("Bouton « Créer » introuvable dans la boîte « combien de mesures ».");
                    Click(ok);

                    Require("BtnPlay", TimeSpan.FromSeconds(40), "la barre de transport (le projet issu du modèle ne s'est pas construit)");
                });
            }
            finally
            {
                report.AppCrashed = app != null && app.HasExited;
                try
                {
                    if (app != null && !app.HasExited)
                    {
                        app.Close();
                        Thread.Sleep(2000);
                        if (!app.HasExited) app.Kill();
                    }
                }
                catch { /* best-effort cleanup */ }
                automation?.Dispose();
            }

            WriteReport(report, outPath);
            return report.Scenarios.Any(s => s.Status != "pass") ? 1 : 0;
        }

        /// <summary>Wait for a control by AutomationId, or throw naming what the tester was looking for.</summary>
        static AutomationElement Require(string automationId, TimeSpan timeout, string what)
        {
            var el = Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), timeout).Result;
            if (el == null) throw new Exception("Impossible de trouver " + what + " (AutomationId « " + automationId + " ») après " + (int)timeout.TotalSeconds + "s.");
            return el;
        }

        /// <summary>Invoke rather than mouse-click: works even when the control is scrolled out of view.</summary>
        static void Click(AutomationElement el) => el.AsButton().Invoke();

        /// <summary>For buttons that open a modal via ShowDialog(): UIA Invoke() is synchronous and would block
        /// until the dialog closes, so the dialog could never be observed. A real mouse click returns immediately.</summary>
        static void ClickModal(AutomationElement el)
        {
            try { window.SetForeground(); } catch { }
            try { el.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch { }
            Thread.Sleep(200);
            el.Click();
        }

        /// <summary>The app's dialogs are owned WPF windows with WindowStyle=None/AllowsTransparency: UIA exposes them
        /// as Window CHILDREN of the main window, not as top-level windows. Check there first, then fall back.</summary>
        static Window WaitForNewWindow(TimeSpan timeout) =>
            Retry.WhileNull(() =>
            {
                var owned = window.FindAllChildren(cf => cf.ByControlType(ControlType.Window)).FirstOrDefault();
                if (owned != null) return owned.AsWindow();
                return app.GetAllTopLevelWindows(automation)
                          .FirstOrDefault(w => w.Properties.NativeWindowHandle.ValueOrDefault
                                               != window.Properties.NativeWindowHandle.ValueOrDefault);
            }, timeout).Result;

        static void CloseWindow(Window w)
        {
            try { w.Close(); } catch { Keyboard.Press(VirtualKeyShort.ESCAPE); }
            Thread.Sleep(500);
        }

        static void RunScenario(RunReport report, string name, Action body)
        {
            var sw = Stopwatch.StartNew();
            if (app.HasExited)
            {
                report.Scenarios.Add(new ScenarioResult { Name = name, Status = "skipped", Message = "L'application avait déjà quitté (crash précédent).", DurationSeconds = 0 });
                return;
            }
            try
            {
                body();
                report.Scenarios.Add(Pass(name, sw));
            }
            catch (Exception ex)
            {
                report.Scenarios.Add(app.HasExited ? Crash(name, sw, ex) : Fail(name, sw, ex));
            }
        }

        static ScenarioResult Pass(string name, Stopwatch sw) =>
            new ScenarioResult { Name = name, Status = "pass", Message = null, DurationSeconds = sw.Elapsed.TotalSeconds };

        static ScenarioResult Fail(string name, Stopwatch sw, Exception ex) =>
            new ScenarioResult { Name = name, Status = "fail", Message = ex.Message, DurationSeconds = sw.Elapsed.TotalSeconds };

        static ScenarioResult Crash(string name, Stopwatch sw, Exception ex) =>
            new ScenarioResult { Name = name, Status = "crash", Message = "L'application a quitté de manière inattendue : " + ex.Message, DurationSeconds = sw.Elapsed.TotalSeconds };

        static void WriteReport(RunReport report, string outPath)
        {
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outPath, json);
            Console.WriteLine(json);
        }

        static string GetArg(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
        }
    }
}
