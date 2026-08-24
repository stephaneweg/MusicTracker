using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginDrive
{
    /// <summary>
    /// Drive — saturation, overdrive, distorsion et fuzz, avec SURÉCHANTILLONNAGE 4×.
    ///
    /// **Pourquoi le suréchantillonnage n'est pas un luxe** : écrêter un signal crée des harmoniques
    /// jusqu'à très haut. Tout ce qui dépasse la moitié de la fréquence d'échantillonnage se replie dans la
    /// bande audible à des fréquences qui n'ont AUCUN rapport harmonique avec la note jouée — c'est le son
    /// « métallique et sale » des distorsions numériques bon marché. On travaille donc à 4× la fréquence,
    /// où ces harmoniques ont la place d'exister, et on filtre avant de redescendre. Filtres de Butterworth
    /// d'ordre 4 (deux biquads en cascade) à la montée comme à la descente.
    ///
    /// **Coupe-bas avant l'étage de gain** : distordre les graves les transforme en bouillie. Comme sur un
    /// ampli de guitare, on les retire AVANT la saturation, ce qui garde le bas du spectre net tout en
    /// laissant les médiums saturer.
    ///
    /// **Cinq caractères** : chacun est une courbe de transfert différente, et c'est la courbe qui fait le
    /// grain — pas la quantité de gain. Tube est volontairement ASYMÉTRIQUE : une courbe asymétrique
    /// engendre des harmoniques PAIRES, celles que l'oreille trouve chaleureuses, là où une courbe
    /// symétrique n'engendre que des impaires, plus dures.
    /// </summary>
    [KotonEffect("Drive", Id = "koton.drive", Category = "Drive", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class DrivePlugin : IKotonEffect
    {
        public string Id => "koton.drive";
        public string DisplayName => "Drive";

        /// <summary>Noms des caractères, dans l'ordre du paramètre <c>type</c>.</summary>
        public static readonly string[] TypeNames = { "Saturation douce", "Overdrive", "Tube (asymétrique)", "Distorsion", "Fuzz", "Wavefolder" };

        readonly KotonParameter _type = new KotonParameter("type", "Caractère", 0.0, 5.0, 1.0) { Automatable = false };
        readonly KotonParameter _drive = new KotonParameter("drive", "Drive", 0.0, 40.0, 14.0, "dB");
        readonly KotonParameter _bass = new KotonParameter("bass_cut", "Coupe-bas avant saturation", 20.0, 600.0, 90.0, "Hz");
        readonly KotonParameter _tone = new KotonParameter("tone", "Tonalité", 0.0, 1.0, 0.60);
        readonly KotonParameter _bias = new KotonParameter("bias", "Asymétrie", 0.0, 1.0, 0.25);
        readonly KotonParameter _mix = new KotonParameter("mix", "Mix", 0.0, 1.0, 1.0);
        readonly KotonParameter _level = new KotonParameter("level", "Niveau", -30.0, 12.0, 0.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        /// <summary>Facteur de suréchantillonnage. 4× suffit à repousser le repliement sous le seuil
        /// d'audibilité pour des courbes douces à moyennement dures, sans le coût d'un 8×.</summary>
        const int Os = 4;

        int _sr = 48000;
        float _hpL, _hpR;                 // état du coupe-bas (1 pôle) avant saturation
        float _toneL, _toneR;             // état de la tonalité (1 pôle) après saturation
        readonly Biquad[] _upL = new Biquad[2], _upR = new Biquad[2];
        readonly Biquad[] _downL = new Biquad[2], _downR = new Biquad[2];

        public DrivePlugin()
        {
            _params = new List<KotonParameter> { _type, _drive, _bass, _tone, _bias, _mix, _level };
            for (int i = 0; i < 2; i++)
            {
                _upL[i] = new Biquad(); _upR[i] = new Biquad();
                _downL[i] = new Biquad(); _downR[i] = new Biquad();
            }
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new DriveEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            // Filtres anti-repliement à 0,45 × la fréquence d'origine, dans le domaine suréchantillonné.
            double cut = sampleRate * 0.45;
            for (int i = 0; i < 2; i++)
            {
                // Deux biquads de Q différents = un Butterworth d'ordre 4 (pente de 24 dB/octave).
                double q = i == 0 ? 0.5412 : 1.3066;
                _upL[i].SetLowPass(sampleRate * Os, cut, q);
                _upR[i].SetLowPass(sampleRate * Os, cut, q);
                _downL[i].SetLowPass(sampleRate * Os, cut, q);
                _downR[i].SetLowPass(sampleRate * Os, cut, q);
            }
            Reset();
        }

        public void Reset()
        {
            _hpL = _hpR = _toneL = _toneR = 0f;
            for (int i = 0; i < 2; i++) { _upL[i].Reset(); _upR[i].Reset(); _downL[i].Reset(); _downR[i].Reset(); }
        }

        public void Process(Span<float> left, Span<float> right)
        {
            int type = (int)Math.Round(_type.Value);
            float gain = (float)Math.Pow(10.0, _drive.Value / 20.0);
            float bias = (float)_bias.Value;
            float mix = (float)_mix.Value;
            float outLin = (float)Math.Pow(10.0, _level.Value / 20.0);

            float hpCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * _bass.Value / _sr);
            float toneHz = (float)(800.0 + _tone.Value * _tone.Value * 11000.0);
            float toneCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * Math.Min(toneHz, _sr * 0.45) / _sr);

            // Compensation de niveau : plus on pousse le gain, plus la courbe écrête, donc plus le niveau
            // monterait. On rend l'audition du réglage « à volume constant », ce qui est la seule façon
            // d'entendre le GRAIN plutôt que le volume.
            float comp = 1f / (1f + 0.7f * (gain - 1f) / (1f + 0.25f * (gain - 1f)));

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float dryL = left[i], dryR = right[i];

                // Coupe-bas AVANT saturation (passe-haut 1 pôle par soustraction du passe-bas).
                _hpL += hpCoef * (dryL - _hpL);
                _hpR += hpCoef * (dryR - _hpR);
                float preL = dryL - _hpL, preR = dryR - _hpR;

                float sumL = 0f, sumR = 0f;
                for (int k = 0; k < Os; k++)
                {
                    // Montée en fréquence : insertion de zéros puis filtrage (le gain Os compense l'énergie
                    // perdue par les zéros).
                    float uL = k == 0 ? preL * Os : 0f;
                    float uR = k == 0 ? preR * Os : 0f;
                    uL = _upL[1].Process(_upL[0].Process(uL));
                    uR = _upR[1].Process(_upR[0].Process(uR));

                    uL = Shape(type, uL * gain, bias);
                    uR = Shape(type, uR * gain, bias);

                    // Descente : on refiltre puis on ne garde qu'un échantillon sur Os.
                    uL = _downL[1].Process(_downL[0].Process(uL));
                    uR = _downR[1].Process(_downR[0].Process(uR));
                    if (k == Os - 1) { sumL = uL; sumR = uR; }
                }

                // Tonalité après saturation.
                _toneL += toneCoef * (sumL - _toneL);
                _toneR += toneCoef * (sumR - _toneR);
                float wetL = _toneL * comp, wetR = _toneR * comp;

                left[i] = (dryL * (1f - mix) + wetL * mix) * outLin;
                right[i] = (dryR * (1f - mix) + wetR * mix) * outLin;
            }
        }

        /// <summary>Les courbes de transfert. C'est ici, et pas dans la quantité de gain, que se joue le
        /// caractère de chaque type.</summary>
        static float Shape(int type, float x, float bias)
        {
            switch (type)
            {
                case 0: // Saturation douce : compression progressive, pas de coude franc.
                    return (float)Math.Tanh(x * 0.7);

                case 1: // Overdrive : la courbe cubique classique, linéaire au centre puis genou net.
                    {
                        float a = Math.Abs(x);
                        if (a < 1f / 3f) return 2f * x;
                        if (a < 2f / 3f)
                        {
                            float t = 2f - 3f * a;
                            return Math.Sign(x) * (3f - t * t) / 3f;
                        }
                        return Math.Sign(x);
                    }

                case 2: // Tube : asymétrie volontaire -> harmoniques PAIRES, le côté « chaud ».
                    {
                        float shifted = x + bias * 0.5f;
                        float y = shifted >= 0
                            ? (float)Math.Tanh(shifted * 0.8)
                            : (float)Math.Tanh(shifted * 1.4);   // l'alternance négative écrête plus tôt
                        return y - (float)Math.Tanh(bias * 0.5 * 0.8);   // retire la composante continue
                    }

                case 3: // Distorsion : écrêtage dur mais adouci juste avant le seuil.
                    {
                        float a = Math.Abs(x);
                        float y = a < 0.7f ? x : Math.Sign(x) * (0.7f + (a - 0.7f) / (1f + (a - 0.7f) * 4f));
                        return Math.Max(-1f, Math.Min(1f, y));
                    }

                case 4: // Fuzz : écrêtage franc + un soupçon de redressement (très riche, très sale).
                    {
                        float y = Math.Max(-1f, Math.Min(1f, x));
                        return y * 0.85f + Math.Abs(y) * 0.15f - 0.075f;
                    }

                default: // Wavefolder : au lieu d'écrêter, la courbe se replie — métallique, très typé.
                    {
                        // Forme CLOSE du repliement triangulaire. Une boucle de repliements successifs
                        // demanderait autant d'itérations que le gain est grand (à +40 dB, quatre passes
                        // laissent encore sortir des valeurs à 10), alors que cette expression est exacte
                        // et bornée à [-1, 1] pour n'importe quelle entrée, à coût constant.
                        double t = (x + 1.0) * 0.25;
                        return (float)(4.0 * Math.Abs(t - Math.Floor(t + 0.5)) - 1.0);
                    }
            }
        }

        /// <summary>Biquad passe-bas (formules RBJ), pour les filtres de suréchantillonnage.</summary>
        sealed class Biquad
        {
            double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

            public void SetLowPass(int sr, double freq, double q)
            {
                double w0 = 2.0 * Math.PI * Math.Min(freq, sr * 0.49) / sr;
                double alpha = Math.Sin(w0) / (2.0 * q);
                double cos = Math.Cos(w0);
                double a0 = 1.0 + alpha;
                _b0 = (1.0 - cos) / 2.0 / a0;
                _b1 = (1.0 - cos) / a0;
                _b2 = _b0;
                _a1 = -2.0 * cos / a0;
                _a2 = (1.0 - alpha) / a0;
            }

            public void Reset() { _x1 = _x2 = _y1 = _y2 = 0; }

            public float Process(float x)
            {
                double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
                return (float)y;
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
            { "Chaleur (à peine)", "Blues crunch", "Ampli poussé", "Grosse distorsion", "Fuzz vintage", "Métal replié", "Voix saturée" };

        static readonly double[,] PresetValues = {
            //                     type drive bass  tone  bias  mix   level
            /* Chaleur         */ { 0,   6,   60,   0.75, 0.15, 0.80,  0.0 },
            /* Blues crunch    */ { 2,  14,   90,   0.60, 0.30, 1.00,  0.0 },
            /* Ampli pousse    */ { 1,  22,  120,   0.55, 0.20, 1.00, -1.0 },
            /* Grosse disto    */ { 3,  30,  150,   0.50, 0.10, 1.00, -3.0 },
            /* Fuzz vintage    */ { 4,  34,  180,   0.45, 0.00, 1.00, -5.0 },
            /* Metal replie    */ { 5,  18,  200,   0.65, 0.00, 0.90, -4.0 },
            /* Voix saturee    */ { 0,  10,  120,   0.70, 0.20, 0.55, -1.0 },
        };

        public void LoadPreset(int idx)
        {
            if (idx < 0 || idx >= PresetValues.GetLength(0)) return;
            _type.Value = PresetValues[idx, 0];
            _drive.Value = PresetValues[idx, 1];
            _bass.Value = PresetValues[idx, 2];
            _tone.Value = PresetValues[idx, 3];
            _bias.Value = PresetValues[idx, 4];
            _mix.Value = PresetValues[idx, 5];
            _level.Value = PresetValues[idx, 6];
        }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
