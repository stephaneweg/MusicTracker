using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicTracker
{
    /// <summary>
    /// Global, persisted application settings: which SoundFont to load and the engine sample rate.
    /// Stored next to userdata.json as settings.json (same convention as <see cref="UserData"/>).
    ///
    /// Changing the SoundFont hot-reloads the patch list (<see cref="Engine.InstrumentCatalog.Reload"/>) so a
    /// piece played right after the change uses the new font, with no app restart. Changing the sample
    /// rate is also applied by reloading the font (samples are re-resampled to the new rate); anything
    /// already playing keeps the old rate until it is restarted.
    /// </summary>
    public class AppSettings
    {
        const string FileName = "settings.json";

        static AppSettings _instance;
        public static AppSettings Instance => _instance ?? (_instance = Load());

        /// <summary>The folder holding the bundled .sf2 files (relative to the assembly directory).</summary>
        public const string SoundFontFolder = "SoundFont";

        /// <summary>Standard output sample rates offered in the settings dialog.</summary>
        public static readonly int[] StandardSampleRates = { 8000, 11025, 16000, 22050, 32000, 44100, 48000 };

        /// <summary>Chosen SoundFont file name (within <see cref="SoundFontFolder"/>) or absolute path.
        /// Empty = the engine default (<see cref="Engine.InstrumentCatalog.DefaultSoundFont"/>).</summary>
        public string SoundFont { get; set; } = "";

        /// <summary>The engine sample rate (one of <see cref="StandardSampleRates"/>).</summary>
        public int SampleRate { get; set; } = Engine.AudioFormat.DefaultSampleRate;

        /// <summary>Last-used settings of the riff recorder dialogs (remembered between sessions).</summary>
        public RecorderSettings Recorder { get; set; } = new RecorderSettings();

        /// <summary>Last-used selections of the "Créer structure" dialog (remembered between sessions).</summary>
        public StructureSettings Structure { get; set; } = new StructureSettings();

        /// <summary>Riff editor keyboard entry speed, in slices per second (hold-to-grow note + Backspace erase).</summary>
        public double RiffInputSpeed { get; set; } = 2;

        /// <summary>Riff editor keyboard entry snap precision, as a fraction of a beat (0 = no snap). Default 1/8.</summary>
        public double RiffSnapFraction { get; set; } = 0.125;

        /// <summary>Riff editor live audio-input device (by name); remembered between sessions.</summary>
        public string RiffAudioInDevice { get; set; } = "";

        /// <summary>Riff editor live MIDI-input device (by name); remembered between sessions.</summary>
        public string RiffMidiInDevice { get; set; } = "";

        /// <summary>Riff editor tempo (BPM) for the ▶ preview and the MIDI/audio cursor advance. Default 100.</summary>
        public double RiffEditorTempo { get; set; } = 100;

        /// <summary>Audio input: snap detected pitches to the editor's scale. Off by default (chromatic).</summary>
        public bool RiffAudioScaleSnap { get; set; } = false;

        /// <summary>Audio input: sensitivity to re-attacks on the SAME pitch (0..1) → splits a détaché into separate
        /// notes. Higher = more sensitive (a softer re-bow triggers a new note). Default 0.5.</summary>
        public double RiffAudioOnsetSensitivity { get; set; } = 0.5;

        /// <summary>Audio input: MPM analysis window in samples (power of 2). Smaller = lower latency, but worse on
        /// low pitches. Default 1024 (~23 ms @ 44.1 kHz). Applied to <see cref="Engine.AudioPitch.FrameSize"/>.</summary>
        public int RiffAudioFrameSize { get; set; } = 1024;

        /// <summary>Riff editor: play a MIDI echo (audition) of played/detected notes. Turn off when monitoring a
        /// live instrument through the audio input (you already hear it). Default on.</summary>
        public bool RiffAudition { get; set; } = true;

        /// <summary>Mistral API key (free tier at console.mistral.ai) for the AI arrangement dialog. Stored locally.</summary>
        public string MistralApiKey { get; set; } = "";

        /// <summary>Mistral model id used by the AI arrangement dialog. Default = a small, fast model.</summary>
        public string MistralModel { get; set; } = "mistral-small-latest";

        /// <summary>Google Gemini API key (free tier at aistudio.google.com) for the AI arrangement dialog. Stored locally.</summary>
        public string GeminiApiKey { get; set; } = "";

        /// <summary>Gemini model id used by the AI arrangement dialog.</summary>
        public string GeminiModel { get; set; } = "gemini-2.0-flash";

        /// <summary>Groq API key (free tier at console.groq.com) for the AI arrangement dialog. Stored locally.</summary>
        public string GroqApiKey { get; set; } = "";

        /// <summary>Groq model id used by the AI arrangement dialog.</summary>
        public string GroqModel { get; set; } = "llama-3.3-70b-versatile";

        /// <summary>DeepSeek API key (platform.deepseek.com) for the AI arrangement dialog. Stored locally.</summary>
        public string DeepSeekApiKey { get; set; } = "";

        /// <summary>DeepSeek model id used by the AI arrangement dialog.</summary>
        public string DeepSeekModel { get; set; } = "deepseek-chat";

        /// <summary>Anthropic Claude API key (console.anthropic.com) for the AI arrangement dialog. Stored locally.</summary>
        public string ClaudeApiKey { get; set; } = "";

        /// <summary>Claude model id used by the AI arrangement dialog.</summary>
        public string ClaudeModel { get; set; } = "claude-opus-4-8";

        /// <summary>xAI Grok API key (console.x.ai) for the AI arrangement dialog. Stored locally.</summary>
        public string GrokApiKey { get; set; } = "";

        /// <summary>Grok model id used by the AI arrangement dialog.</summary>
        public string GrokModel { get; set; } = "grok-4";

        /// <summary>Alibaba Qwen (DashScope) API key for the AI arrangement dialog. Stored locally.</summary>
        public string QwenApiKey { get; set; } = "";

        /// <summary>Qwen model id used by the AI arrangement dialog.</summary>
        public string QwenModel { get; set; } = "qwen3.7-max";

        /// <summary>Which AI provider the arrangement dialog last used: "mistral", "gemini", "groq", "deepseek", "claude", "grok" or "qwen".</summary>
        public string AiProvider { get; set; } = "mistral";

        /// <summary>Named API keys (multiple per provider, e.g. several Gemini projects rotated to beat daily limits).
        /// Managed in Paramètres → Clés API; the compose dialogs pick one by name. Migrated from the single-key fields
        /// above on first use (each becomes a "Défaut" entry).</summary>
        public List<ApiKeyEntry> ApiKeys { get; set; } = new List<ApiKeyEntry>();

        /// <summary>The key NAME last chosen per provider (provider id → key name), so the compose dialogs restore it.</summary>
        public Dictionary<string, string> SelectedKeyName { get; set; } = new Dictionary<string, string>();

        /// <summary>Per-instrument playback volume boost (GM program 0-127, or 128 = drum kit) → slider -10..+10.
        /// 0 = unchanged (×1), +10 = ×<see cref="MaxBoostFactor"/>, -10 = ÷<see cref="MaxBoostFactor"/>.
        /// Applied by the MeltySynth playback (timeline + editors). Edited in the "Boost instruments" dialog.</summary>
        public Dictionary<int, int> InstrumentBoost { get; set; } = new Dictionary<int, int>();

        /// <summary>Max multiply/divide factor at slider ±10 (so the full range is ÷10 … ×10, ≈ ±20 dB).</summary>
        public const double MaxBoostFactor = 10.0;

        /// <summary>Linear gain for an instrument's boost slider: MaxBoostFactor^(slider/10). 1.0 when unset.</summary>
        public double BoostGain(int program)
        {
            if (InstrumentBoost != null && InstrumentBoost.TryGetValue(program, out int s) && s != 0)
                return Math.Pow(MaxBoostFactor, Math.Max(-10, Math.Min(10, s)) / 10.0);
            return 1.0;
        }

        // ---- last-used AI arrangement dialog inputs (remembered between sessions) ----
        public string AiStyle { get; set; } = "";
        public int AiMeasures { get; set; } = 32;
        public bool AiRiffMode { get; set; } = false;
        public bool AiFixNotes { get; set; } = true;
        public bool AiDrums { get; set; } = false;
        // Chords silent on the Accords track (empty custom motif, kept only as a harmonic marker) + the AI voices the
        // chord content freely in a dedicated "Accords" voice/track.
        public bool AiChordVoice { get; set; } = false;
        public string AiIntention { get; set; } = "";

        /// <summary>Gemini 2.5 "thinking" token budget: -1 = auto (model decides, best quality), 0 = off (fastest/cheapest),
        /// up to ~24576 = max reasoning. Only applied to Gemini 2.5 models.</summary>
        public int AiThinkingBudget { get; set; } = -1;

        // ---- persistence -----------------------------------------------------------

        public void Save()
        {
            try { File.WriteAllText(AppPaths.Local(FileName), System.Text.Json.JsonSerializer.Serialize(this)); }
            catch { /* settings are best-effort; ignore write failures */ }
        }

        static AppSettings Load()
        {
            try
            {
                string path = AppPaths.Local(FileName);
                if (File.Exists(path))
                    return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
                           ?? new AppSettings();
            }
            catch { /* corrupt file -> fall back to defaults */ }
            return new AppSettings();
        }

        // ---- SoundFont discovery / resolution --------------------------------------

        /// <summary>The .sf2 files available under <see cref="SoundFontFolder"/> (file names only).</summary>
        public static List<string> AvailableSoundFonts()
        {
            try
            {
                string folder = AppPaths.Local(SoundFontFolder);
                if (Directory.Exists(folder))
                    return Directory.GetFiles(folder, "*.sf2")
                                    .Select(Path.GetFileName)
                                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                    .ToList();
            }
            catch { }
            return new List<string>();
        }

        /// <summary>Absolute path to the chosen SoundFont (resolved against the assembly directory), or the
        /// engine default if none/missing.</summary>
        public string ResolveSoundFontPath()
        {
            if (!string.IsNullOrWhiteSpace(SoundFont))
            {
                // An absolute path chosen directly.
                if (Path.IsPathRooted(SoundFont) && File.Exists(SoundFont)) return SoundFont;
                // A bundled file name inside the SoundFont folder (assembly-relative).
                string inFolder = AppPaths.Local(Path.Combine(SoundFontFolder, SoundFont));
                if (File.Exists(inFolder)) return inFolder;
                // A relative path from the assembly directory.
                string local = AppPaths.Local(SoundFont);
                if (File.Exists(local)) return local;
            }
            return AppPaths.Local(Engine.InstrumentCatalog.DefaultSoundFont);
        }

        // ---- applying --------------------------------------------------------------

        /// <summary>
        /// Apply the sample rate, then (re)load the chosen SoundFont at that rate. Call once at startup
        /// BEFORE the patch list is first touched (so the initial load uses the right rate), and again
        /// whenever the settings change. Safe to call repeatedly.
        /// </summary>
        public void Apply()
        {
            if (SampleRate > 0) Engine.AudioFormat.SampleRate = SampleRate;
            if (RiffAudioFrameSize > 0) Engine.AudioPitch.FrameSize = RiffAudioFrameSize;

            string path = ResolveSoundFontPath();
            // (Re)load when the file or the rate the patches were resampled to differs from what we want.
            // Reading CurrentSoundFont triggers InstrumentCatalog's lazy initial load (at the rate set above),
            // so the first call of the session establishes the preset table and later calls hot-swap it.
            if (Engine.InstrumentCatalog.CurrentSoundFont != path
                || Engine.InstrumentCatalog.LoadedSampleRate != SampleRate)
            {
                Engine.InstrumentCatalog.Reload(path);
            }
        }
    }

    /// <summary>One named API key for a provider (several may share a provider). Stored locally in settings.json.
    /// Observable so the keys-manager ListView reflects two-way edits live.</summary>
    public class ApiKeyEntry : System.ComponentModel.INotifyPropertyChanged
    {
        string provider = "mistral", name = "", key = "";
        public string Provider { get => provider; set { if (provider != value) { provider = value; OnChanged(nameof(Provider)); } } }
        public string Name { get => name; set { if (name != value) { name = value; OnChanged(nameof(Name)); } } }
        public string Key { get => key; set { if (key != value) { key = value; OnChanged(nameof(Key)); } } }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        void OnChanged(string p) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
    }

    /// <summary>
    /// Remembered state of the riff recorder dialogs (audio + MIDI). Tempo/metronome are shared between both;
    /// the rest are audio-recorder specific. Defaults match the dialog's initial UI.
    /// </summary>
    public class RecorderSettings
    {
        // shared (audio + MIDI)
        public double Bpm { get; set; } = 80;
        public int BeatsPerBar { get; set; } = 4;
        public bool Metronome { get; set; } = true;

        // scale snapping
        public bool SnapScale { get; set; } = false;
        public int ScaleRoot { get; set; } = 0;        // cboRoot index (0 = Do)
        public int ScaleAccidental { get; set; } = 0;  // -1 / 0 / +1
        public int ScaleMode { get; set; } = 0;        // cboMode index

        // input gain
        public bool Normalize { get; set; } = true;
        public double Gain { get; set; } = 1;          // manual gain multiplier

        // polyphony
        public bool Poly { get; set; } = false;
        public double PolyThreshold { get; set; } = 0.45;

        // rhythm correction
        public bool QuantEnabled { get; set; } = true;
        public int QuantIndex { get; set; } = 2;       // cboQuant index (2 = 1/4)
        public double QuantStrength { get; set; } = 1.0; // 0..1 (1 = fully on the grid)

        // devices, kept by NAME (indices can change between sessions)
        public string AudioInputDevice { get; set; } = "";
        public string MidiInputDevice { get; set; } = "";
    }

    /// <summary>
    /// Remembered selections of the "Créer structure" dialog so the user doesn't re-pick every time. Model is kept by
    /// NAME (robust to list order); the option combos by index (clamped to the model's lists on restore). `Saved` stays
    /// false until the first "Créer", so the very first open still uses the project's key/tempo as sensible defaults.
    /// </summary>
    public class StructureSettings
    {
        public bool Saved { get; set; } = false;
        public string Model { get; set; } = "";   // composer (Orchestrateur) name
        public int Form { get; set; } = 0;
        public int Style { get; set; } = 0;
        public int Tone { get; set; } = 0;         // tonic note index (0 = Do)
        public int Mode { get; set; } = 0;
        public int Char { get; set; } = 0;
        public int Dev { get; set; } = 0;          // development method (0 = Auto)
        public int Tempo { get; set; } = 60;
        public int ThemeBars { get; set; } = 4;
        public int IntroBars { get; set; } = 4;
        public int OutroBars { get; set; } = 4;
        public int Reps { get; set; } = 2;
        public bool GenerateMusic { get; set; } = true;
        public bool CounterSameStaff { get; set; } = false;
        public int MelodyInst { get; set; } = -1;   // -1 = Auto (style default)
        public int AccompInst { get; set; } = -1;
        public int PadInst { get; set; } = -1;
    }
}
