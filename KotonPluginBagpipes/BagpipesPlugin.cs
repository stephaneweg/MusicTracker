using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginBagpipes
{
    /// <summary>
    /// Bagpipes / Cornemuse — drone bourdon continu (2 sines Bb0/Bb1) + chanterelle melodique
    /// jouee par les notes MIDI (saw filtre BP haut avec bruit d'anche). Le drone se declenche
    /// des qu'une note est active et coupe apres release. Timbre nasal caracteristique.
    /// </summary>
    [KotonInstrument("Bagpipes", Id = "koton.bagpipes", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class BagpipesPlugin : IKotonInstrument
    {
        public string Id => "koton.bagpipes";
        public string DisplayName => "Bagpipes";

        readonly KotonParameter _droneSource= new KotonParameter("drone_source","Drone source", 0, 1, 1);   // 0=MIDI fixe, 1=fondamentale accord
        readonly KotonParameter _dronePitch = new KotonParameter("drone_pitch","Drone pitch (MIDI)", 24, 60, 46);
        readonly KotonParameter _droneMix   = new KotonParameter("drone_mix",  "Drone mix",   0.0, 1.0, 0.55);
        readonly KotonParameter _reedHz     = new KotonParameter("reed_hz",    "Reed formant", 1000, 4000, 2200, "Hz");
        readonly KotonParameter _reedQ      = new KotonParameter("reed_q",     "Reed Q",      2, 12, 5);
        readonly KotonParameter _reedNoise  = new KotonParameter("reed_noise", "Reed noise",  0.0, 1.0, 0.15);
        readonly KotonParameter _brightness = new KotonParameter("brightness", "Brightness",  0.0, 1.0, 0.70);
        readonly KotonParameter _attack     = new KotonParameter("attack",     "Attack",      1.0, 100.0, 10.0, "ms");
        readonly KotonParameter _release    = new KotonParameter("release",    "Release",     50.0, 1000.0, 200.0, "ms");
        readonly KotonParameter _volumeDb   = new KotonParameter("volume",     "Volume",      -30.0, 6.0, -4.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;
        public BagpipesPlugin() { _params = new List<KotonParameter> { _droneSource, _dronePitch, _droneMix, _reedHz, _reedQ, _reedNoise, _brightness, _attack, _release, _volumeDb }; }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new BagpipesEditor(this);

        int _sr;
        int _activeCount;
        // Drone tone : 2 sines (Bb0 = base, Bb1 = octave) + sine 5th (F1) pour epaissir
        double _drone1, _drone2, _drone3;
        double _droneInc1, _droneInc2, _droneInc3;
        // Chanterelle voices (poly 4 pour permettre les ornements courts)
        ChanterelleVoice[] _voices;

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _voices = new ChanterelleVoice[4];
            for (int i = 0; i < 4; i++) _voices[i] = new ChanterelleVoice(sampleRate);
            _activeCount = 0;
        }
        public void Reset() { if (_voices != null) foreach (var v in _voices) v.Kill(); _activeCount = 0; }
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            ChanterelleVoice t = null;
            for (int i = 0; i < _voices.Length; i++) if (!_voices[i].IsActive) { t = _voices[i]; break; }
            if (t == null) { t = _voices[0]; t.Kill(); }
            t.NoteOn(note, velocity / 127f, (float)_attack.Value);
            _activeCount++;
            // Setup drone freqs when first note starts.
            // Si drone_source = 1, on prend la fondamentale de l'accord courant (via KotonHost),
            // sinon la note MIDI configuree (fallback quand pas de piste d'accords sous le bloc).
            if (_activeCount == 1)
            {
                int droneMidi = (int)Math.Round(_dronePitch.Value);
                if (_droneSource.Value >= 0.5 && KotonHost.GetChordAt != null)
                {
                    var ch = KotonHost.GetChordAt(0);   // approx : on lit l'accord au debut du morceau (le drone est fixe pendant la duree active)
                    if (ch.HasValue)
                    {
                        // Recale la fondamentale de l'accord dans l'octave demandee : on prend
                        // la classe de hauteur de l'accord et l'octave la plus proche du dronePitch
                        // configure (par defaut 46 = Bb1). Ainsi changer d'accord = changer de drone
                        // dans le meme registre.
                        int rootPc = ch.Value.Root % 12;
                        int refPc = droneMidi % 12;
                        int refOct = droneMidi / 12;
                        int candidate = refOct * 12 + rootPc;
                        while (candidate - droneMidi > 6) candidate -= 12;
                        while (candidate - droneMidi < -6) candidate += 12;
                        droneMidi = candidate;
                    }
                }
                double baseF = 440.0 * Math.Pow(2.0, (droneMidi - 69) / 12.0);
                _droneInc1 = baseF / _sr;
                _droneInc2 = baseF * 2 / _sr;
                _droneInc3 = baseF * 3 / _sr;  // 12eme (quinte sur octave)
            }
        }
        public void NoteOff(int note, int sampleOffset = 0)
        {
            if (_voices == null) return;
            for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive && _voices[i].Note == note) { _voices[i].NoteOff((float)_release.Value); _activeCount = Math.Max(0, _activeCount - 1); }
        }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            float dMix = (float)_droneMix.Value;
            float reedHz = (float)_reedHz.Value, reedQ = (float)_reedQ.Value, reedNoise = (float)_reedNoise.Value, bright = (float)_brightness.Value;
            bool anyActive = false;
            for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive) { anyActive = true; break; }
            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float chSum = 0;
                for (int v = 0; v < _voices.Length; v++) if (_voices[v].IsActive) chSum += _voices[v].RenderSample(reedHz, reedQ, reedNoise, bright);

                float drone = 0;
                if (anyActive)
                {
                    _drone1 += _droneInc1; if (_drone1 >= 1) _drone1 -= 1;
                    _drone2 += _droneInc2; if (_drone2 >= 1) _drone2 -= 1;
                    _drone3 += _droneInc3; if (_drone3 >= 1) _drone3 -= 1;
                    drone = (float)(Math.Sin(_drone1 * 2 * Math.PI) * 0.5 + Math.Sin(_drone2 * 2 * Math.PI) * 0.3 + Math.Sin(_drone3 * 2 * Math.PI) * 0.2);
                }

                float s = (chSum + drone * dMix * 0.6f) * volLin;
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                left[i] = s; right[i] = s;
            }
        }

        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] state) { if (state == null || state.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }
    }

    internal sealed class ChanterelleVoice
    {
        readonly int _sr;
        double _phase, _phaseInc;
        BiquadState _reed, _lp;
        float _env, _atkR, _relR;
        int _stage;
        Random _rng; float _noiseSt;
        int _note; float _vel; bool _active;
        public bool IsActive => _active;
        public int Note => _note;
        public ChanterelleVoice(int sr) { _sr = sr; }
        public void NoteOn(int note, float vel, float atkMs)
        {
            _note = note; _vel = vel;
            double f = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _phaseInc = f / _sr; _phase = 0;
            _rng = new Random(note * 7919 + Environment.TickCount);
            _atkR = 1f / Math.Max(1, atkMs * _sr / 1000f);
            _env = 0; _stage = 1;
            SetLP(ref _lp, _sr, 4500, 0.707f);
            _active = true;
        }
        public void NoteOff(float relMs) { _relR = 1f / Math.Max(1, relMs * _sr / 1000f); _stage = 3; }
        public void Kill() { _active = false; _env = 0; _stage = 0; }
        public float RenderSample(float reedHz, float reedQ, float noiseAmt, float brightness)
        {
            if (!_active) return 0f;
            if (_stage == 1) { _env += _atkR; if (_env >= 1f) { _env = 1f; _stage = 2; } }
            else if (_stage == 3) { _env -= _relR; if (_env <= 0f) { _env = 0f; _active = false; return 0f; } }
            _phase += _phaseInc; if (_phase >= 1) _phase -= 1;
            float saw = (float)(2.0 * _phase - 1.0);
            float noise = (float)(_rng.NextDouble() * 2 - 1);
            _noiseSt = _noiseSt * 0.85f + noise * 0.15f;
            float src = saw + _noiseSt * noiseAmt * 0.2f;
            SetBP(ref _reed, _sr, reedHz, reedQ);
            float f = BiquadProcess(ref _reed, src) * (0.6f + brightness * 0.5f);
            float mix = src * 0.3f + f * 0.9f;
            float outv = BiquadProcess(ref _lp, mix);
            return outv * _env * _vel * 0.7f;
        }
        internal struct BiquadState { public float b0, b1, b2, a1, a2, x1, x2, y1, y2; }
        static void SetBP(ref BiquadState s, int sr, float freq, float q) { if (freq < 20f) freq = 20f; if (freq > sr * 0.45f) freq = sr * 0.45f; double w0 = 2.0 * Math.PI * freq / sr, alpha = Math.Sin(w0) / (2.0 * q), cosw0 = Math.Cos(w0), a0 = 1.0 + alpha; s.b0 = (float)(alpha / a0); s.b1 = 0; s.b2 = (float)(-alpha / a0); s.a1 = (float)(-2.0 * cosw0 / a0); s.a2 = (float)((1.0 - alpha) / a0); }
        static void SetLP(ref BiquadState s, int sr, float freq, float q) { if (freq < 20f) freq = 20f; if (freq > sr * 0.45f) freq = sr * 0.45f; double w0 = 2.0 * Math.PI * freq / sr, alpha = Math.Sin(w0) / (2.0 * q), cosw0 = Math.Cos(w0), a0 = 1.0 + alpha; s.b0 = (float)((1.0 - cosw0) / 2.0 / a0); s.b1 = (float)((1.0 - cosw0) / a0); s.b2 = s.b0; s.a1 = (float)(-2.0 * cosw0 / a0); s.a2 = (float)((1.0 - alpha) / a0); }
        static float BiquadProcess(ref BiquadState s, float x) { float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2; s.x2 = s.x1; s.x1 = x; s.y2 = s.y1; s.y1 = y; return y; }
    }
}
