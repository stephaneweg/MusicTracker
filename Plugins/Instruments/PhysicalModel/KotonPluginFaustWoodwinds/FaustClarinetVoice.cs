using System;

namespace KotonPluginFaustWoodwinds
{
    /// <summary>
    /// Port fidele du modele FAUST physmodels.lib clarinetModel (Grame CNCM Lyon, licence
    /// permissive). Reference : https://github.com/grame-cncm/faustlibraries/blob/master/physmodels.lib
    ///
    /// Structure FAUST :
    ///   clarinetModel(tubeLength, pressure, reedStiffness, bellOpening) = chain(
    ///     clarinetMouthPiece(reedStiffness, pressure) :
    ///     openTube(maxTubeLength, tubeLength/2 - 0.05) :
    ///     wBell(bellOpening) : out
    ///   )
    ///
    /// Waveguide bidirectionnel = 2 lignes a retard separees pour l'onde ALLER (mouth→bell)
    /// et l'onde RETOUR (bell→mouth), chaque delay = n = l2s(tunedLength)/2 = sr/(4*f0).
    ///
    /// Reed table FAUST : rt = 0.7 + slope*pDiff, clip ±1, slope = -0.44 + 0.26*stiffness.
    /// (Different de STK Perry Cook : slope 0.3 fixe. Le -0.44 negatif = anche qui ferme.)
    ///
    /// Termination bell : basicBlock + reflexion smooth(-opening). basicBlock est un simple
    /// passthrough dans le cas de wBell (pas de filtre supplementaire).
    /// </summary>
    internal sealed class FaustClarinetVoice
    {
        readonly int _sr;
        // Deux delays du waveguide bidirectionnel : leftGoing = onde qui remonte vers l'anche,
        // rightGoing = onde qui descend vers le pavillon.
        readonly DelayL _leftDelay, _rightDelay;

        // Pression bouche (envelope ramp) FAUST-style : lineaire vers target
        float _envValue, _envTarget, _envRate;
        bool _releasing;

        // Reed slope calcule au NoteOn depuis stiffness
        float _reedSlope;

        // Bell opening smooth (FAUST si.smooth = 1-pole LP a coefficient fixe ~0.999)
        float _bellOpening;
        float _bellSmoothState;

        bool _active;
        int _note;
        float _velocity;

        Random _rng;

        public bool IsActive => _active;
        public int Note => _note;

        public FaustClarinetVoice(int sampleRate)
        {
            _sr = sampleRate;
            int maxDelay = sampleRate / 20 + 8;   // support down to 20 Hz
            _leftDelay = new DelayL(maxDelay);
            _rightDelay = new DelayL(maxDelay);
            _rng = new Random();
        }

        public void NoteOn(int note, float velocity, float airPressure, float reedStiffness, float bellOpening, float attackSec)
        {
            _note = note;
            _velocity = velocity;

            double f0 = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            // Delay par sens du waveguide : n = sr/(4*f0) (equivalent l2s(tunedLength)/2 en FAUST
            // avec tunedLength = c/(2*f0)). C'est la formule quart-d'onde du tube cylindrique
            // ferme a l'anche + ouvert au pavillon.
            float delayN = (float)(_sr / (4.0 * f0));
            if (delayN < 1f) delayN = 1f;
            _leftDelay.Clear();
            _rightDelay.Clear();
            _leftDelay.SetDelay(delayN);
            _rightDelay.SetDelay(delayN);

            // FAUST clarinetReed : tableSlope = -0.44 + 0.26 * stiffness
            _reedSlope = -0.44f + 0.26f * reedStiffness;

            _bellOpening = bellOpening;
            _bellSmoothState = bellOpening;   // pre-charge pour eviter la rampe initiale

            // Envelope : ramp lineaire vers target = airPressure*velocity, en attackSec
            _envTarget = Math.Max(0.15f, airPressure) * (0.6f + velocity * 0.4f);
            _envValue = 0f;
            _envRate = _envTarget / Math.Max(1f, attackSec * _sr);
            _releasing = false;

            _rng = new Random(note * 7919 + Environment.TickCount);
            _active = true;
        }

        public void NoteOff(float releaseSec)
        {
            _envTarget = 0f;
            _envRate = _envValue / Math.Max(1f, releaseSec * _sr);
            _releasing = true;
        }

