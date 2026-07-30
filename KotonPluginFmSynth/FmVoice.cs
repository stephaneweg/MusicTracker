using System;

namespace KotonPluginFmSynth
{
    /// <summary>
    /// Une voix polyphonique du synthé FM 2-opérateurs (carrier + modulator). Formule classique :
    /// <c>out(t) = A(t) * sin(2π·fc·t + I·sin(2π·fm·t))</c> où :
    /// - <c>fc</c> = fréquence de la note (Hz)
    /// - <c>fm</c> = ratio × fc (paramètre "Ratio" du plugin)
    /// - <c>I</c> = index de modulation (paramètre "Index"), éventuellement modulé par un LFO
    /// - <c>A(t)</c> = enveloppe ADSR
    ///
    /// **Vibrato-ish** : quand <c>LfoDepth &gt; 0</c>, l'index effectif est
    /// <c>I·(1 + depth·sin(2π·flfo·t))</c>. C'est un tremolo de spectre plutôt qu'un vrai vibrato de
    /// hauteur, mais l'effet est très agréable sur un patch cloche ou une pointe brass.
    ///
    /// **Enveloppe** : ADSR linéaire (attack ramp, decay expo simple approximé par ramp linéaire vers
    /// sustain, sustain flat, release ramp). L'expo réel améliorerait légèrement la naturalité mais
    /// le coût CPU d'un exp par sample est disproportionné pour un plugin de démo.
    ///
    /// **Phase continue** : les accumulateurs de phase (_phaseC, _phaseM, _phaseLfo) ne sont jamais
    /// réinitialisés sauf sur <see cref="Reset"/> — deux notes qui se croisent gardent des phases
    /// désynchronisées, ce qui rend le son moins "identique" et plus naturel. Le NoteOn met à zéro
    /// UNIQUEMENT le compteur d'enveloppe.
    ///
    /// **Politique de crash** : la voix ne jette jamais. Un paramètre absurde (ratio = 0 en runtime)
    /// donne juste une sortie plate — le plugin globale reste playable.
    /// </summary>
    internal sealed class FmVoice
    {
        // ---- état déclenchement ----
        public bool Active;
        public int Note;            // MIDI 0-127
        public float BaseFreq;      // Hz (dérivé de la note à NoteOn)
        public float Velocity;      // 0..1

        // ---- accumulateurs de phase (0..2π), en radians, incrémentés à chaque sample ----
        double _phaseC;
        double _phaseM;
        double _phaseLfo;

        // ---- enveloppe ADSR ----
        enum Stage { Idle, Attack, Decay, Sustain, Release }
        Stage _stage = Stage.Idle;
        float _env;                 // niveau actuel de l'enveloppe (0..1)

        readonly int _sampleRate;

        public FmVoice(int sampleRate)
        {
            _sampleRate = sampleRate;
        }

        /// <summary>Déclenche la voix sur une note MIDI. Ne réinitialise PAS la phase (naturalité) ni le
        /// LFO (partagé conceptuellement entre voix mais chaque voix a son propre accumulateur pour
        /// éviter les glitchs quand plusieurs notes démarrent au même buffer).</summary>
        public void NoteOn(int note, int velocity)
        {
            Note = note;
            BaseFreq = (float)(440.0 * Math.Pow(2, (note - 69) / 12.0));
            Velocity = Math.Max(1, Math.Min(127, velocity)) / 127f;
            Active = true;
            _env = 0f;
            _stage = Stage.Attack;
        }

        /// <summary>Passe la voix en release. Une voix déjà en release (double note-off, ou release
        /// avant que la voix soit encore audible) est ignorée silencieusement.</summary>
        public void NoteOff()
        {
            if (_stage == Stage.Idle || _stage == Stage.Release) return;
            _stage = Stage.Release;
        }

        public void Reset()
        {
            Active = false;
            _stage = Stage.Idle;
            _env = 0f;
            _phaseC = _phaseM = _phaseLfo = 0;
        }

        /// <summary>Rend un sample mono. Le buffer stéréo est peuplé au niveau du plugin (les 2 canaux
        /// reçoivent le même signal — pas de spatialisation par voix v1). <paramref name="bendMul"/>
        /// est un multiplicateur de fréquence (pitch bend global appliqué à la carrier).</summary>
        public float RenderSample(float ratio, float index, float lfoRate, float lfoDepth,
                                  float attackSec, float decaySec, float sustainLvl, float releaseSec,
                                  float bendMul)
        {
            if (!Active) return 0f;

            // ---- Enveloppe ----
            // Deltas par sample : dt = 1 / (durée en secondes × sampleRate). Un temps très court est
            // clampé à 1 sample (dt = 1) pour éviter une div-par-zéro et un pop.
            switch (_stage)
            {
                case Stage.Attack:
                {
                    float dt = attackSec <= 0 ? 1f : (1f / (attackSec * _sampleRate));
                    _env += dt;
                    if (_env >= 1f) { _env = 1f; _stage = Stage.Decay; }
                    break;
                }
                case Stage.Decay:
                {
                    float target = sustainLvl;
                    float dt = decaySec <= 0 ? 1f : ((1f - target) / (decaySec * _sampleRate));
                    _env -= dt;
                    if (_env <= target) { _env = target; _stage = Stage.Sustain; }
                    break;
                }
                case Stage.Sustain:
                    _env = sustainLvl;
                    break;
                case Stage.Release:
                {
                    float dt = releaseSec <= 0 ? 1f : (_env / (releaseSec * _sampleRate));
                    _env -= dt;
                    if (_env <= 0f) { _env = 0f; _stage = Stage.Idle; Active = false; return 0f; }
                    break;
                }
                default:
                    return 0f;
            }

            // ---- LFO sur l'index (vibrato-ish, tremolo de spectre) ----
            _phaseLfo += 2 * Math.PI * lfoRate / _sampleRate;
            if (_phaseLfo > 2 * Math.PI) _phaseLfo -= 2 * Math.PI;
            double lfoMod = 1.0 + lfoDepth * Math.Sin(_phaseLfo);
            double effIndex = index * lfoMod;

            // ---- Synthèse FM ----
            double fc = BaseFreq * bendMul;
            double fm = fc * ratio;
            _phaseM += 2 * Math.PI * fm / _sampleRate;
            _phaseC += 2 * Math.PI * fc / _sampleRate;
            if (_phaseM > 2 * Math.PI) _phaseM -= 2 * Math.PI;
            if (_phaseC > 2 * Math.PI) _phaseC -= 2 * Math.PI;

            double mod = effIndex * Math.Sin(_phaseM);
            double s = Math.Sin(_phaseC + mod);

            return (float)(s * _env * Velocity);
        }
    }
}
