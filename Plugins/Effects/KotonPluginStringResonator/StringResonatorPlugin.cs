using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;
using KotonStudio.Plugins.Shared;

namespace KotonPluginStringResonator
{
    /// <summary>
    /// String Resonator — une corde de Karplus-Strong EXCITÉE PAR L'ENTRÉE, accordée en continu sur la
    /// hauteur du signal.
    ///
    /// **L'idée** : au lieu de détecter des notes pour les rejouer sur un instrument, on garde le signal et
    /// on lui donne un corps. La voix (ou l'archet) sert d'excitation à une corde numérique dont la longueur
    /// suit la fondamentale de ce qu'on joue. Résultat : le vibrato, les glissandos, les attaques et les
    /// nuances passent intacts — ce ne sont plus des notes, c'est le même geste avec un autre timbre.
    ///
    /// **DSP** : ligne à retard de longueur <c>sr / f</c> (lecture fractionnaire interpolée, sans quoi la
    /// hauteur avancerait par marches), boucle de rétroaction avec passe-bas 1 pôle — c'est exactement
    /// Karplus-Strong, à ceci près que l'excitation n'est pas une bouffée de bruit mais l'entrée, injectée
    /// en permanence. L'injection est pondérée par <c>1 - rétroaction</c> : à la résonance, un peigne de
    /// gain <c>1/(1-r)</c> exploserait sinon dès que la décroissance est longue. Une saturation douce dans
    /// la boucle borne le tout en dernier recours.
    ///
    /// **Deux cordes** : la seconde est désaccordée de <c>Spread</c> cents et envoyée à droite (la première
    /// à gauche) — largeur stéréo et battement naturel, comme deux cordes d'un même chœur.
    ///
    /// **Accord** : « suivi » (la corde suit l'entrée) ou « fixe » (la corde a sa propre note, l'entrée ne
    /// fournit plus que le rythme et la couleur — un bourdon dans lequel on chante).
    /// </summary>
    [KotonEffect("String Resonator", Id = "koton.stringresonator", Category = "Instrument", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class StringResonatorPlugin : IKotonEffect
    {
        public string Id => "koton.stringresonator";
        public string DisplayName => "String Resonator";

        readonly KotonParameter _tuneMode = new KotonParameter("tune_mode", "Accord (0 = suivi, 1 = fixe)", 0.0, 1.0, 0.0) { Automatable = false };
        readonly KotonParameter _fixedNote = new KotonParameter("fixed_note", "Note fixe", 24.0, 96.0, 55.0);
        readonly KotonParameter _interval = new KotonParameter("interval", "Intervalle", -24.0, 24.0, 0.0, "demi-tons");
        readonly KotonParameter _decay = new KotonParameter("decay", "Décroissance", 0.0, 1.0, 0.65);
        readonly KotonParameter _tone = new KotonParameter("tone", "Brillance", 0.0, 1.0, 0.55);
        readonly KotonParameter _drive = new KotonParameter("drive", "Excitation", 0.0, 1.0, 0.70);
        readonly KotonParameter _spread = new KotonParameter("spread", "Désaccord", 0.0, 30.0, 6.0, "cents");
        readonly KotonParameter _lowNote = new KotonParameter("low_note", "Note la plus grave", 24.0, 72.0, 43.0);
        readonly KotonParameter _maxLeap = new KotonParameter("max_leap", "Écart max", 0.0, 36.0, 12.0, "demi-tons");
        readonly KotonParameter _octaveGuard = new KotonParameter("octave_guard", "Anti-sous-harmonique", 0.0, 1.0, 0.80);
        readonly KotonParameter _mix = new KotonParameter("mix", "Mix", 0.0, 1.0, 0.75);
        readonly KotonParameter _outGain = new KotonParameter("out_gain", "Sortie", -30.0, 12.0, 0.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        readonly KotonPitchTracker _tracker = new KotonPitchTracker();

        int _sr = 48000, _size;
        float[] _lineA, _lineB;
        int _idxA, _idxB;
        float _lpA, _lpB;

        public StringResonatorPlugin()
        {
            _params = new List<KotonParameter>
            {
                _tuneMode, _fixedNote, _interval, _decay, _tone, _drive, _spread,
                _lowNote, _maxLeap, _octaveGuard, _mix, _outGain
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new StringResonatorEditor(this);

        /// <summary>Fréquence suivie, en Hz — lue par l'éditeur pour afficher un témoin d'accroche.</summary>
        public double TrackedFrequency => _tracker.Frequency;
        /// <summary>Vrai quand le suiveur est accroché sur une hauteur claire.</summary>
        public bool Locked => _tracker.Voiced;

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            // Assez long pour la note la plus grave possible du sélecteur (Do1, ~32 Hz) avec de la marge.
            _size = Math.Max(1024, sampleRate / 25);
            _lineA = new float[_size];
            _lineB = new float[_size];
            // Fenêtre de 2048 : il faut au moins deux périodes pour reconnaître une période, et 2048 à
            // 48 kHz en couvre deux jusqu'à ~47 Hz. Analyse tous les 512 échantillons (~11 ms).
            _tracker.Prepare(sampleRate, 2048, 512);
            Reset();
        }

        public void Reset()
        {
            if (_lineA != null) { Array.Clear(_lineA, 0, _lineA.Length); Array.Clear(_lineB, 0, _lineB.Length); }
            _idxA = _idxB = 0;
            _lpA = _lpB = 0f;
            _tracker.Reset();
        }

        public void Process(Span<float> left, Span<float> right)
        {
            if (_lineA == null) return;

            bool fixedTune = _tuneMode.Value >= 0.5;
            double interval = Math.Pow(2.0, _interval.Value / 12.0);
            double fixedHz = 440.0 * Math.Pow(2.0, (_fixedNote.Value - 69.0) / 12.0);
            // Décroissance 0..1 -> rétroaction 0,90..0,9995 (courbe pour que le haut du curseur soit fin).
            float fb = (float)(0.90 + 0.0995 * Math.Pow(_decay.Value, 0.5));
            float toneHz = (float)(700.0 + _tone.Value * _tone.Value * 11000.0);
            float lpCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * Math.Min(toneHz, _sr * 0.45) / _sr);
            float drive = (float)_drive.Value;
            double detune = Math.Pow(2.0, _spread.Value / 1200.0);
            float mix = (float)_mix.Value;
            float outLin = (float)Math.Pow(10.0, _outGain.Value / 20.0);

            _tracker.MinFrequency = 440.0 * Math.Pow(2.0, (_lowNote.Value - 69.0) / 12.0) * Math.Pow(2, -0.5 / 12);
            _tracker.MaxFrequency = Math.Min(2000.0, _sr * 0.25);
            _tracker.MaxLeapSemitones = _maxLeap.Value;
            _tracker.SubHarmonicGuard = _octaveGuard.Value;

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float dry = 0.5f * (left[i] + right[i]);

                // Le suiveur consomme TOUJOURS l'entrée, même en accord fixe : le témoin de l'éditeur
                // reste vivant et le passage suivi/fixe se fait sans réaccrochage.
                double tracked = _tracker.Push(dry);
                double f = fixedTune ? fixedHz : (tracked > 0 ? tracked : 0);
                // Rien d'accroché (silence, ou hauteur pas encore trouvée) : on laisse passer le sec seul.
                if (f <= 0) { float d = dry * (1f - mix) * outLin; left[i] = d; right[i] = d; continue; }
                f *= interval;

                float wetA = RunString(_lineA, ref _idxA, ref _lpA, f, dry, fb, lpCoef, drive);
                float wetB = _spread.Value > 0.01
                    ? RunString(_lineB, ref _idxB, ref _lpB, f * detune, dry, fb, lpCoef, drive)
                    : wetA;

                left[i] = (dry * (1f - mix) + wetA * mix) * outLin;
                right[i] = (dry * (1f - mix) + wetB * mix) * outLin;
            }
        }

        /// <summary>Un passage de corde : lecture fractionnaire, amortissement, rétroaction, réinjection.</summary>
        float RunString(float[] line, ref int idx, ref float lpState, double freq, float excite,
                        float feedback, float lpCoef, float drive)
        {
            double delay = _sr / freq;
            if (delay < 2) delay = 2;
            if (delay > _size - 2) delay = _size - 2;

            double readPos = idx - delay;
            while (readPos < 0) readPos += _size;
            int i0 = (int)readPos;
            int i1 = i0 + 1; if (i1 >= _size) i1 = 0;
            float frac = (float)(readPos - i0);
            float outSample = line[i0] * (1f - frac) + line[i1] * frac;

            // Amortissement : un passe-bas dans la boucle, c'est ce qui fait qu'une corde perd ses aigus
            // avant ses graves — sans lui le son est métallique et interminable.
            lpState += lpCoef * (outSample - lpState);

            // L'excitation est pondérée par (1 - rétroaction) : à la résonance le peigne a un gain de
            // 1/(1-r), donc sans cette compensation une décroissance longue ferait exploser le niveau.
            float loop = lpState * feedback + excite * drive * (1f - feedback) * 8f;

            // Saturation douce : filet de sécurité si l'entrée est très forte ou la rétroaction extrême.
            if (loop > 1f || loop < -1f) loop = (float)Math.Tanh(loop);

            line[idx] = loop;
            idx++; if (idx >= _size) idx = 0;
            return outSample;
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
            { "Corde nue", "Guitare fantôme", "Harpe", "Basse sub", "Cithare métallique", "Bourdon (fixe)" };

        static readonly double[,] PresetValues = {
            //                 mode note  interv decay tone  drive spread low  leap guard mix   out
            /* Corde nue   */ { 0,   55,   0,    0.55, 0.55, 0.70,  6,    43,  12,  0.80, 0.70,  0.0 },
            /* Guitare     */ { 0,   55,   0,    0.75, 0.45, 0.60, 10,    43,  12,  0.80, 0.80,  0.0 },
            /* Harpe       */ { 0,   55,  12,    0.80, 0.65, 0.55,  4,    43,  12,  0.80, 0.85, -1.0 },
            /* Basse sub   */ { 0,   55, -12,    0.85, 0.25, 0.80,  3,    36,  12,  0.70, 0.85, -2.0 },
            /* Cithare     */ { 0,   55,   0,    0.92, 0.90, 0.65, 18,    43,  12,  0.80, 0.90, -3.0 },
            /* Bourdon     */ { 1,   43,   0,    0.95, 0.50, 0.75, 12,    43,  24,  0.80, 0.80, -2.0 },
        };

        public void LoadPreset(int idx)
        {
            if (idx < 0 || idx >= PresetValues.GetLength(0)) return;
            _tuneMode.Value = PresetValues[idx, 0];
            _fixedNote.Value = PresetValues[idx, 1];
            _interval.Value = PresetValues[idx, 2];
            _decay.Value = PresetValues[idx, 3];
            _tone.Value = PresetValues[idx, 4];
            _drive.Value = PresetValues[idx, 5];
            _spread.Value = PresetValues[idx, 6];
            _lowNote.Value = PresetValues[idx, 7];
            _maxLeap.Value = PresetValues[idx, 8];
            _octaveGuard.Value = PresetValues[idx, 9];
            _mix.Value = PresetValues[idx, 10];
            _outGain.Value = PresetValues[idx, 11];
        }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
