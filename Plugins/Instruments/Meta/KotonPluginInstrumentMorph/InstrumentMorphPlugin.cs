using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginInstrumentMorph
{
    /// <summary>
    /// Instrument Morph — meme concept que WaveMorph, mais au lieu de choisir une FORME D'ONDE pour
    /// l'oscillateur A et B, on choisit un INSTRUMENT KOTON complet. La sortie de l'instrument A et
    /// celle de l'instrument B sont mixees par le slider Morph (crossfade equi-puissance cos/sin) —
    /// c'est le signature-move de Koton : morph continu entre 2 instruments physiques.
    ///
    /// **Exemples** :
    /// - Violon → Flûte : morph=0 pur violon bowed, morph=1 pur flûte blown, entre 2 un timbre
    ///   hybride "violon souffle" impossible acoustiquement
    /// - Karplus (guitare) → Bell → au fil du LFO, un pluck qui se transforme en cloche pendant la note
    /// - Ocean drone → Woodwind pad : morph slow LFO donne l'impression d'un vent qui devient flûte
    /// - JewsHarp → Handpan : même percu, timbre morph
    ///
    /// **Architecture** : les 2 instruments sont instancies via KotonHost.InstantiateInstrument (le
    /// host injecte le registry), et prepared avec le meme sample rate. Chaque NoteOn/Off est
    /// broadcasté aux 2. Render appelle A.Render puis B.Render dans des buffers temporaires,
    /// puis crossfade.
    ///
    /// **Modulation** : LFO sinusoidal sur le morph (rate 0..12 Hz, depth 0..1). Envelope Note (AR)
    /// aussi possible — chaque note commence à morph=start et migre vers morph=end sur envAr ms.
    /// </summary>
    [KotonInstrument("Instrument Morph", Id = "koton.instrumentmorph", Category = "Meta", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class InstrumentMorphPlugin : IKotonInstrument
    {
        public string Id => "koton.instrumentmorph";
        public string DisplayName => "Instrument Morph";

        // Ids des 2 instruments choisis (string pour dropdown, sauvegarde .sq).
        // Vide = pas d'instrument selectionne (le canal reste silencieux).
        internal string _idA = "";
        internal string _idB = "";
        // Etat opaque (blob JSON du SaveState de chaque instrument), pour persister les tweaks des
        // 2 instruments avec le projet.
        internal string _stateA;
        internal string _stateB;

        readonly KotonParameter _morph      = new KotonParameter("morph",      "Morph A → B",  0.0, 1.0, 0.5);
        // Mode 0 = Mix (lerp signaux, sonne comme 2 instruments a la fois),
        // Mode 1 = Spectral morph (banc de filtres + env followers, vrai timbre hybride)
        readonly KotonParameter _mode       = new KotonParameter("mode",       "Mode",         0, 1, 1) { Automatable = false };
        readonly KotonParameter _lfoRate    = new KotonParameter("lfo_rate",   "LFO rate",     0.0, 12.0, 0.0, "Hz");
        readonly KotonParameter _lfoDepth   = new KotonParameter("lfo_depth",  "LFO depth",    0.0, 1.0, 0.0);
        readonly KotonParameter _envMorph   = new KotonParameter("env_morph",  "Env morph",    -1.0, 1.0, 0.0);
        readonly KotonParameter _envMs      = new KotonParameter("env_ms",     "Env time",     10.0, 5000.0, 500.0, "ms");
        readonly KotonParameter _gainA      = new KotonParameter("gain_a",     "Gain A",       -12.0, 12.0, 0.0, "dB");
        readonly KotonParameter _gainB      = new KotonParameter("gain_b",     "Gain B",       -12.0, 12.0, 0.0, "dB");
        readonly KotonParameter _volumeDb   = new KotonParameter("volume",     "Volume",       -30.0, 6.0, -3.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        int _maxBlock;
        IKotonInstrument _a;
        IKotonInstrument _b;

        // Buffers reutilises pour le rendu A / B avant crossfade
        float[] _bufAL, _bufAR, _bufBL, _bufBR;

        // Peek buffers ring pour l'affichage 3 ondes en temps reel (A / B / OUT). Circulaire de
        // PeekSize samples ~46 ms a 44.1kHz — l'editeur snapshot a 30 Hz.
        const int PeekSize = 2048;
        readonly float[] _peekA = new float[PeekSize];
        readonly float[] _peekB = new float[PeekSize];
        readonly float[] _peekOut = new float[PeekSize];
        int _peekPos;

        // Etat de la modulation
        double _lfoPhase;
        // Envelope de morph par-note : declenche au NoteOn, atteint 0 (destination = morph nominal)
        // en envMs ms. Valeur = 0..1 courant.
        float _envAmount;
        float _envDecay;

        public InstrumentMorphPlugin()
        {
            _params = new List<KotonParameter> {
                _morph, _mode, _lfoRate, _lfoDepth, _envMorph, _envMs, _gainA, _gainB, _volumeDb,
            };
            InitVocoder();
        }

        // ======= Vocoder-style spectral morph =======
        // 16 bandes BP log-spaced 80..6400 Hz. Chaque bande a un env follower (peak avec attaque
        // 5ms / release 50ms). Le carrier = mix A+B ; on remodele son enveloppe SPECTRALE avec les
        // env A et B interpolees. Resultat : au milieu (m=0.5) on a un timbre HYBRIDE reel — le
        // spectre est intermediaire entre A et B, pas la superposition des 2.
        const int Bands = 16;
        static readonly float[] BandFreqs;
        static InstrumentMorphPlugin()
        {
            BandFreqs = new float[Bands];
            // Log-spaced 80 -> 6400 Hz (6.3 octaves, ~0.42 oct par bande)
            double logMin = Math.Log(80), logMax = Math.Log(6400);
            for (int i = 0; i < Bands; i++)
                BandFreqs[i] = (float)Math.Exp(logMin + (logMax - logMin) * i / (Bands - 1));
        }
        readonly BiquadState[] _bpA = new BiquadState[Bands];
        readonly BiquadState[] _bpB = new BiquadState[Bands];
        readonly BiquadState[] _bpC = new BiquadState[Bands];
        readonly float[] _envA = new float[Bands];
        readonly float[] _envB = new float[Bands];
        readonly float[] _envC = new float[Bands];
        float _envAtk, _envRel;

        void InitVocoder()
        {
            for (int i = 0; i < Bands; i++)
            {
                _envA[i] = 0f; _envB[i] = 0f; _envC[i] = 0f;
                _bpA[i] = default; _bpB[i] = default; _bpC[i] = default;
            }
        }
        void SetupVocoderForSr(int sr)
        {
            for (int i = 0; i < Bands; i++)
            {
                SetBiquadBP(ref _bpA[i], sr, BandFreqs[i], 3.5f);
                SetBiquadBP(ref _bpB[i], sr, BandFreqs[i], 3.5f);
                SetBiquadBP(ref _bpC[i], sr, BandFreqs[i], 3.5f);
            }
            _envAtk = (float)Math.Exp(-1.0 / (0.005 * sr));   // 5 ms attack
            _envRel = (float)Math.Exp(-1.0 / (0.060 * sr));   // 60 ms release
        }

        internal struct BiquadState { public float b0, b1, b2, a1, a2; public float x1, x2, y1, y2; }
        static void SetBiquadBP(ref BiquadState s, int sr, float freq, float q)
        {
            if (freq < 20f) freq = 20f;
            if (freq > sr * 0.45f) freq = sr * 0.45f;
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
            s.b0 = (float)(alpha / a0);
            s.b1 = 0;
            s.b2 = (float)(-alpha / a0);
            s.a1 = (float)(-2.0 * cosw0 / a0);
            s.a2 = (float)((1.0 - alpha) / a0);
        }
        static float BiquadProc(ref BiquadState s, float x)
        {
            float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2;
            s.x2 = s.x1; s.x1 = x;
            s.y2 = s.y1; s.y1 = y;
            return y;
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new InstrumentMorphEditor(this);

        // Public helpers pour l'editeur
        public string InstrumentAId { get => _idA; set { _idA = value ?? ""; ReloadA(); } }
        public string InstrumentBId { get => _idB; set { _idB = value ?? ""; ReloadB(); } }
        public IKotonInstrument InstrumentA => _a;
        public IKotonInstrument InstrumentB => _b;

        void ReloadA()
        {
            try { (_a as IDisposable)?.Dispose(); } catch { }
            _a = null;
            if (string.IsNullOrEmpty(_idA) || _idA == Id) return;   // pas d'auto-referencing
            var inst = KotonHost.InstantiateInstrument?.Invoke(_idA);
            if (inst == null) return;
            _a = inst;
            if (_sr > 0) _a.Prepare(_sr, _maxBlock);
            if (!string.IsNullOrEmpty(_stateA)) { try { _a.LoadState(Encoding.UTF8.GetBytes(_stateA)); } catch { } }
        }
        void ReloadB()
        {
            try { (_b as IDisposable)?.Dispose(); } catch { }
            _b = null;
            if (string.IsNullOrEmpty(_idB) || _idB == Id) return;
            var inst = KotonHost.InstantiateInstrument?.Invoke(_idB);
            if (inst == null) return;
            _b = inst;
            if (_sr > 0) _b.Prepare(_sr, _maxBlock);
            if (!string.IsNullOrEmpty(_stateB)) { try { _b.LoadState(Encoding.UTF8.GetBytes(_stateB)); } catch { } }
        }

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _maxBlock = maxBlockSize;
            _bufAL = new float[maxBlockSize];
            _bufAR = new float[maxBlockSize];
            _bufBL = new float[maxBlockSize];
            _bufBR = new float[maxBlockSize];
            SetupVocoderForSr(sampleRate);
            try { _a?.Prepare(sampleRate, maxBlockSize); } catch { }
            try { _b?.Prepare(sampleRate, maxBlockSize); } catch { }
        }
        public void Reset()
        {
            try { _a?.Reset(); } catch { }
            try { _b?.Reset(); } catch { }
        }
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            try { _a?.NoteOn(note, velocity, sampleOffset); } catch { }
            try { _b?.NoteOn(note, velocity, sampleOffset); } catch { }
            // Envelope de morph : démarre à 1.0 (= amplitude max de l'offset envMorph), décroit exp
            // vers 0 en envMs ms → position finale = morph nominal.
            _envAmount = 1f;
            float ms = (float)_envMs.Value;
            _envDecay = (float)Math.Exp(-6.907755278982137 / (ms * _sr / 1000.0));
        }
        public void NoteOff(int note, int sampleOffset = 0)
        {
            try { _a?.NoteOff(note, sampleOffset); } catch { }
            try { _b?.NoteOff(note, sampleOffset); } catch { }
        }
        public void MidiCC(int cc, int value, int sampleOffset = 0)
        {
            try { _a?.MidiCC(cc, value, sampleOffset); } catch { }
            try { _b?.MidiCC(cc, value, sampleOffset); } catch { }
        }
        public void SetPitchBend(float value, int sampleOffset = 0)
        {
            try { _a?.SetPitchBend(value, sampleOffset); } catch { }
            try { _b?.SetPitchBend(value, sampleOffset); } catch { }
        }

        public void Render(Span<float> left, Span<float> right)
        {
            int n = left.Length;
            // Zero les 2 buffers de travail
            Array.Clear(_bufAL, 0, n); Array.Clear(_bufAR, 0, n);
            Array.Clear(_bufBL, 0, n); Array.Clear(_bufBR, 0, n);

            if (_a != null)
            {
                try { _a.Render(new Span<float>(_bufAL, 0, n), new Span<float>(_bufAR, 0, n)); }
                catch { Array.Clear(_bufAL, 0, n); Array.Clear(_bufAR, 0, n); }
            }
            if (_b != null)
            {
                try { _b.Render(new Span<float>(_bufBL, 0, n), new Span<float>(_bufBR, 0, n)); }
                catch { Array.Clear(_bufBL, 0, n); Array.Clear(_bufBR, 0, n); }
            }

            float baseMorph = (float)_morph.Value;
            int mode = (int)Math.Round(_mode.Value);
            float lfoRate = (float)_lfoRate.Value;
            float lfoDepth = (float)_lfoDepth.Value;
            float envMorphAmount = (float)_envMorph.Value;
            float gainALin = (float)Math.Pow(10.0, _gainA.Value / 20.0);
            float gainBLin = (float)Math.Pow(10.0, _gainB.Value / 20.0);
            float outLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);

            for (int i = 0; i < n; i++)
            {
                // LFO sinusoidal
                float lfo = 0f;
                if (lfoRate > 0.001f)
                {
                    _lfoPhase += lfoRate / _sr;
                    if (_lfoPhase >= 1.0) _lfoPhase -= 1.0;
                    lfo = (float)Math.Sin(_lfoPhase * 2.0 * Math.PI);
                }
                _envAmount *= _envDecay;

                float m = baseMorph + lfo * lfoDepth * 0.5f + _envAmount * envMorphAmount * 0.5f;
                if (m < 0f) m = 0f;
                if (m > 1f) m = 1f;

                float aL = _bufAL[i] * gainALin;
                float aR = _bufAR[i] * gainALin;
                float bL = _bufBL[i] * gainBLin;
                float bR = _bufBR[i] * gainBLin;
                float aMono = (aL + aR) * 0.5f;
                float bMono = (bL + bR) * 0.5f;

                float sL, sR;
                if (mode == 0)
                {
                    // Mode Mix : lerp lineaire signaux (2 instruments qui se croisent en volume)
                    float mixL = aL + (bL - aL) * m;
                    float mixR = aR + (bR - aR) * m;
                    sL = mixL * outLin;
                    sR = mixR * outLin;
                }
                else
                {
                    // Mode Vocoder V2 : DOUBLE VOCODER CONVERGENT. Le probleme du vocoder V1 (carrier
                    // = lerp signaux) : l'env du carrier vaut deja lerp(envA, envB) donc gain=1 et
                    // ca sonne comme un simple mix. Ici on fait 2 vocoders paralleles qui recolorent
                    // A et B SEPAREMENT vers le meme spectre cible, puis crossfade :
                    //   voiceA = A recolore vers env_target (= lerp env_A + env_B)
                    //   voiceB = B recolore vers env_target (meme cible)
                    //   out    = voiceA*(1-m) + voiceB*m
                    // Au milieu, voiceA et voiceB ont le MEME spectre → ne se contredisent plus,
                    // fusionnent en un timbre unifie. Aux extremes, gain=1 → A pur ou B pur.
                    float voiceA = 0f, voiceB = 0f;
                    for (int b = 0; b < Bands; b++)
                    {
                        float aBand = BiquadProc(ref _bpA[b], aMono);
                        float bBand = BiquadProc(ref _bpB[b], bMono);

                        // Env followers peak-hold : attaque rapide, release lent
                        float absA = aBand < 0 ? -aBand : aBand;
                        float absB = bBand < 0 ? -bBand : bBand;
                        _envA[b] = absA > _envA[b] ? _envAtk * _envA[b] + (1 - _envAtk) * absA : _envRel * _envA[b] + (1 - _envRel) * absA;
                        _envB[b] = absB > _envB[b] ? _envAtk * _envB[b] + (1 - _envAtk) * absB : _envRel * _envB[b] + (1 - _envRel) * absB;

                        float envTarget = _envA[b] * (1 - m) + _envB[b] * m;
                        float gainA = envTarget / (_envA[b] + 1e-5f);
                        float gainB = envTarget / (_envB[b] + 1e-5f);
                        if (gainA > 4f) gainA = 4f;
                        if (gainB > 4f) gainB = 4f;
                        voiceA += aBand * gainA;
                        voiceB += bBand * gainB;
                    }
                    float voc = voiceA * (1 - m) + voiceB * m;
                    voc *= 0.6f;
                    sL = voc * outLin;
                    sR = voc * outLin;
                }

                if (sL > 1f) sL = 1f; else if (sL < -1f) sL = -1f;
                if (sR > 1f) sR = 1f; else if (sR < -1f) sR = -1f;   
                left[i] = sL;
                right[i] = sR;

                // Peek pour l'editeur : sample mono (moyenne L+R) des 3 signaux (A pur, B pur, OUT)
                // dans un ring buffer. Editeur snapshot a 30 Hz.
                float oMono = (sL + sR) * 0.5f;
                _peekA[_peekPos] = aMono;
                _peekB[_peekPos] = bMono;
                _peekOut[_peekPos] = oMono;
                _peekPos = (_peekPos + 1) % PeekSize;
            }
        }

        // Sauvegarde : ids + params + blobs des 2 instruments
        public byte[] SaveState()
        {
            try
            {
                var d = new Dictionary<string, object>();
                foreach (var kp in _params) d[kp.Id] = kp.Value;
                d["_idA"] = _idA ?? "";
                d["_idB"] = _idB ?? "";
                if (_a != null)
                {
                    try { var st = _a.SaveState(); if (st != null && st.Length > 0) d["_stateA"] = Encoding.UTF8.GetString(st); } catch { }
                }
                if (_b != null)
                {
                    try { var st = _b.SaveState(); if (st != null && st.Length > 0) d["_stateB"] = Encoding.UTF8.GetString(st); } catch { }
                }
                return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d));
            }
            catch { return Array.Empty<byte>(); }
        }
        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(state));
                var root = doc.RootElement;
                foreach (var kp in _params)
                {
                    if (root.TryGetProperty(kp.Id, out var v) && v.ValueKind == JsonValueKind.Number)
                        kp.Value = v.GetDouble();
                }
                if (root.TryGetProperty("_idA", out var idA)) _idA = idA.GetString() ?? "";
                if (root.TryGetProperty("_idB", out var idB)) _idB = idB.GetString() ?? "";
                _stateA = root.TryGetProperty("_stateA", out var sA) ? sA.GetString() : null;
                _stateB = root.TryGetProperty("_stateB", out var sB) ? sB.GetString() : null;
                ReloadA();
                ReloadB();
            }
            catch { }
        }
        public void Dispose()
        {
            try { (_a as IDisposable)?.Dispose(); } catch { }
            try { (_b as IDisposable)?.Dispose(); } catch { }
            _a = null; _b = null;
        }

        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }

        // Capture l'etat actuel des 2 instruments dans les blobs (a appeler avant SaveState).
        // L'editeur appelle CaptureChildStates() apres un tweak d'un instrument enfant, pour que la
        // sauvegarde .sq contienne l'etat courant meme si l'editeur enfant n'a pas ete ferme.
        public void CaptureChildStates()
        {
            try { if (_a != null) { var s = _a.SaveState(); _stateA = s != null && s.Length > 0 ? Encoding.UTF8.GetString(s) : null; } } catch { }
            try { if (_b != null) { var s = _b.SaveState(); _stateB = s != null && s.Length > 0 ? Encoding.UTF8.GetString(s) : null; } } catch { }
        }

        /// <summary>Snapshot des 3 ring-buffers de peek (A/B/OUT) dans les tableaux fournis
        /// (allocation-free : l'appelant reutilise les buffers a travers les frames). Les samples
        /// sont ordonnes chronologiquement (les plus vieux au debut, plus recent a la fin). Appele
        /// par l'editeur a 30 Hz — coût = 3 copies de 2048 floats.</summary>
        public void SnapshotPeek(float[] outA, float[] outB, float[] outMix)
        {
            if (outA == null || outA.Length != PeekSize) return;
            int start = _peekPos;   // le prochain slot a ecrire = le plus vieux
            for (int i = 0; i < PeekSize; i++)
            {
                int src = (start + i) % PeekSize;
                outA[i] = _peekA[src];
                outB[i] = _peekB[src];
                outMix[i] = _peekOut[src];
            }
        }
        public int PeekBufferSize => PeekSize;
    }
}
