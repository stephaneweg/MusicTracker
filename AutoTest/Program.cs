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

            Application app = null;
            UIA3Automation automation = null;
            Window window = null;

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

                RunScenario(report, app, "OpenTemplateProject", () =>
                {
                    var card = Retry.WhileNull(
                        () => window.FindAllDescendants().FirstOrDefault(e =>
                            (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("TemplateCard_")),
                        TimeSpan.FromSeconds(10)).Result;
                    if (card == null) throw new Exception("Aucune carte de modèle (TemplateCard_*) trouvée sur l'accueil.");
                    card.Click();

                    // Template creation asks "combien de mesures" in a modal dialog before building the project.
                    var measuresDialog = Retry.WhileNull(() => FindNewWindow(automation, window), TimeSpan.FromSeconds(5)).Result;
                    if (measuresDialog != null)
                    {
                        var ok = measuresDialog.FindFirstDescendant(cf => cf.ByAutomationId("BtnTemplateMeasuresOk"));
                        if (ok != null) ok.AsButton().Invoke();
                        else Keyboard.Press(VirtualKeyShort.ENTER);
                    }

                    var play = Retry.WhileNull(
                        () => window.FindFirstDescendant(cf => cf.ByAutomationId("BtnPlay")),
                        TimeSpan.FromSeconds(15)).Result;
                    if (play == null) throw new Exception("L'éditeur de timeline ne s'est pas ouvert (BtnPlay introuvable) après clic sur un modèle.");
                });

                RunScenario(report, app, "PlayThenStop", () =>
                {
                    var play = window.FindFirstDescendant(cf => cf.ByAutomationId("BtnPlay"));
                    if (play == null) throw new Exception("BtnPlay introuvable.");
                    play.AsButton().Invoke();
                    Thread.Sleep(2500);
                    if (app.HasExited) throw new Exception("L'application a quitté pendant la lecture.");
                    var stop = window.FindFirstDescendant(cf => cf.ByAutomationId("BtnStop"));
                    if (stop == null) throw new Exception("BtnStop introuvable.");
                    stop.AsButton().Invoke();
                    Thread.Sleep(300);
                });

                RunScenario(report, app, "SaveDialogOpensAndCancels", () =>
                {
                    var save = window.FindFirstDescendant(cf => cf.ByAutomationId("BtnSaveMusic"));
                    if (save == null) throw new Exception("BtnSaveMusic introuvable.");
                    save.AsButton().Invoke();

                    var dialog = Retry.WhileNull(() =>
                    {
                        var desktopWindows = automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window));
                        return desktopWindows.FirstOrDefault(w => w.Properties.ProcessId.ValueOrDefault == window.Properties.ProcessId.ValueOrDefault
                                                                    && w.Properties.NativeWindowHandle.ValueOrDefault != window.Properties.NativeWindowHandle.ValueOrDefault);
                    }, TimeSpan.FromSeconds(8)).Result;

                    if (dialog == null) throw new Exception("Aucune boîte de dialogue d'enregistrement n'est apparue après clic sur Sauvegarder.");
                    Keyboard.Press(VirtualKeyShort.ESCAPE);
                    Thread.Sleep(500);
                    if (app.HasExited) throw new Exception("L'application a quitté après fermeture de la boîte de dialogue.");
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
                        if (!app.HasExited)
                            app.Kill();
                    }
                }
                catch { /* best-effort cleanup */ }
                automation?.Dispose();
            }

            WriteReport(report, outPath);
            bool anyFailure = report.Scenarios.Any(s => s.Status != "pass");
            return anyFailure ? 1 : 0;
        }

        static void RunScenario(RunReport report, Application app, string name, Action body)
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
