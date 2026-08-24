using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginWindchimes
{
    /// <summary>
    /// Windchimes — carillon à vent : plusieurs tubes métalliques accordés (typiquement en gamme
    /// pentatonique) qui sont excités aléatoirement par une "brise" procédurale. Peut être joué au
    /// clavier MIDI (chaque note = une frappe d'un tube) OU laissé en mode auto : la brise excite
    /// aléatoirement les tubes selon un profil de vent contrôlable.
    ///
    /// **DSP** :
    /// - Chaque tube = 3-4 modes sinusoïdaux inharmoniques (rapports mesurés sur vrais chimes en
    ///   aluminium type Woodstock) : 1.0, 2.756, 5.404, 8.933 (série de Chladni pour tube libre-libre)
    /// - Excitation impulsionnelle courte (le battant qui frappe le tube)
    /// - Auto-play : événements Poisson selon le paramètre Wind (0..1 → 0..3 frappes/sec)
    /// - Chaque frappe pick un tube aléatoire dans la gamme active
    /// - Petit random de pitch (± 0.5%) simule l'imperfection du son réel
    ///
    /// Gamme fixe (pentatonique majeure sur do) : do, ré, mi, sol, la (5 tubes classiques).
    /// Les NoteOn MIDI mappent directement sur les tubes correspondants.
    /// </summary>
    [KotonInstrument("Windchimes", Id = "koton.windchimes", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class WindchimesPlugin : IKotonInstrument
    {
        public string Id => "koton.windchimes";
        public string DisplayName => "Windchimes";

        readonly KotonParameter _brightness = new KotonParameter("brightness", "Brightness",  0.0, 1.0, 0.65);
        readonly KotonParameter _decay      = new KotonParameter("decay",      "Decay",       0.0, 1.0, 0.55);
        readonly KotonParameter _wind       = new KotonParameter("wind",       "Wind (auto)", 0.0, 1.0, 0.00);
        readonly KotonParameter _windGust   = new KotonParameter("wind_gust",  "Wind gust (rafales)", 0.0, 1.0, 0.30);
        readonly KotonParameter _stereoSpread = new KotonParameter("stereo_spread", "Stereo spread", 0.0, 1.0, 0.75);
        readonly KotonParameter _volumeDb   = new KotonParameter("volume",     "Volume",      -30.0, 6.0, -4.0, "dB");
        // Ré-attaque périodique : rejoue la note tenue tous les 1/taux de seconde.
        // 0 Hz = une seule attaque, donc aucun projet existant ne change.
        readonly KotonStudio.Plugins.Shared.KotonReAttack _retrig =
            new KotonStudio.Plugins.Shared.KotonReAttack("Trémolo", 20.0, 0.0);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        ChimeVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 20;   // beaucoup de tubes peuvent résonner ensemble

        // Auto-wind : événements Poisson qui déclenchent des frappes aléatoires
        int _samplesUntilNextGust;
        float _gustPhase;
        Random _rng = new Random(11);

        // Notes autorisées par la gamme (pentatonique majeure sur do : 60, 62, 64, 67, 69 + octaves)
        // Utilisé pour l'auto-wind. Le mode manuel joue n'importe quelle note MIDI.
        static readonly int[] PentatonicNotes = { 55, 57, 60, 62, 64, 67, 69, 72, 74, 76, 79, 81, 84, 88 };

        public WindchimesPlugin()
        {
            _params = new List<KotonParameter> { _brightness, _decay, _wind, _windGust, _stereoSpread, _volumeDb, _retrig.Rate };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new WindchimesEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _voices = new ChimeVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new ChimeVoice(sampleRate);
            _samplesUntilNextGust = _sr * 2;
            _retrig.Prepare(sampleRate);
        }
        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
            _retrig.Reset();
        }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            _retrig.NoteOn(note, velocity);
            if (_voices == null || velocity == 0) return;
            float vel = velocity / 127f;
            TriggerChime(note, vel);
        }

        void TriggerChime(int note, float velocity)
        {
            ChimeVoice target = null;
            // Rejouer la MÊME note reprend sa voix au lieu d'en allouer une neuve : sans ça les coups
            // répétés s'empilent (mesure : pic 0,33 → 0,68 à 9 coups/s). C'est aussi le comportement
            // physique — ré-exciter un résonateur déjà en vibration l'arrête.
            for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive && _voices[i].Note == note) { target = _voices[i]; target.Kill(); break; }
            if (target == null) for (int i = 0; i < _voices.Length; i++) if (!_voices[i].IsActive) { target = _voices[i]; break; }
            if (target == null)
            {
                target = _voices[_stealCursor];
                _stealCursor = (_stealCursor + 1) % _voices.Length;
                target.Kill();
            }
            // Pan pseudo-random selon la note (chaque tube est physiquement à une position dans l'espace)
            float noteNorm = (note - 60) / 24f;
            if (noteNorm < -1f) noteNorm = -1f; else if (noteNorm > 1f) noteNorm = 1f;
            float pan = noteNorm * (float)_stereoSpread.Value + (float)(_rng.NextDouble() * 0.2 - 0.1);
            target.NoteOn(note, velocity, (float)_brightness.Value, (float)_decay.Value, pan);
        }

        public void NoteOff(int note, int sampleOffset = 0) { /* les tubes décroissent naturellement */ }
        public void MidiCC(int cc, int value, int sampleOffset = 0)
        {
            if (cc == 123) Reset();
        }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }

            float wind = (float)_wind.Value;
            float gust = (float)_windGust.Value;
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);

            // Auto-wind : événements Poisson modulés par LFO gust (rafales)
            _gustPhase += (float)(2 * Math.PI * 0.15 / _sr);   // 0.15 Hz = rafales lentes
            if (_gustPhase > 2 * Math.PI) _gustPhase -= (float)(2 * Math.PI);
            float gustLfo = 0.5f + 0.5f * (float)Math.Sin(_gustPhase);
            float effectiveWind = wind * (1f + gust * (gustLfo - 0.5f));

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                // Ré-attaque : à l'échéance, la note tenue est rejouée (BeginStroke neutralise
                // la notification que NoteOn va renvoyer à l'engin).
                if (_retrig.Tick()) { _retrig.BeginStroke(); for (int rt = 0; rt < _retrig.Count; rt++) NoteOn(_retrig.NoteAt(rt), _retrig.VelocityAt(rt)); _retrig.EndStroke(); }

                // Événement wind (Poisson)
                _samplesUntilNextGust--;
                if (_samplesUntilNextGust <= 0 && effectiveWind > 0.01f)
                {
                    // Frappe un tube aléatoire dans la gamme
                    int noteIdx = _rng.Next(PentatonicNotes.Length);
                    float vel = 0.3f + (float)_rng.NextDouble() * 0.5f * effectiveWind;
                    TriggerChime(PentatonicNotes[noteIdx], vel);
                    // Prochain event : distribution exponentielle (Poisson)
                    double avgSec = 1.5 / (effectiveWind + 0.05);
                    _samplesUntilNextGust = (int)(-Math.Log(1 - _rng.NextDouble()) * _sr * avgSec);
                    // Occasionnellement 2 tubes frappés proches (comme quand plusieurs bougent au vent)
                    if (_rng.NextDouble() < 0.3 * effectiveWind)
                    {
                        int noteIdx2 = _rng.Next(PentatonicNotes.Length);
                        float vel2 = 0.2f + (float)_rng.NextDouble() * 0.4f * effectiveWind;
                        TriggerChime(PentatonicNotes[noteIdx2], vel2);
                    }
                }

                float sumL = 0f, sumR = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    var voice = _voices[v];
                    if (!voice.IsActive) continue;
                    float s = voice.RenderSample();
                    sumL += s * voice.PanL;
                    sumR += s * voice.PanR;
                }
                left[i] = sumL * volLin;
                right[i] = sumR * volLin;
            }
        }

        public byte[] SaveState()
        {
            try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); }
        }
        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v; } catch { }
        }
        public void Dispose() { }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }

        public static readonly string[] PresetNames = { "Balcony calm", "Garden breeze", "Storm chimes", "Meditation", "Zen minimal" };
        static readonly double[,] PresetValues = {
            //           brgh dec  wind gust wid  vol
            /*Balcony*/  { 0.60, 0.50, 0.10, 0.30, 0.80, -4.0 },
            /*Garden*/   { 0.65, 0.55, 0.30, 0.40, 0.85, -4.0 },
            /*Storm*/    { 0.75, 0.45, 0.80, 0.75, 0.95, -3.0 },
            /*Medit*/    { 0.55, 0.75, 0.05, 0.20, 0.70, -5.0 },
            /*Zen*/      { 0.50, 0.85, 0.00, 0.00, 0.60, -5.0 },
        };
        public void LoadPreset(int idx)
        {
            if (idx < 0 || idx >= PresetValues.GetLength(0)) return;
            _brightness.Value = PresetValues[idx, 0]; _decay.Value = PresetValues[idx, 1];
            _wind.Value = PresetValues[idx, 2]; _windGust.Value = PresetValues[idx, 3];
            _stereoSpread.Value = PresetValues[idx, 4]; _volumeDb.Value = PresetValues[idx, 5];
        }
    }

    /// <summary>Un chime = tube métallique libre-libre. Modes de Chladni pour un tube libre aux 2
    /// bouts : f_n = f0 × [1, 2.756, 5.404, 8.933, 13.34, ...]. Les ratios inharmoniques donnent
    /// le timbre "clocher" caractéristique. Décroissance longue et brillante.</summary>
    internal sealed class ChimeVoice
    {
        readonly int _sr;
        const int NumModes = 5;
        static readonly float[] ModeRatios = { 1.0f, 2.756f, 5.404f, 8.933f, 13.344f };
        static readonly float[] ModeAmps   = { 1.0f, 0.55f,  0.30f,  0.15f,  0.08f };
        static readonly float[] ModeDecayBaseMs = { 6000f, 3500f, 2000f, 1200f, 700f };

        readonly double[] _phase = new double[NumModes];
        readonly double[] _phaseInc = new double[NumModes];
        readonly float[] _amp = new float[NumModes];
        readonly float[] _decayFactor = new float[NumModes];

        bool _active;
        public bool IsActive => _active;
        public float PanL, PanR;
        int _note;
        public int Note => _note;

        Random _rng;

        public ChimeVoice(int sampleRate)
        {
            _sr = sampleRate;
        }

        public void NoteOn(int note, float velocity, float brightness, float decay, float pan)
        {
            _note = note;
            _rng = new Random(note * 7919 + Environment.TickCount);
            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            // Petit random ±0.5% pour l'imperfection réelle
            freq *= 1.0 + (_rng.NextDouble() * 2 - 1) * 0.005;

            float p01 = 0.5f * (1f + Math.Max(-1f, Math.Min(1f, pan)));
            PanL = 1f - p01;
            PanR = p01;

            for (int i = 0; i < NumModes; i++)
            {
                double f = freq * ModeRatios[i];
                if (f > _sr * 0.45)
                {
                    _amp[i] = 0f; _phaseInc[i] = 0; _decayFactor[i] = 0.5f;
                    continue;
                }
                _phase[i] = 0;
                _phaseInc[i] = 2 * Math.PI * f / _sr;
                // Brightness renforce les modes aigus
                float brightnessBoost = 1f + i * brightness * 0.3f;
                _amp[i] = velocity * ModeAmps[i] * brightnessBoost * 0.5f;
                // Decay proportionnel à Resonance (0=court, 1=très long)
                float effDecayMs = ModeDecayBaseMs[i] * (0.3f + decay * 1.7f);
                double samples = effDecayMs * _sr / 1000.0;
                _decayFactor[i] = (float)Math.Exp(-6.907755278982137 / samples);
            }
            _active = true;
        }

        public void Kill()
        {
            _active = false;
            for (int i = 0; i < NumModes; i++) _amp[i] = 0f;
        }

        public float RenderSample()
        {
            if (!_active) return 0f;
            float sum = 0f;
            float maxAmp = 0f;
            for (int i = 0; i < NumModes; i++)
            {
                sum += _amp[i] * (float)Math.Sin(_phase[i]);
                _phase[i] += _phaseInc[i];
                if (_phase[i] > 2 * Math.PI) _phase[i] -= 2 * Math.PI;
                _amp[i] *= _decayFactor[i];
                if (_amp[i] > maxAmp) maxAmp = _amp[i];
            }
            if (maxAmp < 1e-5f) { _active = false; return 0f; }
            return (float)Math.Tanh(sum * 0.6);
        }
    }
}
