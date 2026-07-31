using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginFmSynth
{
    /// <summary>
    /// Synthétiseur FM 2-opérateurs polyphonique — implémentation de référence du framework Koton
    /// natif. Objectif : montrer bout à bout ce qu'un plugin Koton doit fournir (paramètres, presets,
    /// éditeur WPF, sauvegarde d'état) tout en produisant un son réellement utilisable.
    ///
    /// **Architecture** : jusqu'à 8 voix, chacune est un couple carrier + modulator. La formule de
    /// synthèse et l'enveloppe sont dans <see cref="FmVoice"/>. Les paramètres du plugin (ratio, index,
    /// enveloppe ADSR, volume, LFO) sont lus À CHAQUE Render — bouger un slider pendant la lecture
    /// s'entend au buffer suivant (latence ~10-30 ms + latence LookaheadBuffer côté hôte ~500 ms).
    ///
    /// **Presets** : 8 sons prêts à l'emploi (voir <see cref="FmPresets.All"/>). Le combo dans l'éditeur
    /// pose le preset ; les valeurs de paramètres sont posées sur les KotonParameter (l'éditeur suit).
    ///
    /// **Éditeur** : <see cref="FmSynthEditor"/> — grille de sliders + zone oscilloscope 200×80 qui
    /// affiche les derniers samples émis. Le ring buffer est alimenté par <see cref="Render"/> et lu
    /// par l'oscilloscope via un timer 30Hz sans lock (races bénignes acceptées — c'est de l'affichage).
    ///
    /// **Sauvegarde** : JSON UTF-8 contenant l'index de preset + les valeurs de chaque paramètre par
    /// Id. LoadState ignore les champs inconnus et garde les défauts pour les manquants.
    /// </summary>
    [KotonInstrument("FM Synth", Id = "koton.fm", Category = "Synth", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class FmSynthPlugin : IKotonInstrument
    {
        public string Id => "koton.fm";
        public string DisplayName => "FM Synth";

        // ---- Paramètres exposés ----
        // Ordre = ordre d'affichage dans l'éditeur. Les Ids servent aussi de clés JSON dans SaveState.
        // Les temps ADSR sont exposés en MILLISECONDES (unité usuelle dans un DAW / synthé),
        // convertis en secondes pour la voix (formule DSP) via *0.001 au Render.
        //
        // Les 2 paramètres discrets `mod_wave` et `car_wave` sont des entiers 0..N-1 stockés en double
        // (mapped à <see cref="FmWaveform"/>). L'éditeur les rend en ComboBox — la convention =
        // Id se termine par `_wave`. Défaut = 0 (Sine) → tous les presets historiques (qui n'incluent
        // pas de forme d'onde) restent bit-parfaits.
        readonly KotonParameter _ratio    = new KotonParameter("ratio",    "Ratio",       0.5, 8.0,  1.0);
        readonly KotonParameter _index    = new KotonParameter("index",    "Index",       0.0, 10.0, 3.0);
        readonly KotonParameter _modWave  = new KotonParameter("mod_wave", "Mod Wave",    0.0, Osc.Count - 1, 0.0);
        readonly KotonParameter _carWave  = new KotonParameter("car_wave", "Car Wave",    0.0, Osc.Count - 1, 0.0);
        readonly KotonParameter _feedback = new KotonParameter("feedback", "Feedback",    0.0, 1.0,  0.0);
        // `algo` : 0 = FM (par défaut, historique) ; 1 = Additif (les 2 opérateurs sortent en
        // parallèle). Défaut = 0 → tous les projets sauvés avec l'ancienne version restent bit-parfaits.
        readonly KotonParameter _algo     = new KotonParameter("algo",     "Algorithme",  0.0, 1.0,  0.0);
        readonly KotonParameter _attack   = new KotonParameter("attack",   "Attaque",     1.0, 2000.0,  10.0, "ms");
        readonly KotonParameter _decay    = new KotonParameter("decay",    "Decay",       1.0, 2000.0, 300.0, "ms");
        readonly KotonParameter _sustain  = new KotonParameter("sustain",  "Sustain",     0.0, 1.0,  0.6);
        readonly KotonParameter _release  = new KotonParameter("release",  "Release",     1.0, 4000.0, 400.0, "ms");
        readonly KotonParameter _volume   = new KotonParameter("volume",   "Volume",      0.0, 1.0,  0.75, "%");
        readonly KotonParameter _lfoRate  = new KotonParameter("lfo_rate", "LFO Rate",    0.1, 20.0, 3.0, "Hz");
        readonly KotonParameter _lfoDepth = new KotonParameter("lfo_depth","LFO Depth",   0.0, 1.0,  0.1);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        // ---- Voix polyphoniques ----
        // 8 voix = compromis raisonnable pour un synthé simple. Une nouvelle NoteOn quand toutes sont
        // occupées vole la plus ancienne (voice stealing FIFO trivial : pas de release de la voix volée,
        // juste rebasculée). Suffisant pour de la démo.
        const int MaxVoices = 8;
        FmVoice[] _voices;
        int _voiceRR;    // round-robin pour le voice stealing

        int _sampleRate = 44100;
        int _currentPreset = -1;
        float _bendMul = 1f;   // multiplicateur de fréquence du pitch bend (2^(semitones/12))
        float _bendRangeSemis = 2f;

        // ---- Oscilloscope ring buffer ----
        // ~1024 samples, alimenté par Render, lu par l'éditeur via GetOscilloscopeSamples. Pas de lock :
        // un race donne au pire une frame d'affichage un peu déchirée, l'oscilloscope se rafraîchit
        // 30 fois par seconde, aucun humain ne le voit.
        internal const int ScopeSize = 1024;
        readonly float[] _scope = new float[ScopeSize];
        int _scopeWrite;

        public FmSynthPlugin()
        {
            // Ordre de déclaration = ordre d'affichage. Les 2 combos de waveform sont placés juste
            // après Ratio/Index pour rester proches des params qu'ils influencent tonalement.
            _params = new List<KotonParameter>
            {
                _ratio, _index, _modWave, _carWave, _feedback, _algo,
                _attack, _decay, _sustain, _release, _volume, _lfoRate, _lfoDepth
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new FmSynthEditor(this);

        // ---- Cycle audio ----

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate <= 0 ? 44100 : sampleRate;
            _voices = new FmVoice[MaxVoices];
            for (int i = 0; i < MaxVoices; i++) _voices[i] = new FmVoice(_sampleRate);
            _voiceRR = 0;
        }

        public void Reset()
        {
            if (_voices != null)
                for (int i = 0; i < _voices.Length; i++) _voices[i]?.Reset();
            _bendMul = 1f;
            Array.Clear(_scope, 0, _scope.Length);
        }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null) return;
            // 1. Trouver une voix inactive.
            int slot = -1;
            for (int i = 0; i < _voices.Length; i++)
            {
                if (!_voices[i].Active) { slot = i; break; }
            }
            // 2. Sinon voler la round-robin (vieille FIFO — un pad qui recouvre en permanence perdrait
            // sa voix la plus vieille, c'est le comportement attendu d'un synthé mono-timbral simple).
            if (slot == -1)
            {
                slot = _voiceRR;
                _voiceRR = (_voiceRR + 1) % _voices.Length;
            }
            _voices[slot].NoteOn(note, velocity <= 0 ? 100 : velocity);
        }

        public void NoteOff(int note, int sampleOffset = 0)
        {
            if (_voices == null) return;
            for (int i = 0; i < _voices.Length; i++)
            {
                if (_voices[i].Active && _voices[i].Note == note && _voices[i].Active)
                    _voices[i].NoteOff();
            }
        }

        public void MidiCC(int cc, int value, int sampleOffset = 0)
        {
            // v1 : on ne consomme aucun CC (le volume vient du paramètre Volume, pas de CC7 ni CC10).
            // Un futur enrichissement pourrait binder CC1 sur le LFO Depth, CC7 sur le volume, etc.
            // CC 123 (All Notes Off) est déjà traité par l'adaptateur via Reset().
        }

        public void SetPitchBend(float value, int sampleOffset = 0)
        {
            // value in -1..+1, mappé à ±_bendRangeSemis demi-tons.
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

            // Snapshot des paramètres AU DÉBUT du buffer. Un slider bougé pendant ce buffer ne s'entend
            // qu'au prochain — c'est OK à 10-30 ms de buffer typique (largement < seuil de perception).
            float ratio    = (float)_ratio.Value;
            float index    = (float)_index.Value;
            FmWaveform modWave = Osc.FromDouble(_modWave.Value);
            FmWaveform carWave = Osc.FromDouble(_carWave.Value);
            float feedback = (float)_feedback.Value;
            // `algo` est stocké comme double 0..1 ; > 0.5 = additif. Un ComboBox le pose à 0 ou 1
            // exact, un slider externe (automation) qui l'écrase à 0.6 est traité comme "additif".
            bool additive  = _algo.Value >= 0.5;
            // ms → s pour l'enveloppe DSP (les KotonParameter sont exposés en ms côté UI, plus lisible).
            float attack   = (float)(_attack.Value * 0.001);
            float decay    = (float)(_decay.Value * 0.001);
            float sustain  = (float)_sustain.Value;
            float release  = (float)(_release.Value * 0.001);
            float volume   = (float)_volume.Value;
            float lfoRate  = (float)_lfoRate.Value;
            float lfoDepth = (float)_lfoDepth.Value;
            float bendMul  = _bendMul;

            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    var voice = _voices[v];
                    if (!voice.Active) continue;
                    sum += voice.RenderSample(ratio, index, lfoRate, lfoDepth,
                                              attack, decay, sustain, release, bendMul,
                                              modWave, carWave, feedback, additive);
                }
                float s = sum * volume;
                left[f] = s;
                right[f] = s;

                // Oscilloscope : écrire dans le ring buffer sans lock (l'éditeur lit ~30 fois/sec,
                // les races sont invisibles).
                _scope[_scopeWrite] = s;
                _scopeWrite = (_scopeWrite + 1) % ScopeSize;
            }
        }

        // ---- Presets ----

        /// <summary>Applique un preset sur les paramètres du plugin. Les KotonParameter.Value posent
        /// l'événement Changed → l'éditeur rafraîchit ses sliders automatiquement.</summary>
        public void LoadPreset(int index)
        {
            if (index < 0 || index >= FmPresets.All.Count) return;
            var p = FmPresets.All[index];
            _ratio.Value    = p.Ratio;
            _index.Value    = p.Index;
            _modWave.Value  = (double)p.ModWave;
            _carWave.Value  = (double)p.CarWave;
            _feedback.Value = p.Feedback;
            _algo.Value     = p.Additive ? 1.0 : 0.0;
            _attack.Value   = p.Attack;
            _decay.Value    = p.Decay;
            _sustain.Value  = p.Sustain;
            _release.Value  = p.Release;
            _volume.Value   = p.Volume;
            _lfoRate.Value  = p.LfoRate;
            _lfoDepth.Value = p.LfoDepth;
            _currentPreset = index;
        }

        /// <summary>Index du dernier preset chargé (-1 si aucun ou si les valeurs ont divergé — v1 ne
        /// track pas les modifications post-chargement, mais l'éditeur peut afficher le nom du dernier
        /// preset choisi comme repère).</summary>
        public int CurrentPreset => _currentPreset;

        /// <summary>Nom des presets pour peupler le combo de l'éditeur.</summary>
        public IReadOnlyList<string> PresetNames
        {
            get
            {
                var arr = new string[FmPresets.All.Count];
                for (int i = 0; i < arr.Length; i++) arr[i] = FmPresets.All[i].Name;
                return arr;
            }
        }

        // ---- Oscilloscope ----

        /// <summary>Copie le contenu du ring buffer dans <paramref name="dest"/> (samples les plus
        /// anciens en premier, les plus récents en dernier). Doit être appelé depuis le thread UI
        /// (l'éditeur), sans lock — races bénignes sur le contenu individuel des cases mais la copie
        /// entière reste cohérente au sens visuel.</summary>
        public void GetOscilloscopeSamples(float[] dest)
        {
            if (dest == null) return;
            int n = Math.Min(dest.Length, ScopeSize);
            int start = _scopeWrite;   // le prochain slot à écrire = le plus ancien à lire
            for (int i = 0; i < n; i++)
            {
                dest[i] = _scope[(start + i) % ScopeSize];
            }
        }

        // ---- Persistance ----

        // Format du JSON de sauvegarde. Bump la version si le format évolue au-delà du "ajouter des
        // clés optionnelles" — LoadState ignore les inconnues, ce qui laisse pas mal de marge sans
        // vraie migration.
        const int SaveFormatVersion = 1;

        public byte[] SaveState()
        {
            var doc = new Dictionary<string, object>
            {
                ["v"] = SaveFormatVersion,
                ["preset"] = _currentPreset,
                ["params"] = new Dictionary<string, double>
                {
                    [_ratio.Id]    = _ratio.Value,
                    [_index.Id]    = _index.Value,
                    [_modWave.Id]  = _modWave.Value,
                    [_carWave.Id]  = _carWave.Value,
                    [_feedback.Id] = _feedback.Value,
                    [_algo.Id]     = _algo.Value,
                    [_attack.Id]   = _attack.Value,
                    [_decay.Id]    = _decay.Value,
                    [_sustain.Id]  = _sustain.Value,
                    [_release.Id]  = _release.Value,
                    [_volume.Id]   = _volume.Value,
                    [_lfoRate.Id]  = _lfoRate.Value,
                    [_lfoDepth.Id] = _lfoDepth.Value,
                }
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
                // Preset : optionnel — un blob v2 sans preset (si un futur enrichit avec des banques
                // externes) reste chargeable. On garde -1 si absent.
                if (root.TryGetProperty("preset", out var presetEl) && presetEl.TryGetInt32(out int p))
                    _currentPreset = p;
                if (root.TryGetProperty("params", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kp in paramsEl.EnumerateObject())
                    {
                        double val;
                        if (!kp.Value.TryGetDouble(out val)) continue;
                        // Dispatch par Id — un Id inconnu (paramètre supprimé d'une v2 future) est ignoré.
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
