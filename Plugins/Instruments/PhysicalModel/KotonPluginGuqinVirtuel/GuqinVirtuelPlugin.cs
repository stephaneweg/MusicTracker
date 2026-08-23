using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginGuqinVirtuel
{
    /// <summary>
    /// Guqin virtuel — instrument physique (Karplus) + couche de CONTRAINTE PLAYABILITY : chaque
    /// NoteOn est résolu vers un fingering (corde × position), soumis à la règle « max N doigts
    /// dans un empan physique de ≤ N cm ». Notes qui ne rentrent pas dans le modèle du guqin
    /// (hors gamme des cordes+hui) sont soit snappées à la plus proche, soit rejetées, selon
    /// l'option Snap.
    ///
    /// **Visualisation** (P1 basique) : l'éditeur peint 7 lignes horizontales (cordes) et 13
    /// marques hui, avec un rond lumineux au point de doigté à chaque note frappée. La vibration
    /// animée de la corde arrive en P2.
    ///
    /// **Glissando** (P3) : détection de deux notes voisines sur la même corde en legato →
    /// interprétées comme slide (delay-line qui glisse). Pour l'instant : notes discrètes uniquement.
    /// </summary>
    [KotonInstrument("Guqin virtuel", Id = "koton.guqin_virtuel", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class GuqinVirtuelPlugin : IKotonInstrument
    {
        public string Id => "koton.guqin_virtuel";
        public string DisplayName => "Guqin virtuel";

        // ---- Modèle guqin ----
        readonly KotonParameter _diapasonCm       = new KotonParameter("diapason_cm",     "Diapason",         50, 150, 110, "cm");
        readonly KotonParameter _spanCm           = new KotonParameter("span_cm",         "Empan main",       5, 30, 15, "cm");
        readonly KotonParameter _maxFingers       = new KotonParameter("max_fingers",     "Max doigts",       2, 5, 4);
        readonly KotonParameter _tuning           = new KotonParameter("tuning",          "Accordage",        0, 3, 0);   // index dans GuqinModel.AllTunings
        // Snap : 0 = rejeter les notes hors modèle, 1 = snap à la plus proche (défaut)
        readonly KotonParameter _snapMode         = new KotonParameter("snap_mode",       "Snap hors gamme",  0, 1, 1);

        // ---- DSP (mêmes params que le Guqin original) ----
        readonly KotonParameter _sustain          = new KotonParameter("sustain",         "Sustain",          0.0, 1.0, 0.85);
        readonly KotonParameter _hfDamping        = new KotonParameter("hf_damping",      "HF damping",       0.0, 1.0, 0.20);
        readonly KotonParameter _pluckBrightness  = new KotonParameter("pluck_brightness","Pluck brightness", 0.0, 1.0, 0.55);
        readonly KotonParameter _pluckLength      = new KotonParameter("pluck_length",    "Pluck length",     3.0, 40.0, 12.0, "ms");
        readonly KotonParameter _bodyResonance    = new KotonParameter("body_resonance",  "Body resonance",   0.0, 1.0, 0.35);
        readonly KotonParameter _vibratoRate      = new KotonParameter("vib_rate",        "Vibrato rate",     0.0, 8.0, 3.5, "Hz");
        readonly KotonParameter _vibratoDepth     = new KotonParameter("vib_depth",       "Vibrato depth",    0.0, 60.0, 15.0, "cent");
        readonly KotonParameter _attackMs         = new KotonParameter("attack",          "Attack",           1.0, 200.0, 5.0, "ms");
        readonly KotonParameter _releaseMs        = new KotonParameter("release",         "Release",          50.0, 3000.0, 800.0, "ms");
        readonly KotonParameter _volumeDb         = new KotonParameter("volume",          "Volume",           -30.0, 6.0, -3.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        GuqinVirtuelVoice[] _voices;
        int _stealCursor;
        const int MaxPoly = 7;   // physique : 7 cordes maximum simultanément

        readonly GuqinConstraint _constraint = new GuqinConstraint();

        // Callback pour la viz — l'éditeur s'y abonne pour afficher le rond de doigté.
        // Struct pass-by-value : le handler peut stocker en snapshot sans race.
        public struct StruckEvent
        {
            public int StringIdx;
            public double Position;
            public int Midi;
            public float Velocity;
        }
        public event Action<StruckEvent> NoteStruck;
        public event Action<int> NoteReleased;   // MIDI

        public GuqinVirtuelPlugin()
        {
            _params = new List<KotonParameter>
            {
                _diapasonCm, _spanCm, _maxFingers, _tuning, _snapMode,
                _sustain, _hfDamping, _pluckBrightness, _pluckLength, _bodyResonance,
                _vibratoRate, _vibratoDepth, _attackMs, _releaseMs, _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new GuqinVirtuelEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _voices = new GuqinVirtuelVoice[MaxPoly];
            for (int i = 0; i < MaxPoly; i++) _voices[i] = new GuqinVirtuelVoice(sampleRate);
        }

        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
            _constraint.Clear();
        }

        internal GuqinModel.Tuning ActiveTuning
        {
            get
            {
                int idx = (int)Math.Max(0, Math.Min(GuqinModel.AllTunings.Length - 1, (int)_tuning.Value));
                return GuqinModel.AllTunings[idx];
            }
        }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            float vel = velocity / 127f;

            // Sync des paramètres avec la contrainte.
            _constraint.DiapasonCm = _diapasonCm.Value;
            _constraint.MaxSpanCm = _spanCm.Value;
            _constraint.MaxStoppedFingers = Math.Max(1, Math.Min(5, (int)_maxFingers.Value));

            // Résolution MIDI → (corde, position).
            var fingering = GuqinModel.ResolveMidi(ActiveTuning, note, out bool exact);
            bool snap = _snapMode.Value >= 0.5;
            if (!exact && !snap) return;   // hors gamme + snap désactivé → rejeté silencieusement

            // Contrainte de polyphonie.
            var decision = _constraint.Consider(fingering.StringIdx, fingering.Position, out var toRelease);
            if (decision == GuqinConstraint.Decision.RejectStringBusy) return;
            if (decision == GuqinConstraint.Decision.StealOldest && toRelease != null)
            {
                // Libère la voix qui portait la note volée.
                for (int i = 0; i < _voices.Length; i++)
                    if (_voices[i].IsActive && _voices[i].Note == toRelease.Midi) { _voices[i].Kill(); break; }
                _constraint.Release(toRelease);
                try { NoteReleased?.Invoke(toRelease.Midi); } catch { }
            }

            // Trouve une voix libre (ou vole la plus vieille).
            GuqinVirtuelVoice target = null;
            for (int i = 0; i < _voices.Length; i++) if (!_voices[i].IsActive) { target = _voices[i]; break; }
            if (target == null)
            {
                target = _voices[_stealCursor];
                _stealCursor = (_stealCursor + 1) % _voices.Length;
                target.Kill();
            }

            int playedMidi = fingering.Midi;   // pitch effectivement joué (snap possible)
            target.NoteOnPluck(playedMidi, vel, BuildParams());
            _constraint.Register(playedMidi, fingering.StringIdx, fingering.Position);

            try { NoteStruck?.Invoke(new StruckEvent
            {
                StringIdx = fingering.StringIdx,
                Position = fingering.Position,
                Midi = playedMidi,
                Velocity = vel,
            }); } catch { }
        }

        public void NoteOff(int note, int sampleOffset = 0)
        {
            if (_voices == null) return;
            // Le note-off arrive avec le MIDI d'ENTRÉE. Comme on a pu snap le pitch, on cherche la
            // voix la plus PROCHE en pitch qui soit encore active — pas ideal si on a joué la même
            // note d'entree deux fois de suite avec 2 snappings différents, mais suffisant.
            int bestIdx = -1, bestDist = int.MaxValue;
            for (int i = 0; i < _voices.Length; i++)
            {
                if (!_voices[i].IsActive) continue;
                int d = Math.Abs(_voices[i].Note - note);
                if (d < bestDist) { bestDist = d; bestIdx = i; }
            }
            if (bestIdx >= 0 && bestDist <= 3)
            {
                int m = _voices[bestIdx].Note;
                _voices[bestIdx].NoteOff();
                _constraint.Release(m);
                try { NoteReleased?.Invoke(m); } catch { }
            }
        }

        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        GvParams BuildParams() => new GvParams
        {
            Sustain = (float)_sustain.Value,
            HFDamping = (float)_hfDamping.Value,
            PluckBrightness = (float)_pluckBrightness.Value,
            PluckLengthMs = (float)_pluckLength.Value,
            BodyResonance = (float)_bodyResonance.Value,
            VibratoRate = (float)_vibratoRate.Value,
            VibratoDepthCents = (float)_vibratoDepth.Value,
            AttackMs = (float)_attackMs.Value,
            ReleaseMs = (float)_releaseMs.Value,
        };

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            var p = BuildParams();
            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                for (int v = 0; v < _voices.Length; v++)
                    if (_voices[v].IsActive) sum += _voices[v].RenderSample(p);
                float s = sum * volLin;
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                left[i] = s; right[i] = s;
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
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }
    }
}
