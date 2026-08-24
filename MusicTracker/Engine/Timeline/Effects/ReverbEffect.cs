using System;
using System.Collections.Generic;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Réverbération maison, du type réseau de retards rebouclés (Schroeder-Moorer) : quatre filtres en
    /// peigne en parallèle construisent la densité d'échos, deux passe-tout en série les diffusent pour
    /// que cette densité cesse de s'entendre comme des répétitions distinctes.
    ///
    /// **Pourquoi une réverbe MAISON alors qu'il existe sept réverbes Koton.** Celle-ci est le moteur du
    /// BUS de réverbe du mixeur, c'est-à-dire d'une fonction de base de l'application. La faire reposer
    /// sur un plugin la rendrait dépendante de la présence d'un fichier dans <c>plugins/</c> : un dossier
    /// incomplet et le départ de réverbe de chaque piste deviendrait silencieux, sans explication. Les
    /// réverbes Koton restent le bon choix comme insert, quand on cherche un caractère.
    ///
    /// **Longueurs de peigne premières entre elles**, en nombres d'échantillons issus des valeurs
    /// classiques de Schroeder mises à l'échelle du taux d'échantillonnage. Des longueurs à facteur commun
    /// feraient coïncider leurs échos périodiquement, ce qui s'entend comme un battement métallique.
    /// Le canal droit décale légèrement ces longueurs : c'est ce qui donne une largeur stéréo sans
    /// recourir à un quelconque élargisseur.
    /// </summary>
    public sealed class ReverbEffect : IAudioEffect
    {
        public string Kind => "reverb";

        // Retards de Schroeder, en échantillons à 44,1 kHz. Mis à l'échelle si le taux diffère.
        static readonly int[] CombBase = { 1557, 1617, 1491, 1422 };
        static readonly int[] AllpassBase = { 225, 556 };
        const int StereoSpread = 23;

        readonly int _sr;
        readonly float[][] _combL, _combR, _apL, _apR;
        readonly int[] _iCombL, _iCombR, _iApL, _iApR;
        readonly float[] _lpL, _lpR;          // amortissement dans la boucle de chaque peigne

        double _size = 0.62, _damping = 0.45, _width = 0.85, _mix = 0.28, _preDelayMs = 12;
        float[] _preL, _preR;
        int _preIdx;

        public ReverbEffect(int sampleRate)
        {
            _sr = sampleRate <= 0 ? 44100 : sampleRate;
            double k = _sr / 44100.0;

            _combL = new float[CombBase.Length][]; _combR = new float[CombBase.Length][];
            _iCombL = new int[CombBase.Length]; _iCombR = new int[CombBase.Length];
            _lpL = new float[CombBase.Length]; _lpR = new float[CombBase.Length];
            for (int i = 0; i < CombBase.Length; i++)
            {
                _combL[i] = new float[(int)(CombBase[i] * k)];
                _combR[i] = new float[(int)((CombBase[i] + StereoSpread) * k)];
            }

            _apL = new float[AllpassBase.Length][]; _apR = new float[AllpassBase.Length][];
            _iApL = new int[AllpassBase.Length]; _iApR = new int[AllpassBase.Length];
            for (int i = 0; i < AllpassBase.Length; i++)
            {
                _apL[i] = new float[(int)(AllpassBase[i] * k)];
                _apR[i] = new float[(int)((AllpassBase[i] + StereoSpread) * k)];
            }

            _preL = new float[Math.Max(1, (int)(0.2 * _sr))];
            _preR = new float[_preL.Length];
        }

        public void Process(float[] left, float[] right, int frames)
        {
            // Le retour de boucle des peignes fixe la durée : 0,84 donne une petite pièce, 0,96 une nef.
            float feedback = (float)(0.70 + _size * 0.28);
            float damp = (float)(_damping * 0.55);
            float damp1 = 1f - damp;
            float wet = (float)_mix, dry = 1f - wet;
            float wide = (float)_width;
            int pre = Math.Max(0, Math.Min(_preL.Length - 1, (int)(_preDelayMs * 0.001 * _sr)));

            for (int n = 0; n < frames; n++)
            {
                float inL = left[n], inR = right[n];

                // Pré-délai : le temps que met le premier écho à revenir des murs. Sans lui, la réverbe
                // colle à la note et brouille l'attaque au lieu de la situer dans un espace.
                _preL[_preIdx] = inL; _preR[_preIdx] = inR;
                int rd = _preIdx - pre; if (rd < 0) rd += _preL.Length;
                float sL = _preL[rd], sR = _preR[rd];
                _preIdx++; if (_preIdx >= _preL.Length) _preIdx = 0;

                float input = (sL + sR) * 0.5f * 0.030f;   // niveau d'entrée du réseau, réglé pour ne pas saturer

                float accL = 0f, accR = 0f;
                for (int i = 0; i < _combL.Length; i++)
                {
                    float yL = _combL[i][_iCombL[i]];
                    accL += yL;
                    // Le passe-bas DANS la boucle est ce qui fait qu'une salle réelle perd ses aigus plus
                    // vite que ses graves : sans lui la queue reste brillante et sonne artificielle.
                    _lpL[i] = yL * damp1 + _lpL[i] * damp;
                    _combL[i][_iCombL[i]] = input + _lpL[i] * feedback;
                    if (++_iCombL[i] >= _combL[i].Length) _iCombL[i] = 0;

                    float yR = _combR[i][_iCombR[i]];
                    accR += yR;
                    _lpR[i] = yR * damp1 + _lpR[i] * damp;
                    _combR[i][_iCombR[i]] = input + _lpR[i] * feedback;
                    if (++_iCombR[i] >= _combR[i].Length) _iCombR[i] = 0;
                }

                // Passe-tout : ils ne changent pas le spectre, seulement les phases. C'est ce qui
                // transforme quatre trains d'échos audibles en une queue continue.
                for (int i = 0; i < _apL.Length; i++)
                {
                    float bufL = _apL[i][_iApL[i]];
                    float outL = -accL + bufL;
                    _apL[i][_iApL[i]] = accL + bufL * 0.5f;
                    if (++_iApL[i] >= _apL[i].Length) _iApL[i] = 0;
                    accL = outL;

                    float bufR = _apR[i][_iApR[i]];
                    float outR = -accR + bufR;
                    _apR[i][_iApR[i]] = accR + bufR * 0.5f;
                    if (++_iApR[i] >= _apR[i].Length) _iApR[i] = 0;
                    accR = outR;
                }

                float mid = (accL + accR) * 0.5f;
                float wL = mid + (accL - mid) * wide;
                float wR = mid + (accR - mid) * wide;

                left[n] = inL * dry + wL * wet;
                right[n] = inR * dry + wR * wet;
            }
        }

        public void Reset()
        {
            for (int i = 0; i < _combL.Length; i++)
            {
                Array.Clear(_combL[i], 0, _combL[i].Length);
                Array.Clear(_combR[i], 0, _combR[i].Length);
                _lpL[i] = _lpR[i] = 0f;
                _iCombL[i] = _iCombR[i] = 0;
            }
            for (int i = 0; i < _apL.Length; i++)
            {
                Array.Clear(_apL[i], 0, _apL[i].Length);
                Array.Clear(_apR[i], 0, _apR[i].Length);
                _iApL[i] = _iApR[i] = 0;
            }
            Array.Clear(_preL, 0, _preL.Length);
            Array.Clear(_preR, 0, _preR.Length);
            _preIdx = 0;
        }

        public Dictionary<string, double> Save() => new Dictionary<string, double>
        {
            { "size", _size }, { "damping", _damping }, { "width", _width },
            { "mix", _mix }, { "predelay", _preDelayMs },
        };

        public void Load(Dictionary<string, double> d)
        {
            if (d == null) return;
            if (d.TryGetValue("size", out var v)) _size = Clamp(v, 0, 1);
            if (d.TryGetValue("damping", out v)) _damping = Clamp(v, 0, 1);
            if (d.TryGetValue("width", out v)) _width = Clamp(v, 0, 1);
            if (d.TryGetValue("mix", out v)) _mix = Clamp(v, 0, 1);
            if (d.TryGetValue("predelay", out v)) _preDelayMs = Clamp(v, 0, 200);
        }

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        public string SaveState() => null;
        public void LoadState(string state) { }
    }
}
