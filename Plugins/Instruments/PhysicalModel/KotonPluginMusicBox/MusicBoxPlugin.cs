using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginMusicBox
{
    /// <summary>
    /// Music Box (boîte à musique) — synthèse modale de peigne métallique. Attaque très piquée
    /// + décroissance moyenne (2-6 s) + partiels harmoniques dominants aigus. Timbre "féerique
    /// enfantin" caractéristique, parfait pour berceuses / génériques / ambiances magiques.
    /// </summary>
    [KotonInstrument("Music Box", Id = "koton.musicbox", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class MusicBoxPlugin : IKotonInstrument
    {
        public string Id => "koton.musicbox";
        public string DisplayName => "Music Box";

        readonly KotonParameter _strike     = new KotonParameter("strike",     "Strike",      0.0, 1.0, 0.75);
        readonly KotonParameter _sustain    = new KotonParameter("sustain",    "Sustain",     0.0, 1.0, 0.30);
        readonly KotonParameter _brightness = new KotonParameter("brightness", "Brillance",   0.0, 1.0, 0.75);
        readonly KotonParameter _bodyClick  = new KotonParameter("body_click", "Click bois",  0.0, 1.0, 0.35);
        readonly KotonParameter _volumeDb   = new KotonParameter("volume",     "Volume",      -30, 6, -3, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;
        int _sr;
        MbVoice[] _voices; int _stealCursor; const int Poly = 12;
        public MusicBoxPlugin() { _params = new List<KotonParameter> { _strike, _sustain, _brightness, _bodyClick, _volumeDb }; }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new KotonPluginMusicBox.Editor(this);
        public void Prepare(int sr, int max) { _sr = sr; _voices = new MbVoice[Poly]; for (int i = 0; i < Poly; i++) _voices[i] = new MbVoice(sr); }
        public void Reset() { if (_voices != null) foreach (var v in _voices) v.Kill(); }
        public void NoteOn(int note, int vel, int off = 0)
        {
            if (_voices == null || vel == 0) return;
            MbVoice t = null;
            for (int i = 0; i < Poly; i++) if (!_voices[i].IsActive) { t = _voices[i]; break; }
            if (t == null) { t = _voices[_stealCursor]; _stealCursor = (_stealCursor + 1) % Poly; t.Kill(); }
            t.NoteOn(note, vel / 127f, (float)_strike.Value, (float)_sustain.Value, (float)_brightness.Value, (float)_bodyClick.Value);
        }
        public void NoteOff(int note, int off = 0) { }   // music box = pas de damper actif
        public void MidiCC(int cc, int val, int off = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float v, int off = 0) { }
        public void Render(Span<float> l, Span<float> r)
        {
            if (_voices == null) { l.Clear(); r.Clear(); return; }
            float g = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            for (int i = 0; i < l.Length; i++)
            {
                float sum = 0f;
                foreach (var v in _voices) if (v.IsActive) sum += v.Render();
                float s = sum * g * 0.5f;
                if (s > 1) s = 1; else if (s < -1) s = -1;
                l[i] = s; r[i] = s;
            }
        }
        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var k in _params) d[k.Id] = k.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] s) { if (s == null || s.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(s)); if (d == null) return; foreach (var k in _params) if (d.TryGetValue(k.Id, out var v)) k.Value = v; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double v) { foreach (var k in _params) if (k.Id == id) { k.Value = v; return; } }
    }

    internal sealed class MbVoice
    {
        // Music box = lamelle métallique pincée par un picot du cylindre. C'est le modèle CANONIQUE
        // d'un Karplus-Strong : excitation impulsionnelle (le picot) injectée dans une delay line
        // (la lamelle qui vibre), rebouclée à travers un LP moyen (amortissement HF naturel de la
        // lamelle). Résultat : timbre riche à l'attaque, se dépouille rapidement en pure sinusoïde
        // (fondamentale qui meurt en dernier) → exactement la signature audio d'un peigne music box.
        //
        // Params importants :
        // - Excitation courte + HF (brillance métallique du "tock")
        // - LP moyen intrinsèque (classique KS) : c'est LUI qui donne la décroissance mécanique
        // - Tone LP variable (brightness) : gouverne le brillant total
        // - All-pass léger (stiffness) : petite inharmonicité pour éviter le son "synthé pur"
        // - Feedback modéré (~0.965) : music box = sustain court à moyen, PAS d'infini
        readonly int _sr;
        readonly float[] _buf;   // ligne à retard
        int _writeIdx;
        int _size;
        // Filtre LP classique KS
        float _lpPrev;
        // LP variable tone
        float _toneZ;
        // All-pass 1er ordre pour stiffness (petite dispersion)
        float _apX1, _apY1;
        int _note;
        float _fbGain;
        float _toneCoef;
        float _apCoef;
        // Enveloppe d'énergie pour libération auto
        float _peak = 1f;
        bool _active;
        public bool IsActive => _active;
        public int Note => _note;
        public MbVoice(int sr)
        {
            _sr = sr;
            // Buffer max = SR / freq_min. Music box va jusqu'à ~ C3 = 130 Hz → SR/130 ≈ 340 samples.
            // On garde une marge confortable (allocation à l'init, pas de GC au NoteOn).
            _buf = new float[Math.Max(sr / 40, 2048)];
        }
        public void NoteOn(int note, float vel, float strike, float sustain, float bright, float click)
        {
            _note = note;
            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _size = Math.Max(4, Math.Min(_buf.Length, (int)Math.Round(_sr / freq)));

            // Excitation = bruit blanc filtré par un LP TRÈS OUVERT (garde les HF pour le "tock")
            // puis multiplié par une enveloppe déclinante (early-decayed noise burst). Plus
            // "strike" est haut, plus l'excitation est brute et brillante.
            float attackLp = 0.85f - strike * 0.35f;   // 0.85 doux, 0.50 dur
            float lp = 0f;
            for (int i = 0; i < _size; i++)
            {
                float raw = (float)(_r.NextDouble() * 2 - 1);
                lp = attackLp * lp + (1f - attackLp) * raw;
                // Enveloppe : décroissance rapide dans la ligne pour simuler le pincement bref
                float env = (i < _size / 3) ? 1f : (1f - (i - _size / 3f) / (_size * 2f / 3f));
                if (env < 0) env = 0;
                _buf[i] = (raw * strike + lp * (1f - strike)) * env * vel * 0.9f;
            }
            // Petit body click très bref superposé au début (résonance caisse bois ~250 Hz)
            if (click > 0f)
            {
                int clickLen = Math.Min(_size, (int)(0.008 * _sr));   // 8ms
                for (int i = 0; i < clickLen; i++)
                    _buf[i] += (float)(_r.NextDouble() * 2 - 1) * click * vel * 0.3f * (1f - i / (float)clickLen);
            }

            // Feedback : music box a un sustain modéré. Base 0.965 → décroissance ~1s pour la
            // fondamentale (compensée par _size / 1000). Sustain augmente vers 0.99 max.
            float gBase = 0.955f + sustain * 0.035f;
            _fbGain = (float)Math.Pow(gBase, _size / 1000.0);

            // Tone LP : cutoff 500..8000 Hz selon brightness. Un music box est brillant.
            float toneHz = 500f + bright * 7500f;
            _toneCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * toneHz / _sr);

            // All-pass stiffness minimal (inharmonicité TRÈS légère → sonne "propre" mais pas synthé)
            _apCoef = 0.08f;

            _writeIdx = 0;
            _lpPrev = 0f;
            _toneZ = 0f;
            _apX1 = _apY1 = 0f;
            _peak = 1f;
            _active = true;
        }
        public void Kill() { _active = false; Array.Clear(_buf, 0, _buf.Length); _peak = 0f; }
        public float Render()
        {
            if (!_active) return 0f;
            float sample = _buf[_writeIdx];
            // 1) LP moyen KS classique (0.5x + 0.5x_prev) — le AMORTISSEMENT MÉTALLIQUE naturel
            float lp = 0.5f * (sample + _lpPrev);
            _lpPrev = sample;
            // 2) LP variable tone
            _toneZ += _toneCoef * (lp - _toneZ);
            float toned = _toneZ;
            // 3) All-pass stiffness (petite dispersion)
            float apOut = _apCoef * toned + _apX1 - _apCoef * _apY1;
            _apX1 = toned; _apY1 = apOut;
            // Feedback
            float outVal = apOut * _fbGain;
            _buf[_writeIdx] = outVal;
            _writeIdx++; if (_writeIdx >= _size) _writeIdx = 0;
            // Détection énergie pour libération auto
            float abs = outVal < 0 ? -outVal : outVal;
            _peak = Math.Max(_peak * 0.9998f, abs);
            if (_peak < 1e-5f) { _active = false; return 0f; }
            return sample;   // retourne le sample non-filtré = préserve l'attaque
        }
        static readonly Random _r = new Random();
    }

    internal sealed class Editor : UserControl, IKotonEditor
    {
        readonly MusicBoxPlugin _plugin;
        public Editor(MusicBoxPlugin p) { _plugin = p; MinWidth = 460; MinHeight = 220; Background = System.Windows.Media.Brushes.Transparent; Build(); }
        void Build()
        {
            var g = new Grid { Margin = new Thickness(14) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int n = _plugin.Parameters.Count, rows = (n + 1) / 2;
            for (int r = 0; r < rows; r++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < n; i++)
            {
                var kp = _plugin.Parameters[i];
                var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                var hg = new Grid();
                hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var lbl = new TextBlock { Text = kp.Name, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0)), FontSize = 11 };
                var val = new TextBlock { Text = Fmt(kp), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(lbl, 0); Grid.SetColumn(val, 1); hg.Children.Add(lbl); hg.Children.Add(val); sp.Children.Add(hg);
                var s = new Slider { Minimum = kp.Min, Maximum = kp.Max, Value = kp.Value };
                s.ValueChanged += (o, e) => { kp.Value = e.NewValue; val.Text = Fmt(kp); };
                sp.Children.Add(s);
                Grid.SetRow(sp, i / 2); Grid.SetColumn(sp, (i % 2) * 2); g.Children.Add(sp);
            }
            Content = g;
        }
        static string Fmt(KotonParameter kp) { string u = string.IsNullOrEmpty(kp.Unit) ? "" : " " + kp.Unit; return (kp.Max - kp.Min > 10) ? kp.Value.ToString("F0") + u : kp.Value.ToString("F2") + u; }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
