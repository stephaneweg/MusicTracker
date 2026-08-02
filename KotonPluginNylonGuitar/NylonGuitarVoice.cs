using System;

namespace KotonPluginNylonGuitar
{
    internal struct NgParams
    {
        public float PluckSoftness;    // 0..1 - 0 = ongle dur, 1 = pulpe molle (attaque plus douce)
        public float PluckPosition;    // 0.05..0.4 - position de pincement (1/4 = classique, 1/6 = flamenco)
        public float Sustain;          // 0..1 - durée de la note (0.995..0.9995 en feedback)
        public float Brightness;       // 0..1 - couleur du filtre feedback
        public float Stiffness;        // 0..1 - all-pass dispersion (piano feel discret)
        public float VolumeDb;
    }

    /// <summary>
    /// Voix de guitare classique nylon. Karplus-Strong étendu avec :
    /// - Excitation nylon = bruit LP filtré (spectre roulé, pas piqué comme un médiator) + comb
    ///   filter selon la position de pincement (supprime les partiels ayant un nœud là où on pince
    ///   — physique réelle de la corde)
    /// - Feedback avec LP variable (Brightness) et all-pass léger (Stiffness) pour un mordant
    ///   caractéristique nylon plutôt que corde acier
    /// - Body resonance = 2 peak biquads en série appliqués EN SORTIE (200 Hz + 800 Hz), qui
    ///   modélisent les deux modes principaux d'une caisse de guitare classique
    /// - Damping global très doux (0.9975..0.9995 selon Sustain) — le nylon a un sustain long
    ///   caractéristique
    /// </summary>
    internal sealed class NylonGuitarVoice
    {
        readonly int _sampleRate;
        readonly float[] _buffer;
        int _writeIdx;
        int _size;

        float _lpPrev;
        float _tonePrev;
        float _apPrevIn, _apPrevOut;

        bool _active;
        int _note;
        float _velocity;
        float _panL, _panR;

        float _peakEnvelope;
        const float SilenceThreshold = 1e-5f;

        public bool IsActive => _active;
        public int Note => _note;
        public float PanL => _panL;
        public float PanR => _panR;

        public NylonGuitarVoice(int sampleRate)
        {
            _sampleRate = sampleRate;
            _buffer = new float[Math.Max(sampleRate / 20, 4096)];
        }

        public void NoteOn(int note, float velocity, in NgParams p, float pan)
        {
            _note = note;
            _velocity = velocity;

            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _size = Math.Max(4, Math.Min(_buffer.Length, (int)Math.Round(_sampleRate / freq)));

            // Excitation nylon : bruit blanc, filtré LP proportionnel à PluckSoftness
            // Soft = pulpe molle = LP plus fort = attaque douce
            var rng = new Random(note * 7919 + Environment.TickCount);
            for (int i = 0; i < _size; i++)
                _buffer[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

            // Comb filter selon position de pincement (supprime les partiels ayant un nœud là)
            int np = Math.Max(1, (int)Math.Round(_size * p.PluckPosition));
            if (np < _size)
            {
                var scratch = new float[_size];
                for (int i = 0; i < _size; i++)
                {
                    float x = _buffer[i];
                    float xPrev = i - np >= 0 ? _buffer[i - np] : 0f;
                    scratch[i] = x - xPrev;
                }
                Array.Copy(scratch, _buffer, _size);
            }

            // LP sur l'excitation (softness) : pulpe = LP plus fort
            if (p.PluckSoftness > 0.01f)
            {
                float alpha = 0.05f + p.PluckSoftness * 0.4f;   // 0.05..0.45
                float lp = 0f;
                for (int i = 0; i < _size; i++)
                {
                    lp += alpha * (_buffer[i] - lp);
                    _buffer[i] = lp;
                }
            }

            // Normalisation + application vélocité
            float peak = 0.001f;
            for (int i = 0; i < _size; i++) peak = Math.Max(peak, Math.Abs(_buffer[i]));
            float gain = velocity / peak;
            for (int i = 0; i < _size; i++) _buffer[i] *= gain;

            _writeIdx = 0;
            _lpPrev = 0f;
            _tonePrev = 0f;
            _apPrevIn = 0f;
            _apPrevOut = 0f;

            float p01 = 0.5f * (1f + pan);
            _panL = 1f - p01;
            _panR = p01;

            _peakEnvelope = 1f;
            _active = true;
        }

        public void Kill()
        {
            _active = false;
            _peakEnvelope = 0f;
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        public float RenderSample(in NgParams p)
        {
            if (!_active) return 0f;

            float sample = _buffer[_writeIdx];

            // LP moyen classique KS (préserve la fondamentale, atténue les aigus au fil du temps)
            float lp = 0.5f * (sample + _lpPrev);
            _lpPrev = sample;

            // LP variable en série (Brightness) — 300..8000 Hz
            float toneHz = 300f + p.Brightness * 7700f;
            float toneCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * toneHz / _sampleRate);
            _tonePrev += toneCoef * (lp - _tonePrev);
            float toned = _tonePrev;

            // All-pass léger pour dispersion (nylon = très peu, presque 0 par défaut)
            float a = p.Stiffness * 0.4f;
            float apOut = a * toned + _apPrevIn - a * _apPrevOut;
            _apPrevIn = toned;
            _apPrevOut = apOut;

            // Feedback avec sustain paramétrable
            // Sustain 0..1 → gBase 0.9975..0.9995 (nylon = feedback très haut = sustain long)
            float gBase = 0.9975f + p.Sustain * 0.002f;
            float gEff = (float)Math.Pow(gBase, _size / 1000.0);
            float outValue = apOut * gEff;

            _buffer[_writeIdx] = outValue;
            _writeIdx++;
            if (_writeIdx >= _size) _writeIdx = 0;

            float absOut = Math.Abs(outValue);
            _peakEnvelope = Math.Max(_peakEnvelope * 0.9998f, absOut);
            if (_peakEnvelope < SilenceThreshold)
            {
                _active = false;
                return 0f;
            }

            return sample;
        }
    }
}
