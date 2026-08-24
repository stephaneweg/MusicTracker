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

        readonly KotonParameter _droneSource= new KotonParameter("drone_source","Drone source", 0, 1, 1) { Automatable = false };   // 0=MIDI fixe, 1=fondamentale accord
        readonly KotonParameter _dronePitch = new KotonParameter("drone_pitch","Drone pitch (MIDI)", 24, 60, 46);
        readonly KotonParameter _droneMix   = new KotonParameter("drone_mix",  "Drone mix",   0.0, 1.0, 0.55);
        readonly KotonParameter _reedHz     = new KotonParameter("reed_hz",    "Reed formant", 1000, 4000, 2200, "Hz");
        readonly KotonParameter _reedQ      = new KotonParameter("reed_q",     "Reed Q",      2, 12, 5);
        readonly KotonParameter _reedNoise  = new KotonParameter("reed_noise", "Reed noise",  0.0, 1.0, 0.15);
        readonly KotonParameter _brightness = new KotonParameter("brightness", "Brightness",  0.0, 1.0, 0.70);
        readonly KotonParameter _attack     = new KotonParameter("attack",     "Attack",      1.0, 100.0, 10.0, "ms");
        readonly KotonParameter _release    = new KotonParameter("release",    "Release",     50.0, 1000.0, 200.0, "ms");
        readonly KotonParameter _volumeDb   = new KotonParameter("volume",     "Volume",      -30.0, 6.0, -4.0, "dB");
        // Ré-attaque périodique. Modèle AUTO-OSCILLANT : on ne rejoue PAS la note (voir
        // KotonReAttack.ArticulationSec), on met en forme la sortie.
        readonly KotonStudio.Plugins.Shared.KotonReAttack _retrig =
            new KotonStudio.Plugins.Shared.KotonReAttack("Coup de langue", 16.0, 0.0) { ArticulationSec = 0.025f };

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;
        public BagpipesPlugin() { _params = new List<KotonParameter> { _droneSource, _dronePitch, _droneMix, _reedHz, _reedQ, _reedNoise, _brightness, _attack, _release, _volumeDb, _retrig.Rate }; }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new BagpipesEditor(this);

        int _sr;
        // Drone tone : 3 SAW (fondamentale + octave + douzieme) filtrees LP ~800 Hz.
        // Anciennement 3 sines pures → son "flute lisse" pas cornemuse. Un vrai bourdon a une
        // anche riche en harmoniques (paires+impaires) filtrees par le tube.
        double _drone1, _drone2, _drone3;
        double _droneInc1, _droneInc2, _droneInc3;
        float _droneLp;                   // LP 1-pole state pour filtrer les saw drones
        float _droneLpAlpha;
        // Enveloppe drone : fade in rapide (5ms), fade out lent (400ms) quand toutes les voix
        // sont eteintes → recree l'inertie de l'air dans la poche du cornemuseur (au lieu d'un
        // cut net qui casse le "legato de bourdon" caracteristique de l'instrument).
        float _droneEnv;
        float _droneEnvUpRate, _droneEnvDownRate;
        // Compteur de samples ecoules depuis Prepare : sert a estimer le beat courant pour
        // relire KotonHost.GetChordAt(beat) — sans ca on lisait toujours l'accord au beat 0
        // du morceau (drone fixe meme quand l'harmonie evolue).
        long _samplesElapsed;
        // Chanterelle voices (poly 4 pour permettre les ornements courts)
        ChanterelleVoice[] _voices;

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _voices = new ChanterelleVoice[4];
            for (int i = 0; i < 4; i++) _voices[i] = new ChanterelleVoice(sampleRate);
            _droneLpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * 800.0 / sampleRate);
            _droneEnvUpRate   = 1f / Math.Max(1, 0.005f * sampleRate);   // 5 ms attack
            _droneEnvDownRate = 1f / Math.Max(1, 0.400f * sampleRate);   // 400 ms release
            _droneEnv = 0f;
            _retrig.Prepare(sampleRate);
        }
        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill(); _droneEnv = 0f; _droneLp = 0f; _samplesElapsed = 0;
            _retrig.Reset();
        }
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            _retrig.NoteOn(note, velocity);
            if (_voices == null || velocity == 0) return;
            // Detecte a la volee "premier NoteOn sans voix active" AVANT d'allouer la voix cible.
            // Remplace le _activeCount qui derivait via le voice stealing (steal ecrasait une voix
            // active sans decrement du compteur → drift +1 a chaque steal).
            bool wasSilent = true;
            for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive) { wasSilent = false; break; }
            ChanterelleVoice t = null;
            for (int i = 0; i < _voices.Length; i++) if (!_voices[i].IsActive) { t = _voices[i]; break; }
            if (t == null) { t = _voices[0]; t.Kill(); }
            t.NoteOn(note, velocity / 127f, (float)_attack.Value);
            // Setup/re-setup du drone A CHAQUE NoteOn (pas seulement au premier). Ainsi si
            // l'harmonie change entre 2 notes tenues, le drone glisse vers la nouvelle fondamentale
            // au lieu de rester bloque sur l'accord initial.
            UpdateDrone();
        }

        void UpdateDrone()
        {
            int droneMidi = (int)Math.Round(_dronePitch.Value);
            if (_droneSource.Value >= 0.5 && KotonHost.GetChordAt != null)
            {
                // Beat courant estime depuis le compteur de samples ecoules + tempo courant du
                // projet. Sans ca on lisait toujours l'accord au beat 0.
                double tempo = 120.0;
                try { var ctx = KotonHost.CurrentContext?.Invoke(); if (ctx != null && ctx.Tempo > 0) tempo = ctx.Tempo; } catch { }
                double currentBeat = _samplesElapsed / (double)_sr * (tempo / 60.0);
                var ch = KotonHost.GetChordAt(currentBeat);
                if (ch.HasValue)
                {
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
            _droneInc3 = baseF * 3 / _sr;
        }
        public void NoteOff(int note, int sampleOffset = 0)
        {
            _retrig.NoteOff(note);
            if (_voices == null) return;
            for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive && _voices[i].Note == note) _voices[i].NoteOff((float)_release.Value);
        }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }
            _samplesElapsed += left.Length;   // compteur pour estimation du beat courant
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            float dMix = (float)_droneMix.Value;
            float reedHz = (float)_reedHz.Value, reedQ = (float)_reedQ.Value, reedNoise = (float)_reedNoise.Value, bright = (float)_brightness.Value;
            bool anyActive = false;
            for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive) { anyActive = true; break; }
            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                _retrig.Tick();
                float artG = _retrig.Gain;

                float chSum = 0;
                for (int v = 0; v < _voices.Length; v++) if (_voices[v].IsActive) chSum += _voices[v].RenderSample(reedHz, reedQ, reedNoise, bright);

                // Enveloppe drone : rise rapide quand une voix demarre, fall lent quand toutes
                // s'eteignent (simule l'air dans la poche du cornemuseur — plus de cut net).
                float droneTarget = anyActive ? 1f : 0f;
                if (droneTarget > _droneEnv) _droneEnv = Math.Min(droneTarget, _droneEnv + _droneEnvUpRate);
                else if (droneTarget < _droneEnv) _droneEnv = Math.Max(droneTarget, _droneEnv - _droneEnvDownRate);

                float drone = 0;
                if (_droneEnv > 1e-4f)
                {
                    _drone1 += _droneInc1; if (_drone1 >= 1) _drone1 -= 1;
                    _drone2 += _droneInc2; if (_drone2 >= 1) _drone2 -= 1;
                    _drone3 += _droneInc3; if (_drone3 >= 1) _drone3 -= 1;
                    // 3 SAW (fondamentale + octave + douzieme) au lieu de sines : anche double du
                    // bourdon = spectre riche paires+impaires, pas onde pure.
                    float saw1 = (float)(2.0 * _drone1 - 1.0);
                    float saw2 = (float)(2.0 * _drone2 - 1.0);
                    float saw3 = (float)(2.0 * _drone3 - 1.0);
                    float sawMix = saw1 * 0.5f + saw2 * 0.3f + saw3 * 0.2f;
                    // LP 1-pole a 800 Hz : coupe les aigues des saw (anti-aliasing + coloration
                    // "guttural/boisee" de l'anche filtree par le tube du bourdon).
                    _droneLp += _droneLpAlpha * (sawMix - _droneLp);
                    drone = _droneLp * _droneEnv;
                }

                float s = (chSum + drone * dMix * 0.6f) * volLin;
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                left[i] = s; right[i] = s;
                            // Enveloppe d'articulation du coup de langue / détaché : la note continue de sonner
                // sous-jacente, c'est la SORTIE qu'on découpe.
                left[i] *= artG; right[i] *= artG;
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
        float _cachedReedHz = -1f, _cachedReedQ = -1f;   // pour eviter le recalcul biquad par sample
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
            _cachedReedHz = -1f; _cachedReedQ = -1f;   // force le premier setup du biquad reed
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
            // Ne recalcule le biquad reed que quand les params changent (etait recalcule
            // 44100 fois/sec/voix = 8 Math.Sin+Math.Cos par sample par voix, catastrophe CPU
            // et artefacts sur les slides continus).
            if (reedHz != _cachedReedHz || reedQ != _cachedReedQ)
            {
                SetBP(ref _reed, _sr, reedHz, reedQ);
                _cachedReedHz = reedHz; _cachedReedQ = reedQ;
            }
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