        public void Kill()
        {
            _active = false;
            _envValue = 0f;
            _envTarget = 0f;
            _leftDelay.Clear();
            _rightDelay.Clear();
        }

        public float Tick(float noiseGain, float vibratoMul)
        {
            if (!_active) return 0f;

            // 1. Envelope pression bouche : rampe lineaire
            if (_envValue < _envTarget) { _envValue += _envRate; if (_envValue > _envTarget) _envValue = _envTarget; }
            else if (_envValue > _envTarget) { _envValue -= _envRate; if (_envValue < _envTarget) _envValue = _envTarget; }
            if (_releasing && _envValue <= 1e-6f) { _active = false; return 0f; }

            // 2. Pression = envelope + noise multiplicatif + vibrato multiplicatif
            float pressure = _envValue * vibratoMul;
            if (noiseGain > 0.001f)
            {
                float noise = (float)(_rng.NextDouble() * 2 - 1);
                pressure += pressure * noiseGain * noise;
            }

            // 3. Bell opening smooth (FAUST si.smooth ~0.999)
            _bellSmoothState += 0.001f * (_bellOpening - _bellSmoothState);
            float bellRefl = -_bellSmoothState;   // reflexion negative avec smoothing

            // === Sample-step du waveguide bidirectionnel FAUST ===
            //
            // Etat courant :
            //   xLeft  = valeur au bout du leftDelay (arrivee a l'anche)
            //   xRight = valeur au bout du rightDelay (arrivee au bell)
            //
            // Anche (clarinetMouthPiece = lTermination) :
            //   reedInteraction = xLeft * (-1) * clarinetReed(pDiff)
            //   nouvelle rightWave in = reedInteraction + pressure (in(pressure) additionne p partout)
            //
            // Bell (wBell = rTermination(basicBlock, opening)) :
            //   basicBlock = passthrough (juste _)
            //   nouvelle leftWave in = xRight * bellRefl

            // Lecture des sorties courantes
            float xLeft  = _leftDelay.LastOut();
            float xRight = _rightDelay.LastOut();

            // Interaction anche : FAUST reedInteraction = *(-1) : *(clarinetReed(stiffness))
            // clarinetReed(x) = reedTable(0.7, slope)(x) = clip(0.7 + slope*x, ±1)
            float pDiff = -xLeft;                    // *(-1)
            float rt = 0.7f + _reedSlope * pDiff;
            if (rt > 1f) rt = 1f; else if (rt < -1f) rt = -1f;
            float reedInteraction = pDiff * rt;      // *(clarinetReed(...))

            // Nouvelles ondes injectees
            float rightIn = reedInteraction + pressure;
            float leftIn = xRight * bellRefl;

            _rightDelay.Tick(rightIn);
            _leftDelay.Tick(leftIn);

            // Sortie audio FAUST : out(x, y, s) = x + y + s
            // Ici s = 0 (pas d'accumulateur), donc sortie = xLeft + xRight
            return xLeft + xRight;
        }
    }

    // ============================================================================================
    // DelayL — Linear interpolating delay line (equivalent FAUST de.fdelay4 simplifie)
    // ============================================================================================
    internal sealed class DelayL
    {
        readonly float[] _inputs;
        int _inPoint;
        float _lastFrame;

        int _outPoint;
        float _alpha, _omAlpha;
        int _size;

        public DelayL(int maxDelay)
        {
            _size = maxDelay + 1;
            _inputs = new float[_size];
        }

        public void Clear() { Array.Clear(_inputs, 0, _size); _lastFrame = 0f; _inPoint = 0; }

        public void SetDelay(float delay)
        {
            if (delay > _size - 1) delay = _size - 1;
            if (delay < 0f) delay = 0f;
            float outPointer = _inPoint - delay;
            while (outPointer < 0f) outPointer += _size;
            _outPoint = (int)outPointer;
            _alpha = outPointer - _outPoint;
            _omAlpha = 1f - _alpha;
            if (_outPoint == _size) _outPoint = 0;
        }

        public float LastOut() => _lastFrame;

        public float Tick(float input)
        {
            _inputs[_inPoint++] = input;
            if (_inPoint == _size) _inPoint = 0;

            int next = _outPoint + 1;
            if (next == _size) next = 0;
            _lastFrame = _inputs[next] * _alpha + _inputs[_outPoint] * _omAlpha;

            _outPoint++;
            if (_outPoint == _size) _outPoint = 0;
            return _lastFrame;
        }
    }
}
