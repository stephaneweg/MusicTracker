using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Engine.BugReport
{
    /// <summary>
    /// The attachable context of a bug report — the project as .sq JSON, the source template as JSON, and a short
    /// human summary. This is pure serialization/formatting logic, deliberately kept OUT of the TimelineScreen UI
    /// class: the screen only hands its state to <see cref="Build"/> and exposes the resulting object.
    /// </summary>
    public sealed class BugReportContext
    {
        /// <summary>The full project as .sq JSON (arrangement + referenced riffs).</summary>
        public string ProjectJson { get; private set; } = "";

        /// <summary>The source template (generative <see cref="TemplateSpec"/>) as JSON, or null if not from a template.</summary>
        public string TemplateJson { get; private set; }

        /// <summary>A few lines describing the piece (file / tracks / meter / tempo / template).</summary>
        public string Summary { get; private set; } = "";

        static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { IncludeFields = true };

        /// <summary>Build the report context from a project's state. <paramref name="templateSpec"/> may be null.</summary>
        public static BugReportContext Build(TimelineProject project, IEnumerable<Riff> riffs,
                                             TemplateSpec templateSpec, string currentPath, int templateSeed)
        {
            var ctx = new BugReportContext
            {
                ProjectJson = SerializeProject(project, riffs),
                TemplateJson = SerializeTemplate(templateSpec),
                Summary = BuildSummary(project, currentPath, templateSpec, templateSeed),
            };
            return ctx;
        }

        static string SerializeProject(TimelineProject project, IEnumerable<Riff> riffs)
        {
            try
            {
                var doc = new TimelineDocument { Project = project };
                if (riffs != null) doc.Riffs.AddRange(riffs);
                return JsonSerializer.Serialize(doc, JsonOpts);
            }
            catch (System.Exception ex) { return "/* sérialisation du projet impossible : " + ex.Message + " */"; }
        }

        static string SerializeTemplate(TemplateSpec templateSpec)
        {
            if (templateSpec == null) return null;
            try { return JsonSerializer.Serialize(templateSpec, JsonOpts); }
            catch { return null; }
        }

        static string BuildSummary(TimelineProject project, string currentPath, TemplateSpec templateSpec, int templateSeed)
        {
            try
            {
                var sb = new StringBuilder();
                string file = string.IsNullOrEmpty(currentPath) ? "(non enregistré)" : System.IO.Path.GetFileName(currentPath);
                sb.AppendLine($"- Fichier : {file}");
                sb.AppendLine($"- Pistes : {(project.Tracks?.Count ?? 0)}");
                sb.AppendLine($"- Mesure : {project.TimeSigNum}/{project.TimeSigDen}" + (project.PickupBeats > 0 ? $" (levée {project.PickupBeats})" : ""));
                sb.AppendLine($"- Tempo : {project.MainBpm:0.#} BPM");
                if (templateSpec != null)
                    sb.AppendLine($"- Modèle : {templateSpec.Name} (seed {templateSeed})");
                return sb.ToString().TrimEnd();
            }
            catch { return "(résumé indisponible)"; }
        }
    }
}
