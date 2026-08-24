using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;
using KotonStudio.Plugins.Shared;

namespace KotonPluginSynthViolin
{
    /// <summary>
    /// Synth Violin — violon synthétique piloté EN CONTINU par le signal d'entrée : hauteur suivie,
    /// amplitude asservie à l'enveloppe de ce qu'on joue. Aucune note n'est jamais décidée.
    ///
    /// **Pourquoi ça sonne « instrument » et pas « synthé »** : la hauteur bouge, mais les résonances du
    /// corps restent IMMOBILES. C'est la signature acoustique d'un instrument réel — la caisse ne se
    /// transpose pas quand on change de note, elle colore toujours les mêmes fréquences. Un synthé dont le
    /// filtre suit la note sonne synthétique précisément parce qu'il n'a pas de corps fixe. On place donc
    /// trois résonances de corps (~280 Hz mode d'air, ~460 et ~700 Hz modes de bois) plus la « colline du
    /// chevalet » vers 2,5 kHz, toutes fixes.
    ///
    /// **Chaîne** : dent de scie anti-repliée (PolyBLEP) à la fréquence suivie — la dent de scie est
    /// l'approximation classique du mouvement de Helmholtz d'une corde frottée — passe-bas d'archet, puis
    /// corps résonant, plus un bruit de crin dosé par la VITESSE de l'enveloppe (ça ne crisse qu'aux
    /// attaques, comme un vrai archet). L'ensemble est multiplié par l'enveloppe de l'entrée : les nuances,
    /// le vibrato et les silences sont ceux du jeu.
    ///
    /// **Latence** : celle du suiveur de hauteur pour l'accroche initiale (~2 fenêtres, soit ~20 ms), nulle
    /// ensuite — l'oscillateur tourne en permanence, seule sa fréquence glisse.
    /// </summary>
    [KotonEffect("Synth Violin", Id = "koton.synthviolin", Category = "Instrument", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class SynthViolinPlugin : IKotonEffect
    {
        public string Id => "koton.synthviolin";
        public string DisplayName => "Synth Violin";

        readonly KotonParameter _interval = new KotonParameter("interval", "Intervalle", -24.0, 24.0, 0.0, "demi-tons");
        readonly KotonParameter _bow = new KotonParameter("bow", "Archet (brillance)", 0.0, 1.0, 0.55);
        readonly KotonParameter _body = new KotonParameter("body", "Corps", 0.0, 1.0, 0.70);
        readonly KotonParameter _noise = new KotonParameter("noise", "Bruit de crin", 0.0, 1.0, 0.25);
        readonly KotonParameter _attack = new KotonParameter("attack", "Attaque", 1.0, 80.0, 12.0, "ms");
        readonly KotonParameter _release = new KotonParameter("release", "Relâchement", 20.0, 600.0, 140.0, "ms");
        readonly KotonParameter _lowNote = new KotonParameter("low_note", "Note la plus grave", 24.0, 72.0, 55.0);
        readonly KotonParameter _maxLeap = new KotonParameter("max_leap", "Écart max", 0.0, 36.0, 12.0, "demi-tons");
        readonly KotonParameter _octaveGuard = new KotonParameter("octave_guard", "Anti-sous-harmonique", 0.0, 1.0, 0.80);
        readonly KotonParameter _mix = new KotonParameter("mix", "Mix", 0.0, 1.0, 0.85);
        readonly KotonParameter _outGain = new KotonParameter("out_gain", "Sortie", -30.0, 12.0, 0.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        readonly KotonPitchTracker _tracker = new KotonPitchTracker();

        // Résonances du corps d'un violon : mode d'air, deux modes de bois, colline du chevalet.
        static readonly double[] BodyHz = { 280, 460, 700, 2500 };
        static readonly double[] BodyQ = { 6.0, 5.0, 4.0, 2.0 };
        static readonly double[] BodyGain = { 1.00, 0.85, 0.60, 0.45 };

        int _sr = 48000;
        double _phase;
        float _env, _envPrev, _lpOsc;
        readonly Biquad[] _bodyL = new Biquad[BodyHz.Length];
        readonly Biquad[] _bodyR = new Biquad[BodyHz.Length];
        float _noiseLp;
        Random _rng = new Random(4242);

        public SynthViolinPlugin()
        {
            _params = new List<KotonParameter>
            {
                _interval, _bow, _body, _noise, _attack, _release,
                _lowNote, _maxLeap, _octaveGuard, _mix, _outGain
            };
            for (int i = 0; i < BodyHz.Length; i++) { _bodyL[i] = new Biquad(); _bodyR[i] = new Biquad(); }
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new SynthViolinEditor(this);

        /// <summary>Fréquence suivie, en Hz — pour le témoin d'accroche de l'éditeur.</summary>
        public double TrackedFrequency => _tracker.Frequency;
        public bool Locked => _tracker.Voiced;

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _tracker.Prepare(sampleRate, 2048, 512);
            for (int i = 0; i < BodyHz.Length; i++)
            {
                _bodyL[i].SetBandPass(sampleRate, BodyHz[i], BodyQ[i]);
                _bodyR[i].SetBandPass(sampleRate, BodyHz[i], BodyQ[i]);
            }
            Reset();
        }

        public void Reset()
        {
            _phase = 0; _env = _envPrev = 0; _lpOsc = 0; _noiseLp = 0;
            for (int i = 0; i < BodyHz.Length; i++) { _bodyL[i].Reset(); _bodyR[i].Reset(); }
            _tracker.Reset();
        }

        public void Process(Span<float> left, Span<float> right)
        {
            double interval = Math.Pow(2.0, _interval.Value / 12.0);
            float bowHz = (float)(600.0 + _bow.Value * _bow.Value * 9000.0);
            float lpCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * Math.Min(bowHz, _sr * 0.45) / _sr);
            float bodyAmt = (float)_body.Value;
            float noiseAmt = (float)_noise.Value;
            float atk = (float)(1.0 - Math.Exp(-1.0 / (_attack.Value * 0.001 * _sr)));
            float rel = (float)(1.0 - Math.Exp(-1.0 / (_release.Value * 0.001 * _sr)));
            float mix = (float)_mix.Value;
            float outLin = (float)Math.Pow(10.0, _outGain.Value / 20.0);
            float noiseCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * 3000.0 / _sr);

            _tracker.MinFrequency = 440.0 * Math.Pow(2.0, (_lowNote.Value - 69.0) / 12.0) * Math.Pow(2, -0.5 / 12);
            _tracker.MaxFrequency = Math.Min(2000.0, _sr * 0.25);
            _tracker.MaxLeapSemitones = _maxLeap.Value;
            _tracker.SubHarmonicGuard = _octaveGuard.Value;

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float dry = 0.5f * (left[i] + right[i]);
                double f = _tracker.Push(dry) * interval;

                // Enveloppe de l'entrée : c'est elle qui donne les nuances et les silences. Attaque et
                // relâchement séparés, comme un suiveur d'enveloppe de compresseur.
                float rect = Math.Abs(dry);
                _env += (rect > _env ? atk : rel) * (rect - _env);
                float rise = Math.Max(0f, _env - _envPrev);   // vitesse de montée = « appui » de l'archet
                _envPrev = _env;

                if (f < 20 || _env < 1e-5f)
                {
                    float d = dry * (1f - mix) * outLin;
                    left[i] = d; right[i] = d;
                    continue;
                }

                // Dent de scie anti-repliée : le spectre riche d'une corde frottée, sans repliement dans
                // l'aigu (une dent de scie naïve replierait dès quelques centaines de Hz).
                double dt = f / _sr;
                _phase += dt;
                if (_phase >= 1.0) _phase -= 1.0;
                float saw = (float)(2.0 * _phase - 1.0) - PolyBlep(_phase, dt);

                // Passe-bas d'archet : plus l'archet est « doux », moins il y a d'harmoniques hautes.
                _lpOsc += lpCoef * (saw - _lpOsc);
                float voice = _lpOsc;

                // Bruit de crin, dosé par la vitesse de montée de l'enveloppe : ça crisse à l'attaque et
                // se tait sur une tenue, comme un vrai archet.
                if (noiseAmt > 0.001f)
                {
                    float white = (float)(_rng.NextDouble() * 2.0 - 1.0);
                    _noiseLp += noiseCoef * (white - _noiseLp);
                    voice += _noiseLp * noiseAmt * (0.15f + rise * 40f);
                }

                // Corps résonant FIXE : quatre résonances qui ne bougent pas avec la note.
                float bodyL = 0f, bodyR = 0f;
                for (int b = 0; b < BodyHz.Length; b++)
                {
                    bodyL += (float)(_bodyL[b].Process(voice) * BodyGain[b]);
                    // Le canal droit passe par ses propres états : les deux voies divergent légèrement,
                    // ce qui élargit sans déphasage artificiel.
                    bodyR += (float)(_bodyR[b].Process(voice * 0.98f) * BodyGain[b]);
                }
                float wetL = voice * (1f - bodyAmt) + bodyL * bodyAmt * 1.6f;
                float wetR = voice * (1f - bodyAmt) + bodyR * bodyAmt * 1.6f;

                wetL *= _env * 2.0f;
                wetR *= _env * 2.0f;
                if (wetL > 1f || wetL < -1f) wetL = (float)Math.Tanh(wetL);
                if (wetR > 1f || wetR < -1f) wetR = (float)Math.Tanh(wetR);

                left[i] = (dry * (1f - mix) + wetL * mix) * outLin;
                right[i] = (dry * (1f - mix) + wetR * mix) * outLin;
            }
        }

        /// <summary>Correction PolyBLEP au voisinage de la discontinuité, pour une dent de scie sans
        /// repliement audible.</summary>
        static float PolyBlep(double t, double dt)
        {
            if (t < dt) { double x = t / dt; return (float)(x + x - x * x - 1.0); }
            if (t > 1.0 - dt) { double x = (t - 1.0) / dt; return (float)(x * x + x + x + 1.0); }
            return 0f;
        }

        /// <summary>Biquad passe-bande (formules RBJ) — une résonance de corps.</summary>
        sealed class Biquad
        {
            double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

            public void SetBandPass(int sr, double freq, double q)
            {
                double w0 = 2.0 * Math.PI * Math.Min(freq, sr * 0.45) / sr;
                double alpha = Math.Sin(w0) / (2.0 * q);
                double cos = Math.Cos(w0);
                double a0 = 1.0 + alpha;
                _b0 = alpha / a0; _b1 = 0; _b2 = -alpha / a0;
                _a1 = -2.0 * cos / a0; _a2 = (1.0 - alpha) / a0;
            }

            public void Reset() { _x1 = _x2 = _y1 = _y2 = 0; }

            public double Process(double x)
            {
                double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
                return y;
            }
        }

        public byte[] SaveState()
        {
            try
            {
                var d = new Dictionary<string, double>();
                foreach (var kp in _params) d[kp.Id] = kp.Value;
                return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d));
            }
            catch { return Array.Empty<byte>(); }
        }

        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state));
                if (d == null) return;
                foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v;
            }
            catch { }
        }

        public void Dispose() { }

        public static readonly string[] PresetNames =
            { "Violon", "Alto (doux)", "Violoncelle (-12)", "Crin serré", "Nappe de cordes" };

        static readonly double[,] PresetValues = {
            //                   interv bow   body  noise atk   rel   low  leap guard mix   out
            /* Violon        */ {   0,  0.55, 0.70, 0.25, 12,   140,  55,  12,  0.80, 0.85,  0.0 },
            /* Alto          */ {  -5,  0.40, 0.75, 0.18, 20,   180,  48,  12,  0.80, 0.85,  0.0 },
            /* Violoncelle   */ { -12,  0.35, 0.80, 0.15, 25,   220,  36,  12,  0.75, 0.85,  1.0 },
            /* Crin serré    */ {   0,  0.75, 0.60, 0.60,  6,    90,  55,   9,  0.80, 0.90,  0.0 },
            /* Nappe         */ {   0,  0.30, 0.85, 0.10, 60,   500,  48,  12,  0.80, 0.90, -2.0 },
        };

        public void LoadPreset(int idx)
        {
            if (idx < 0 || idx >= PresetValues.GetLength(0)) return;
            _interval.Value = PresetValues[idx, 0];
            _bow.Value = PresetValues[idx, 1];
            _body.Value = PresetValues[idx, 2];
            _noise.Value = PresetValues[idx, 3];
            _attack.Value = PresetValues[idx, 4];
            _release.Value = PresetValues[idx, 5];
            _lowNote.Value = PresetValues[idx, 6];
            _maxLeap.Value = PresetValues[idx, 7];
            _octaveGuard.Value = PresetValues[idx, 8];
            _mix.Value = PresetValues[idx, 9];
            _outGain.Value = PresetValues[idx, 10];
        }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
