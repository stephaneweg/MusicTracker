using System;

namespace KotonPluginWaveMorph
{
    /// <summary>
    /// Enveloppe ADSR linéaire — même modèle que <c>KotonPluginFmSynth.FmVoice</c> pour la stabilité
    /// de comportement (attack ramp / decay ramp vers sustain / sustain flat / release ramp), extraite
    /// ici en classe autonome pour être partagée entre l'Amp env, l'Env 2, et l'Env 3 du plugin.
    ///
    /// **Coût CPU** : une addition par sample + un test de stage. L'exponentielle "vraie" (exp par
    /// sample) est plus naturelle mais dispendieuse ; le ramp linéaire donne un résultat parfaitement
    /// acceptable dans un contexte polyphonique (8-16 voix × 3 envs = 48 envs).
    ///
    /// **Retrigger** : chaque NoteOn remet le niveau à 0 et repart en Attack. Un NoteOff pendant
    /// Attack ou Decay passe direct en Release (avec le niveau courant comme point de départ), ce qui
    /// évite un pop au lieu de forcer une transition instantanée.
    /// </summary>
    internal sealed class Envelope
    {
        enum Stage { Idle, Attack, Decay, Sustain, Release }

        Stage _stage = Stage.Idle;
        float _level;
        readonly int _sampleRate;

        public Envelope(int sampleRate)
        {
            _sampleRate = sampleRate > 0 ? sampleRate : 44100;
        }

        /// <summary>Niveau courant de l'enveloppe dans [0, 1]. Lu par le plugin pour appliquer sur
        /// l'amp ou pour alimenter la mod matrix.</summary>
        public float Level => _level;

        /// <summary>Vrai tant que l'enveloppe n'est pas Idle — c'est-à-dire tant qu'elle produit un
        /// niveau non nul (ou en train de descendre vers zéro). Une voix devient inactive quand toutes
        /// ses enveloppes utilisées sont Idle (typiquement l'Amp env qui détermine la mort de la voix).</summary>
        public bool IsActive => _stage != Stage.Idle;

        public void NoteOn()
        {
            _level = 0f;
            _stage = Stage.Attack;
        }

        public void NoteOff()
        {
            // Pas de reset du niveau — on part du niveau courant vers zéro en releaseSec. Un NoteOff
            // pendant Attack/Decay entend un release naturel depuis là où on était.
            if (_stage != Stage.Idle) _stage = Stage.Release;
        }

        public void Reset()
        {
            _level = 0f;
            _stage = Stage.Idle;
        }

        /// <summary>Avance l'enveloppe d'un sample. Retourne le niveau courant (dans [0, 1]) pour usage
        /// direct par l'appelant qui vient de faire tourner un sample.</summary>
        public float Advance(float attackSec, float decaySec, float sustainLvl, float releaseSec)
        {
            switch (_stage)
            {
                case Stage.Attack:
                {
                    float dt = attackSec <= 0 ? 1f : (1f / (attackSec * _sampleRate));
                    _level += dt;
                    if (_level >= 1f) { _level = 1f; _stage = Stage.Decay; }
                    break;
                }
                case Stage.Decay:
                {
                    float target = sustainLvl;
                    // Vitesse basée sur la distance normalisée (1→sustain) pour que decaySec soit le
                    // temps de traversée complet, indépendant du sustainLvl.
                    float dt = decaySec <= 0 ? 1f : ((1f - target) / (decaySec * _sampleRate));
                    _level -= dt;
                    if (_level <= target) { _level = target; _stage = Stage.Sustain; }
                    break;
                }
                case Stage.Sustain:
                    _level = sustainLvl;
                    break;
                case Stage.Release:
                {
                    float dt = releaseSec <= 0 ? 1f : (_level / (releaseSec * _sampleRate));
                    _level -= dt;
                    if (_level <= 0f) { _level = 0f; _stage = Stage.Idle; }
                    break;
                }
                default:
                    _level = 0f;
                    break;
            }
            return _level;
        }
    }
}
