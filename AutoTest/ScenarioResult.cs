using System.Collections.Generic;

namespace MusicTracker.AutoTest
{
    public class ScenarioResult
    {
        public string Name { get; set; }
        public string Status { get; set; } // pass, fail, crash, skipped
        public string Message { get; set; }
        public double DurationSeconds { get; set; }
    }

    public class RunReport
    {
        public string StartedAtUtc { get; set; }
        public string ExePath { get; set; }
        public List<ScenarioResult> Scenarios { get; set; } = new List<ScenarioResult>();
        public bool AppCrashed { get; set; }
    }
}
