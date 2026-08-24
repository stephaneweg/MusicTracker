using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginGuqin
{
    /// <summary>
    /// Guqin (古琴, cithare chinoise a 7 cordes) — Karplus-Strong etendu avec sustain long et
    /// GLISSANDO inter-notes (respectant la direction montante ou descendante). Timbre : corde
    /// grattee/pincee avec long chant + inflexions typiques du jeu de main gauche (yin/nao).
    ///
    /// **Architecture** :
    /// - Voice MONOphonique par defaut : quand une nouvelle note arrive alors qu'une note est encore
    ///   active (legato), on ne re-pluck PAS, on GLIDE la frequence de lecture du delay-line de
    ///   l'ancienne vers la nouvelle sur GlideMs (par defaut 120ms). L'attaque n'est jouee que sur
    ///   la premiere note ou apres un vrai NoteOff → NoteOn. C'est ce qui donne le "glissando" du
    ///   guqin, respectant naturellement la direction (interpolation en semi-tons = log = sens
    ///   correct montant/descendant).
    /// - Karplus-Strong avec feedback tres eleve (0.995..0.999) → sustain 5-15 secondes.
    /// - LP dans le feedback (damping) controle par HFDamping : bas = brillant, haut = feutre.
    /// - Excitation : burst de bruit blanc court (~10ms) filtre en LP par PluckBrightness.
    ///
    /// **Contexte** : plein potentiel avec ShimmerSparkle en insert + un peu de reverb longue.
    /// </summary>
    [KotonInstrument("Guqin", Id = "koton.guqin", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class GuqinPlugin : IKotonInstrument
    {
        public string Id => "koton.guqin";
        public string DisplayName => "Guqin";

        readonly KotonParameter _sustain         = new KotonParameter("sustain",        "Sustain",        0.0, 1.0, 0.85);
        readonly KotonParameter _hfDamping       = new KotonParameter("hf_damping",     "HF damping",     0.0, 1.0, 0.20);
        readonly KotonParameter _pluckBrightness = new KotonParameter("pluck_brightness","Pluck brightness", 0.0, 1.0, 0.55);
        readonly KotonParameter _pluckLength     = new KotonParameter("pluck_length",   "Pluck length",   3.0, 40.0, 12.0, "ms");
        readonly KotonParameter _bodyResonance   = new KotonParameter("body_resonance", "Body resonance", 0.0, 1.0, 0.35);

        readonly KotonParameter _glideMs         = new KotonParameter("glide_ms",       "Glide time",     0.0, 800.0, 120.0, "ms");
        readonly KotonParameter _glideCurve      = new KotonParameter("glide_curve",    "Glide curve",    0.0, 1.0, 0.6);

        readonly KotonParameter _vibratoRate     = new KotonParameter("vib_rate",       "Vibrato rate",   0.0, 8.0, 3.5, "Hz");
        readonly KotonParameter _vibratoDepth    = new KotonParameter("vib_depth",      "Vibrato depth",  0.0, 60.0, 15.0, "cent");

        readonly KotonParameter _attackMs        = new KotonParameter("attack",         "Attack",         1.0, 200.0, 5.0, "ms");
        readonly KotonParameter _releaseMs       = new KotonParameter("release",        "Release",        50.0, 3000.0, 800.0, "ms");

        readonly KotonParameter _polyphony       = new KotonParameter("polyphony",      "Polyphony",      1, 6, 1) { Automatable = false };
        readonly KotonParameter _volumeDb        = new KotonParameter("volume",         "Volume",         -30.0, 6.0, -3.0, "dB");
        // Ré-attaque périodique : rejoue la note tenue tous les 1/taux de seconde.
        // 0 Hz = une seule attaque, donc aucun projet existant ne change.
        readonly KotonStudio.Plugins.Shared.KotonReAttack _retrig =
            new KotonStudio.Plugins.Shared.KotonReAttack("Trémolo", 20.0, 0.0);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        GuqinVoice[] _voices;
        int _stealCursor;
        const int MaxPoly = 6;

        public GuqinPlugin()
        {
            _params = new List<KotonParameter> {
                _sustain, _hfDamping, _pluckBrightness, _pluckLength, _bodyResonance,
                _glideMs, _glideCurve,
                _vibratoRate, _vibratoDepth,
                _attackMs, _releaseMs, _polyphony, _volumeDb, _retrig.Rate };
        }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new GuqinEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _voices = new GuqinVoice[MaxPoly];
            for (int i = 0; i < MaxPoly; i++) _voices[i] = new GuqinVoice(sampleRate);
            _retrig.Prepare(sampleRate);
        }
        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
            _retrig.Reset();
        }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            _retrig.NoteOn(note, velocity);
            if (_voices == null || velocity == 0) return;
            float vel = velocity / 127f;
            int poly = Math.Max(1, Math.Min(MaxPoly, (int)Math.Round(_polyphony.Value)));

            // MONO / faible poly : si une voix est ACTIVE (sustaining ou release) on prend CELLE-LA
            // et on lui envoie NoteOn en mode LEGATO → glide de sa freq courante vers la nouvelle.
            // Si toutes les voix sont libres, on prend la premiere libre = pluck neuf.
            if (poly == 1)
            {
                var v = _voices[0];
                // …SAUF si c'est la MEME note : un glissando vers la hauteur deja jouee ne produit
                // rien du tout. Rejouer la note, c'est la repincer — vrai pour un trille de la main
                // droite comme pour la re-attaque periodique.
                if (v.IsActive && v.Note != note) v.NoteOnLegato(note, vel, BuildParams());
                else v.NoteOnPluck(note, vel, BuildParams());
                return;
            }

            // Poly : voice-stealing FIFO, mais on tente d'abord une voix libre.
            GuqinVoice target = null;
            // Rejouer la MÊME note reprend sa voix au lieu d'en allouer une neuve : sans ça les coups
            // répétés s'empilent (mesure : pic 0,33 → 0,68 à 9 coups/s). C'est aussi le comportement
            // physique — repincer une corde déjà en vibration l'arrête.
            for (int i = 0; i < poly; i++) if (_voices[i].IsActive && _voices[i].Note == note) { target = _voices[i]; target.Kill(); break; }
            if (target == null) for (int i = 0; i < poly; i++) if (!_voices[i].IsActive) { target = _voices[i]; break; }
            if (target == null) { target = _voices[_stealCursor]; _stealCursor = (_stealCursor + 1) % poly; target.Kill(); }
            target.NoteOnPluck(note, vel, BuildParams());
        }
        public void NoteOff(int note, int sampleOffset = 0)
        {
            _retrig.NoteOff(note);
            if (_voices == null) return;
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].IsActive && _voices[i].Note == note) _voices[i].NoteOff();
        }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        GuqinParams BuildParams() => new GuqinParams
        {
            Sustain = (float)_sustain.Value,
            HFDamping = (float)_hfDamping.Value,
            PluckBrightness = (float)_pluckBrightness.Value,
            PluckLengthMs = (float)_pluckLength.Value,
            BodyResonance = (float)_bodyResonance.Value,
            GlideMs = (float)_glideMs.Value,
            GlideCurve = (float)_glideCurve.Value,
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
                // Ré-attaque : à l'échéance, la note tenue est rejouée (BeginStroke neutralise
                // la notification que NoteOn va renvoyer à l'engin).
                if (_retrig.Tick()) { _retrig.BeginStroke(); for (int rt = 0; rt < _retrig.Count; rt++) NoteOn(_retrig.NoteAt(rt), _retrig.VelocityAt(rt)); _retrig.EndStroke(); }

                float sum = 0f;
                for (int v = 0; v < _voices.Length; v++)
                    if (_voices[v].IsActive) sum += _voices[v].RenderSample(p);
                float s = sum * volLin;
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                left[i] = s; right[i] = s;
            }
        }

        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] state) { if (state == null || state.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }

        // Presets
        public static readonly string[] PresetNames = { "Guqin classique (soie, mediatif)", "Guqin percussif (metal, punchy)", "Guqin drone (sustain long)", "Guqin harmonique (aigu doux)" };
        //                                sus  hfD  plkBr plkLen bodyR glide gcv vibR vibD atk rel poly vol
        static readonly double[,] PresetValues = {
            /*Classique*/                { 0.85, 0.20, 0.55, 12, 0.35, 120, 0.6, 3.5, 15, 5, 800, 1, -3 },
            /*Percussif*/                { 0.75, 0.15, 0.80, 6,  0.20, 60,  0.4, 4.0, 8,  3, 400, 1, -3 },
            /*Drone*/                    { 0.98, 0.10, 0.40, 20, 0.55, 200, 0.7, 3.0, 20, 10, 2500, 1, -4 },
            /*Harmonique*/               { 0.90, 0.30, 0.35, 15, 0.15, 100, 0.6, 3.5, 12, 5, 1200, 1, -3 },
        };
        public void LoadPreset(int i)
        {
            if (i < 0 || i >= PresetValues.GetLength(0)) return;
            _sustain.Value = PresetValues[i, 0]; _hfDamping.Value = PresetValues[i, 1];
            _pluckBrightness.Value = PresetValues[i, 2]; _pluckLength.Value = PresetValues[i, 3];
            _bodyResonance.Value = PresetValues[i, 4];
            _glideMs.Value = PresetValues[i, 5]; _glideCurve.Value = PresetValues[i, 6];
            _vibratoRate.Value = PresetValues[i, 7]; _vibratoDepth.Value = PresetValues[i, 8];
            _attackMs.Value = PresetValues[i, 9]; _releaseMs.Value = PresetValues[i, 10];
            _polyphony.Value = PresetValues[i, 11]; _volumeDb.Value = PresetValues[i, 12];
        }
    }

    internal struct GuqinParams
    {
        public float Sustain, HFDamping, PluckBrightness, PluckLengthMs, BodyResonance;
        public float GlideMs, GlideCurve;
        public float VibratoRate, VibratoDepthCents;
        public float AttackMs, ReleaseMs;
    }
}
