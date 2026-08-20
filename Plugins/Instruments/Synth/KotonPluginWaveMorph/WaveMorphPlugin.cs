using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginWaveMorph
{
    /// <summary>
    /// Snapshot des paramètres du plugin AU DÉBUT d'un buffer audio. Rempli une fois par
    /// <see cref="WaveMorphPlugin.Render"/>, puis passé à chaque appel de
    /// <see cref="WaveMorphVoice.RenderSample"/>. Évite de re-lire chaque KotonParameter.Value pour
    /// chaque voix × sample (économie modeste mais l'agréable est surtout la centralisation des
    /// conversions unités : ms→sec, dB→linéaire, index→enum).
    ///
    /// Les valeurs sont figées pour la durée du buffer — un slider bougé pendant ce buffer ne
    /// s'entend qu'au prochain. Latence typique ~10-30 ms selon la taille de buffer + LookaheadBuffer
    /// côté hôte, invisible à l'oreille.
    /// </summary>
    internal struct WaveMorphParams
    {
        // Oscillateurs
        public WavePrim W1Wave, W2Wave;
        public float W1AmpDb, W2AmpDb;
        public float W1DetuneCents, W2DetuneCents;
        public double W1Mult, W2Mult;

        // Morphing
        public float XFade;

        // Env Amp
        public float AmpAttackSec, AmpDecaySec, AmpSustain, AmpReleaseSec;
        // Env 2
        public float E2AttackSec, E2DecaySec, E2Sustain, E2ReleaseSec;
        // Env 3
        public float E3AttackSec, E3DecaySec, E3Sustain, E3ReleaseSec;

        // LFOs
        public LfoShape Lfo1Shape, Lfo2Shape;
        public float Lfo1RateHz, Lfo2RateHz;
        public float Lfo1Amount, Lfo2Amount;

        // Filtres
        public FilterType F1Type, F2Type;
        public bool F1Slope24, F2Slope24;
        public double F1Cutoff, F2Cutoff;
        public float F1Res, F2Res;
        public float F1DriveDb, F2DriveDb;
        public float F1Mix, F2Mix;
        public bool ParallelRouting;

        // Output
        public float OutVolumeDb;
        public float OutPan;
        public float GlideMs;

        // Global (partagé toutes voix)
        public float BendMul;
    }

    /// <summary>
    /// Wave Morph — synthétiseur 2 oscillateurs primitifs (Sine/Square/Triangle/Sawtooth) morphés
    /// via un X-Fade linéaire, avec 3 enveloppes ADSR, 2 LFO, 2 filtres biquad, mod matrix, et
    /// un mixer output (volume/pan/glide + voice mode).
    ///
    /// **Signal path** (par sample, par voix) :
    /// <code>
    ///   osc1 ─amp1─┐
    ///              ├─(xfade)─ amp_env ─ filter_chain ─ vol/pan ─ out (stéréo)
    ///   osc2 ─amp2─┘
    /// </code>
    /// La <c>filter_chain</c> = F1→F2 (série) ou (F1+F2)/2 (parallèle) selon <c>ParallelRouting</c>.
    ///
    /// **Mod matrix** : les 8 sources (Env2/3, LFO1/2, Vel, Note, AT, ModWheel) peuvent affecter les
    /// 11 cibles (XFade, W1/W2 Amp, W1/W2 Det, F1/F2 Freq, F1/F2 Res, Amp, Pan). Stockée à part des
    /// KotonParameter (JSON dédié dans SaveState) — les slots sont dynamiques (0-N entrées), un
    /// KotonParameter par slot serait rigide.
    ///
    /// **Polyphonie** : 8 ou 16 voix selon <c>voice_mode</c>. Mode Mono = 1 voix avec glide entre
    /// notes. Voice stealing en round-robin quand toutes les voix sont occupées.
    ///
    /// **Persistance** : JSON UTF-8, cf. <see cref="SaveState"/>/<see cref="LoadState"/>. Blob
    /// corrompu = garde les défauts (pas de crash).
    ///
    /// **Ce qui manque en v1** (documenté, pas TODO caché) :
    /// - Unison : le mode est exposé mais monomapped à "1 voix par note" pour l'instant. Enrichir
    ///   demande d'ajouter des sub-voices detunées dans <see cref="WaveMorphVoice"/> — laissé pour v2.
    /// - LFO tempo-sync : les LFO sont uniquement en Hz. L'accès au tempo se fait via
    ///   <see cref="KotonHost.CurrentContext"/> mais pas encore câblé.
    /// - Presets : pas de banque built-in (contrairement au FM Synth qui en a 44). Le user pose ses
    ///   valeurs, le SaveState préserve tout.
    /// </summary>
    [KotonInstrument("Wave Morph", Id = "koton.wavemorph", Category = "Synth", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class WaveMorphPlugin : IKotonInstrument
    {
        public string Id => "koton.wavemorph";
        public string DisplayName => "Wave Morph";

        // =============================================================================================
        // Paramètres exposés
        // =============================================================================================
        //
        // Convention : nom = section_champ (osc1_amp, env2_attack...) pour lisibilité côté JSON.
        // Les temps ADSR sont en MS côté UI (convertis en secondes au Render — plus intuitif).
        // Les amps sont en dB (linéaire trop peu discriminant sur la plage -60..+6).
        // Les detunes sont en cents (±100 = ±1 semitone, borne raisonnable pour un synth).
        // Les cutoffs sont en Hz (10..20000, la plage audible).
        // Les slopes 12/24 dB sont stockés en bool via 0/1 dans un KotonParameter (0..1 range).

        // Osc 1
        readonly KotonParameter _w1Wave   = new KotonParameter("w1_wave",   "Wave 1",       0, WaveOsc.Count - 1, 0);
        readonly KotonParameter _w1Amp    = new KotonParameter("w1_amp",    "W1 Amp",       -60, 6, -6, "dB");
        readonly KotonParameter _w1Detune = new KotonParameter("w1_detune", "W1 Detune",    -100, 100, 0, "ct");
        readonly KotonParameter _w1Mult   = new KotonParameter("w1_mult",   "W1 Mult",      0, FreqMult.Count - 1, 0);

        // Osc 2
        readonly KotonParameter _w2Wave   = new KotonParameter("w2_wave",   "Wave 2",       0, WaveOsc.Count - 1, 1);   // Square par défaut pour un contraste immédiat avec Wave 1 Sine
        readonly KotonParameter _w2Amp    = new KotonParameter("w2_amp",    "W2 Amp",       -60, 6, -6, "dB");
        readonly KotonParameter _w2Detune = new KotonParameter("w2_detune", "W2 Detune",    -100, 100, 0, "ct");
        readonly KotonParameter _w2Mult   = new KotonParameter("w2_mult",   "W2 Mult",      0, FreqMult.Count - 1, 0);

        // X-Fade
        readonly KotonParameter _xfade    = new KotonParameter("xfade",     "X-Fade",       0, 1, 0.5);

        // Env Amp (défauts : attaque courte, decay moyen, sustain 0.7, release moyen — patch "pluck-pad")
        readonly KotonParameter _ampA = new KotonParameter("amp_a", "Amp A", 1, 4000, 5, "ms");
        readonly KotonParameter _ampD = new KotonParameter("amp_d", "Amp D", 1, 4000, 200, "ms");
        readonly KotonParameter _ampS = new KotonParameter("amp_s", "Amp S", 0, 1, 0.7);
        readonly KotonParameter _ampR = new KotonParameter("amp_r", "Amp R", 1, 8000, 400, "ms");

        // Env 2 (défaut : identique à amp — un utilisateur qui ne s'en sert pas ne l'affectera à rien)
        readonly KotonParameter _e2A = new KotonParameter("e2_a", "Env2 A", 1, 4000, 20, "ms");
        readonly KotonParameter _e2D = new KotonParameter("e2_d", "Env2 D", 1, 4000, 400, "ms");
        readonly KotonParameter _e2S = new KotonParameter("e2_s", "Env2 S", 0, 1, 0.4);
        readonly KotonParameter _e2R = new KotonParameter("e2_r", "Env2 R", 1, 8000, 250, "ms");

        // Env 3
        readonly KotonParameter _e3A = new KotonParameter("e3_a", "Env3 A", 1, 4000, 10, "ms");
        readonly KotonParameter _e3D = new KotonParameter("e3_d", "Env3 D", 1, 4000, 1200, "ms");
        readonly KotonParameter _e3S = new KotonParameter("e3_s", "Env3 S", 0, 1, 1.0);
        readonly KotonParameter _e3R = new KotonParameter("e3_r", "Env3 R", 1, 8000, 300, "ms");

        // LFO 1
        readonly KotonParameter _l1Rate   = new KotonParameter("l1_rate",   "LFO1 Rate",  0.05, 20, 3.5, "Hz");
        readonly KotonParameter _l1Shape  = new KotonParameter("l1_shape",  "LFO1 Shape", 0, 5, 0);
        readonly KotonParameter _l1Amount = new KotonParameter("l1_amount", "LFO1 Amt",   0, 1, 0.5);

        // LFO 2
        readonly KotonParameter _l2Rate   = new KotonParameter("l2_rate",   "LFO2 Rate",  0.05, 20, 6.0, "Hz");
        readonly KotonParameter _l2Shape  = new KotonParameter("l2_shape",  "LFO2 Shape", 0, 5, 1);
        readonly KotonParameter _l2Amount = new KotonParameter("l2_amount", "LFO2 Amt",   0, 1, 0.5);

        // Filtre 1 : LP 12 dB à 4.9 kHz, Q faible (défaut "brillance sans coloration forte")
        readonly KotonParameter _f1Type   = new KotonParameter("f1_type",   "F1 Type",    0, 3, 0);
        readonly KotonParameter _f1Slope  = new KotonParameter("f1_slope",  "F1 Slope",   0, 1, 0);  // 0 = 12 dB, 1 = 24 dB
        readonly KotonParameter _f1Cutoff = new KotonParameter("f1_cutoff", "F1 Cutoff",  20, 20000, 4900, "Hz");
        readonly KotonParameter _f1Res    = new KotonParameter("f1_res",    "F1 Res",     0, 1, 0.2);
        readonly KotonParameter _f1Drive  = new KotonParameter("f1_drive",  "F1 Drive",   -12, 24, 0, "dB");
        readonly KotonParameter _f1Mix    = new KotonParameter("f1_mix",    "F1 Mix",     0, 1, 1.0);

        // Filtre 2 : HP 12 dB à 80 Hz par défaut (cleanup des sub-basses)
        readonly KotonParameter _f2Type   = new KotonParameter("f2_type",   "F2 Type",    0, 3, 1);
        readonly KotonParameter _f2Slope  = new KotonParameter("f2_slope",  "F2 Slope",   0, 1, 0);
        readonly KotonParameter _f2Cutoff = new KotonParameter("f2_cutoff", "F2 Cutoff",  20, 20000, 80, "Hz");
        readonly KotonParameter _f2Res    = new KotonParameter("f2_res",    "F2 Res",     0, 1, 0.2);
        readonly KotonParameter _f2Drive  = new KotonParameter("f2_drive",  "F2 Drive",   -12, 24, 0, "dB");
        readonly KotonParameter _f2Mix    = new KotonParameter("f2_mix",    "F2 Mix",     0, 1, 1.0);

        // Routing filtres : 0 = série (F1→F2), 1 = parallèle ((F1+F2)/2)
        readonly KotonParameter _fRouting = new KotonParameter("f_routing", "F Routing",  0, 1, 0);

        // Output
        readonly KotonParameter _outVol   = new KotonParameter("out_vol",  "Volume", -60, 6, -6, "dB");
        readonly KotonParameter _outPan   = new KotonParameter("out_pan",  "Pan",    -1, 1, 0);
        readonly KotonParameter _glide    = new KotonParameter("glide",    "Glide",  0, 500, 0, "ms");
        // Voice mode : 0 = Mono, 1 = Poly 8, 2 = Poly 16
        readonly KotonParameter _voiceMode = new KotonParameter("voice_mode", "Voice", 0, 2, 1);
        // Unison mode : 0 = Off, 1 = Classic 2, 2 = Wide 3, 3 = Shimmer 5
        // NOTE v1 : exposé pour la persistance et l'UI, mais NON IMPLÉMENTÉ dans le rendu (le voice
        // reste mono-oscillateur par instance). Un futur pass ajoutera des sub-voices detunées.
        readonly KotonParameter _unisonMode = new KotonParameter("unison_mode", "Unison", 0, 3, 0);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        // =============================================================================================
        // Mod matrix (état interne, sérialisé dans le blob JSON)
        // =============================================================================================
        readonly ModMatrix _modMatrix = new ModMatrix();
        public ModMatrix Matrix => _modMatrix;

        // =============================================================================================
        // Voix polyphoniques
        // =============================================================================================
        // MaxVoices = 16 (borne haute du voice_mode). Un mode Mono utilise juste voice[0]. Un mode
        // Poly 8 utilise voices[0..7]. Le voice stealing round-robin remplace la voix la plus ancienne
        // quand tout est occupé (comportement standard des synths mono-timbraux simples).
        const int MaxVoices = 16;
        WaveMorphVoice[] _voices;
        int _voiceRR;

        int _sampleRate = 44100;
        float _bendMul = 1f;
        float _bendRangeSemis = 2f;

        // ---- Mod wheel + aftertouch globals (partagés entre voix — ils sont MIDI-channel-wide) ----
        float _modWheelGlobal;
        float _aftertouchGlobal;

        // ---- Scope ring buffer (pour l'oscilloscope de l'éditeur, non locké) ----
        internal const int ScopeSize = 1024;
        readonly float[] _scope = new float[ScopeSize];
        int _scopeWrite;

        // ---- Fréquence de la dernière note jouée (pour amorcer le glide en mode Mono) ----
        float _lastNoteFreq;

        public WaveMorphPlugin()
        {
            // Ordre = ordre logique par section. L'éditeur pioche par Id (pas de dépendance sur
            // l'ordre) mais un affichage générique de type "liste de sliders" retomberait ici sur
            // un ordre lisible.
            _params = new List<KotonParameter>
            {
                _w1Wave, _w1Amp, _w1Detune, _w1Mult,
                _w2Wave, _w2Amp, _w2Detune, _w2Mult,
                _xfade,
                _ampA, _ampD, _ampS, _ampR,
                _e2A, _e2D, _e2S, _e2R,
                _e3A, _e3D, _e3S, _e3R,
                _l1Rate, _l1Shape, _l1Amount,
                _l2Rate, _l2Shape, _l2Amount,
                _f1Type, _f1Slope, _f1Cutoff, _f1Res, _f1Drive, _f1Mix,
                _f2Type, _f2Slope, _f2Cutoff, _f2Res, _f2Drive, _f2Mix,
                _fRouting,
                _outVol, _outPan, _glide, _voiceMode, _unisonMode,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new WaveMorphEditor(this);

        // =============================================================================================
        // Cycle audio
        // =============================================================================================

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate <= 0 ? 44100 : sampleRate;
            _voices = new WaveMorphVoice[MaxVoices];
            for (int i = 0; i < MaxVoices; i++) _voices[i] = new WaveMorphVoice(_sampleRate);
            _voiceRR = 0;
        }

        public void Reset()
        {
            if (_voices != null)
                for (int i = 0; i < _voices.Length; i++) _voices[i]?.Reset();
            _bendMul = 1f;
            _modWheelGlobal = 0f;
            _aftertouchGlobal = 0f;
            _lastNoteFreq = 0f;
            Array.Clear(_scope, 0, _scope.Length);
        }

        int ActiveVoiceCount()
        {
            // Mono = 1, Poly8 = 8, Poly16 = 16. Clampé selon MaxVoices.
            int mode = (int)Math.Round(_voiceMode.Value);
            if (mode <= 0) return 1;
            if (mode == 1) return Math.Min(8, MaxVoices);
            return Math.Min(16, MaxVoices);
        }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null) return;
            int poly = ActiveVoiceCount();
            bool isMono = poly == 1;

            if (isMono)
            {
                // Mono : réutiliser toujours voice[0], amorcer le glide depuis _lastNoteFreq.
                var v = _voices[0];
                float newFreq = (float)(440.0 * Math.Pow(2, (note - 69) / 12.0));
                bool glideOn = _glide.Value > 0.001 && _lastNoteFreq > 0 && v.Active;
                v.NoteOn(note, velocity <= 0 ? 100 : velocity, glideOn, _lastNoteFreq);
                v.Aftertouch = _aftertouchGlobal;
                v.ModWheel = _modWheelGlobal;
                _lastNoteFreq = newFreq;
                return;
            }

            // Poly : voix inactive d'abord, sinon voice stealing round-robin.
            int slot = -1;
            for (int i = 0; i < poly; i++)
            {
                if (!_voices[i].Active) { slot = i; break; }
            }
            if (slot == -1)
            {
                slot = _voiceRR % poly;
                _voiceRR = (_voiceRR + 1) % poly;
            }
            var voice = _voices[slot];
            voice.NoteOn(note, velocity <= 0 ? 100 : velocity, false, 0);
            voice.Aftertouch = _aftertouchGlobal;
            voice.ModWheel = _modWheelGlobal;
            _lastNoteFreq = voice.TargetFreq;
        }

        public void NoteOff(int note, int sampleOffset = 0)
        {
            if (_voices == null) return;
            int poly = ActiveVoiceCount();
            for (int i = 0; i < poly; i++)
            {
                if (_voices[i].Active && _voices[i].Note == note) _voices[i].NoteOff();
            }
        }

        public void MidiCC(int cc, int value, int sampleOffset = 0)
        {
            // CC1 (Mod Wheel) : source de modulation exposée dans la matrice. CC128+ / Aftertouch
            // canal (via CC message dédié) — l'hôte peut le passer via un CC personnalisé. On mappe
            // CC74 (Brightness) et CC71 (Resonance) sur les Aftertouch / ModWheel comme fallback.
            // v1 : seul CC1 est reconnu formellement, les autres sont ignorés (le mixer Koton
            // s'occupe déjà de CC7/CC10 via l'adaptateur host-side).
            if (cc == 1)
            {
                _modWheelGlobal = Math.Max(0, Math.Min(127, value)) / 127f;
                // Propager aux voix actives immédiatement (elles lisent leur propre copie par sample).
                if (_voices != null)
                    for (int i = 0; i < _voices.Length; i++)
                        if (_voices[i] != null) _voices[i].ModWheel = _modWheelGlobal;
            }
        }

        public void SetPitchBend(float value, int sampleOffset = 0)
        {
            float v = value < -1 ? -1 : (value > 1 ? 1 : value);
            _bendMul = (float)Math.Pow(2, (v * _bendRangeSemis) / 12.0);
        }

        public void Render(Span<float> left, Span<float> right)
        {
            int frames = left.Length;
            if (_voices == null || frames <= 0 || right.Length != frames)
            {
                left.Clear(); right.Clear();
                return;
            }

            // Snapshot des paramètres au début du buffer.
            var p = SnapshotParams();

            int poly = ActiveVoiceCount();

            for (int f = 0; f < frames; f++)
            {
                float sumL = 0f, sumR = 0f;
                for (int vi = 0; vi < poly; vi++)
                {
                    var voice = _voices[vi];
                    if (!voice.Active) continue;
                    voice.RenderSample(p, _modMatrix, out float vl, out float vr);
                    sumL += vl;
                    sumR += vr;
                }
                left[f] = sumL;
                right[f] = sumR;

                // Scope ring buffer : moyenne stéréo, sans lock (lecture ~30 Hz, races bénignes).
                _scope[_scopeWrite] = 0.5f * (sumL + sumR);
                _scopeWrite = (_scopeWrite + 1) % ScopeSize;
            }
        }

        WaveMorphParams SnapshotParams()
        {
            return new WaveMorphParams
            {
                W1Wave = WaveOsc.FromDouble(_w1Wave.Value),
                W2Wave = WaveOsc.FromDouble(_w2Wave.Value),
                W1AmpDb = (float)_w1Amp.Value,
                W2AmpDb = (float)_w2Amp.Value,
                W1DetuneCents = (float)_w1Detune.Value,
                W2DetuneCents = (float)_w2Detune.Value,
                W1Mult = FreqMult.GetFromDouble(_w1Mult.Value),
                W2Mult = FreqMult.GetFromDouble(_w2Mult.Value),
                XFade = (float)_xfade.Value,

                AmpAttackSec = (float)(_ampA.Value * 0.001),
                AmpDecaySec = (float)(_ampD.Value * 0.001),
                AmpSustain = (float)_ampS.Value,
                AmpReleaseSec = (float)(_ampR.Value * 0.001),

                E2AttackSec = (float)(_e2A.Value * 0.001),
                E2DecaySec = (float)(_e2D.Value * 0.001),
                E2Sustain = (float)_e2S.Value,
                E2ReleaseSec = (float)(_e2R.Value * 0.001),

                E3AttackSec = (float)(_e3A.Value * 0.001),
                E3DecaySec = (float)(_e3D.Value * 0.001),
                E3Sustain = (float)_e3S.Value,
                E3ReleaseSec = (float)(_e3R.Value * 0.001),

                Lfo1Shape = Lfo.ShapeFromDouble(_l1Shape.Value),
                Lfo2Shape = Lfo.ShapeFromDouble(_l2Shape.Value),
                Lfo1RateHz = (float)_l1Rate.Value,
                Lfo2RateHz = (float)_l2Rate.Value,
                Lfo1Amount = (float)_l1Amount.Value,
                Lfo2Amount = (float)_l2Amount.Value,

                F1Type = FilterTypeFromDouble(_f1Type.Value),
                F2Type = FilterTypeFromDouble(_f2Type.Value),
                F1Slope24 = _f1Slope.Value >= 0.5,
                F2Slope24 = _f2Slope.Value >= 0.5,
                F1Cutoff = _f1Cutoff.Value,
                F2Cutoff = _f2Cutoff.Value,
                F1Res = (float)_f1Res.Value,
                F2Res = (float)_f2Res.Value,
                F1DriveDb = (float)_f1Drive.Value,
                F2DriveDb = (float)_f2Drive.Value,
                F1Mix = (float)_f1Mix.Value,
                F2Mix = (float)_f2Mix.Value,
                ParallelRouting = _fRouting.Value >= 0.5,

                OutVolumeDb = (float)_outVol.Value,
                OutPan = (float)_outPan.Value,
                GlideMs = (float)_glide.Value,

                BendMul = _bendMul,
            };
        }

        static FilterType FilterTypeFromDouble(double v)
        {
            int i = (int)Math.Round(v);
            if (i < 0) i = 0;
            else if (i > 3) i = 3;
            return (FilterType)i;
        }

        // =============================================================================================
        // Oscilloscope + wave display helpers (utilisés par l'éditeur)
        // =============================================================================================

        /// <summary>Rend la forme d'onde THÉORIQUE d'un oscillateur (mult + detune ignorés — on
        /// affiche la forme primitive à 440 Hz étalée sur <paramref name="dest"/>). Utilisé par
        /// l'éditeur pour la mini-vue "Wave 1" / "Wave 2".</summary>
        public void GetOscWave(int oscIndex, float[] dest)
        {
            if (dest == null || dest.Length == 0) return;
            WavePrim w = oscIndex == 0 ? WaveOsc.FromDouble(_w1Wave.Value) : WaveOsc.FromDouble(_w2Wave.Value);
            const double cycles = 2.0;
            const double TwoPi = 2 * Math.PI;
            int n = dest.Length;
            for (int i = 0; i < n; i++)
            {
                double phase = (double)i / (n - 1) * cycles * TwoPi;
                dest[i] = WaveOsc.Sample(w, phase);
            }
        }

        /// <summary>Rend le RÉSULTAT du morphing (lerp XFade × amp) — vue "Résultat" centrale de
        /// l'éditeur. Affiche 2 cycles à 440 Hz, sans envelope ni filtres (capture statique du timbre).</summary>
        public void GetMorphWave(float[] dest)
        {
            if (dest == null || dest.Length == 0) return;
            WavePrim w1 = WaveOsc.FromDouble(_w1Wave.Value);
            WavePrim w2 = WaveOsc.FromDouble(_w2Wave.Value);
            double m1 = FreqMult.GetFromDouble(_w1Mult.Value);
            double m2 = FreqMult.GetFromDouble(_w2Mult.Value);
            float a1 = DbToLin((float)_w1Amp.Value);
            float a2 = DbToLin((float)_w2Amp.Value);
            float xf = (float)_xfade.Value;
            const double cycles = 2.0;
            const double TwoPi = 2 * Math.PI;
            int n = dest.Length;
            for (int i = 0; i < n; i++)
            {
                double basePhase = (double)i / (n - 1) * cycles * TwoPi;
                float w1v = WaveOsc.Sample(w1, basePhase * m1) * a1;
                float w2v = WaveOsc.Sample(w2, basePhase * m2) * a2;
                dest[i] = w1v + xf * (w2v - w1v);
            }
        }

        /// <summary>Copie le ring-buffer live du scope dans <paramref name="dest"/> — utilisé par
        /// la bande "Onde finale" en bas de l'éditeur. Contrairement à <see cref="GetMorphWave"/>,
        /// cette source montre le POST-processing complet (envelopes + filtres + volume) tel que
        /// le player produit à cet instant. Buffer vide quand aucune note ne joue.</summary>
        public void GetScopeSamples(float[] dest)
        {
            if (dest == null || dest.Length == 0) return;
            int n = dest.Length;
            int src = _scope.Length;
            // Lecture du plus ancien au plus récent (ring reordering).
            int start = _scopeWrite;
            float maxAbs = 0f;
            for (int i = 0; i < n; i++)
            {
                int srcIdx = (start + (int)((long)i * src / n)) % src;
                float s = _scope[srcIdx];
                dest[i] = s;
                float a = s < 0 ? -s : s;
                if (a > maxAbs) maxAbs = a;
            }
            // Fallback : si le buffer live est vide (aucune note ne joue → ligne plate), on affiche
            // l'onde THEORIQUE completement processee (morph + filtres + sustain amp + volume) —
            // comme si on avait une note tenue en sustain qui joue depuis quelques secondes.
            if (maxAbs < 0.001f) GetProcessedWave(dest);
        }

        /// <summary>Rend l'onde THEORIQUE complete "en regime permanent" : morphing brut passe a
        /// travers les 2 filtres (serie/parallele, 12 ou 24 dB) puis multiplie par le volume global
        /// et le niveau sustain de l'Amp env. Warmup interne de 2048 samples pour laisser les
        /// biquads converger avant de capturer les samples affiches — simule "une onde qui joue
        /// depuis quelques secondes". LFO/enveloppes/modulations ignoress (impossible sur snapshot
        /// statique) mais toute la chaine DSP est visible : l'utilisateur voit l'effet du LP a
        /// 100 Hz sur son square, l'effet d'un HP sur le sub, etc. — meme sans note qui joue.</summary>
        public void GetProcessedWave(float[] dest)
        {
            if (dest == null || dest.Length == 0) return;
            var p = SnapshotParams();
            int n = dest.Length;
            const int warmup = 2048;   // ~46 ms a 44.1 kHz — largement assez pour convergence biquad

            // Instancie 4 biquads locaux (2 etages par filtre pour la pente 24 dB). Configures une
            // fois, pas de recompute par sample (le vrai renderer fait pareil).
            var f1a = new BiquadFilter(_sampleRate);
            var f1b = new BiquadFilter(_sampleRate);
            var f2a = new BiquadFilter(_sampleRate);
            var f2b = new BiquadFilter(_sampleRate);
            double f1Freq = p.F1Cutoff;
            double f2Freq = p.F2Cutoff;
            double f1Q = Clamp01Static(p.F1Res) * 9.9 + 0.1;   // meme mapping que WaveMorphVoice
            double f2Q = Clamp01Static(p.F2Res) * 9.9 + 0.1;
            f1a.UpdateCoefs(p.F1Type, f1Freq, f1Q);
            f2a.UpdateCoefs(p.F2Type, f2Freq, f2Q);
            if (p.F1Slope24) f1b.UpdateCoefs(p.F1Type, f1Freq, f1Q);
            if (p.F2Slope24) f2b.UpdateCoefs(p.F2Type, f2Freq, f2Q);

            float w1AmpLin = DbToLin(p.W1AmpDb);
            float w2AmpLin = DbToLin(p.W2AmpDb);
            float f1DriveLin = DbToLin(p.F1DriveDb);
            float f2DriveLin = DbToLin(p.F2DriveDb);
            // Sustain amp × volume global = le niveau "en regime permanent" d'une note tenue.
            float finalGain = DbToLin(p.OutVolumeDb) * p.AmpSustain;

            // Synthese : 440 Hz continu. Warmup + n samples ; on n'ecrit dans dest que les n derniers.
            double phaseIncr = 2.0 * Math.PI * 440.0 / _sampleRate;
            double phase = 0;
            const double TwoPi = 2 * Math.PI;
            int total = warmup + n;

            for (int i = 0; i < total; i++)
            {
                float w1v = WaveOsc.Sample(p.W1Wave, phase * p.W1Mult) * w1AmpLin;
                float w2v = WaveOsc.Sample(p.W2Wave, phase * p.W2Mult) * w2AmpLin;
                float dry = w1v + p.XFade * (w2v - w1v);

                // F1 (drive + biquad(s) + mix)
                float f1In = SoftClipStatic(dry * f1DriveLin);
                float f1Wet = f1a.Process(f1In);
                if (p.F1Slope24) f1Wet = f1b.Process(f1Wet);
                float f1Out = dry + (f1Wet - dry) * p.F1Mix;

                float filtered;
                if (p.ParallelRouting)
                {
                    // Parallele : F1 et F2 recoivent dry, sommes moyennes (evite doublement d'amplitude)
                    float f2In = SoftClipStatic(dry * f2DriveLin);
                    float f2Wet = f2a.Process(f2In);
                    if (p.F2Slope24) f2Wet = f2b.Process(f2Wet);
                    float f2Out = dry + (f2Wet - dry) * p.F2Mix;
                    filtered = 0.5f * (f1Out + f2Out);
                }
                else
                {
                    // Serie : F1 output → F2. Meme ordre que WaveMorphVoice.
                    float f2SerialIn = SoftClipStatic(f1Out * f2DriveLin);
                    float f2Wet = f2a.Process(f2SerialIn);
                    if (p.F2Slope24) f2Wet = f2b.Process(f2Wet);
                    filtered = f1Out + (f2Wet - f1Out) * p.F2Mix;
                }

                float sample = filtered * finalGain;
                if (i >= warmup) dest[i - warmup] = sample;

                phase += phaseIncr;
                if (phase > TwoPi) phase -= TwoPi;
            }
        }

        static float Clamp01Static(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        // tanh soft-clip approximation rationnelle — meme formule que WaveMorphVoice.SoftClip.
        static float SoftClipStatic(float x)
        {
            float a = x * x;
            return x * (27f + a) / (27f + 9f * a);
        }

        static float DbToLin(float db) => (float)Math.Pow(10.0, db / 20.0);

        // =============================================================================================
        // Persistance
        // =============================================================================================

        const int SaveFormatVersion = 1;

        public byte[] SaveState()
        {
            var slots = _modMatrix.Snapshot();
            var slotsSerialized = new List<Dictionary<string, object>>(slots.Count);
            foreach (var s in slots)
            {
                slotsSerialized.Add(new Dictionary<string, object>
                {
                    ["src"] = s.Src.ToString(),
                    ["tgt"] = s.Tgt.ToString(),
                    ["amt"] = s.Amount,
                });
            }

            var paramsDict = new Dictionary<string, double>(_params.Count);
            foreach (var p in _params) paramsDict[p.Id] = p.Value;

            var doc = new Dictionary<string, object>
            {
                ["v"] = SaveFormatVersion,
                ["params"] = paramsDict,
                ["mod_matrix"] = slotsSerialized,
            };
            var opts = new JsonSerializerOptions { WriteIndented = false };
            return System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc, opts));
        }

        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                using var doc = JsonDocument.Parse(state);
                var root = doc.RootElement;

                // Paramètres : dispatch par Id, tolère les inconnus.
                if (root.TryGetProperty("params", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kp in paramsEl.EnumerateObject())
                    {
                        if (!kp.Value.TryGetDouble(out double val)) continue;
                        for (int i = 0; i < _params.Count; i++)
                        {
                            if (string.Equals(_params[i].Id, kp.Name, StringComparison.Ordinal))
                            {
                                _params[i].Value = val;
                                break;
                            }
                        }
                    }
                }

                // Mod matrix : reconstruit depuis la liste (tolère les enums inconnus par TryParse).
                if (root.TryGetProperty("mod_matrix", out var mmEl) && mmEl.ValueKind == JsonValueKind.Array)
                {
                    var newSlots = new List<ModSlot>();
                    foreach (var slotEl in mmEl.EnumerateArray())
                    {
                        if (slotEl.ValueKind != JsonValueKind.Object) continue;
                        if (!slotEl.TryGetProperty("src", out var srcEl) ||
                            !slotEl.TryGetProperty("tgt", out var tgtEl) ||
                            !slotEl.TryGetProperty("amt", out var amtEl)) continue;
                        if (!Enum.TryParse(typeof(ModSource), srcEl.GetString(), out object srcObj)) continue;
                        if (!Enum.TryParse(typeof(ModTarget), tgtEl.GetString(), out object tgtObj)) continue;
                        if (!amtEl.TryGetDouble(out double amt)) continue;
                        newSlots.Add(new ModSlot((ModSource)srcObj, (ModTarget)tgtObj, (float)amt));
                    }
                    _modMatrix.Assign(newSlots);
                }
            }
            catch
            {
                // Blob corrompu : garder les défauts, ne pas jeter — un projet ne doit pas casser à
                // cause d'un plugin qui a changé de format.
            }
        }

        public void Dispose()
        {
            _voices = null;
        }
    }
}
