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
        readonly KotonParameter _sustain    = new KotonParameter("sustain",    "Sustain",     0.0, 1.0, 0.55);
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
        // Music box = peigne métallique. Ratios harmoniques mais avec inharmonicité légère aux aigus.
        // Ratios de mesure : peigne d'un music box "Reuge" 30 notes (Fletcher & Rossing 1998).
        static readonly float[] Ratios = { 1.000f, 2.000f, 3.010f, 4.030f, 5.070f, 6.130f };
        const int NModes = 6;
        readonly int _sr;
        readonly double[] _phase = new double[NModes];
        readonly double[] _inc = new double[NModes];
        readonly float[] _amp = new float[NModes];
        readonly float[] _decay = new float[NModes];
        // Bruit d'attaque + click du bois (BP autour de 400 Hz)
        float _clickAmp; float _clickDecay;
        int _note;
        bool _active;
        public bool IsActive => _active;
        public int Note => _note;
        public MbVoice(int sr) { _sr = sr; }
        public void NoteOn(int note, float vel, float strike, float sustain, float bright, float click)
        {
            _note = note;
            double f0 = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            for (int i = 0; i < NModes; i++)
            {
                _phase[i] = 0;
                double f = f0 * Ratios[i];
                if (f > _sr * 0.45) f = _sr * 0.45;
                _inc[i] = 2.0 * Math.PI * f / _sr;
                // Amplitude : la fondamentale et l'octave dominent, les partiels aigus dopés par brightness
                float rank = 1f / (1f + i * 0.5f);
                float brBoost = 1f + bright * (i / (float)NModes) * 2f;
                _amp[i] = vel * rank * brBoost * 0.35f * (1f + strike * 0.5f);
                float decaySec = 0.8f + sustain * 5f - i * 0.15f;
                if (decaySec < 0.15f) decaySec = 0.15f;
                _decay[i] = (float)Math.Exp(-1.0 / (decaySec * _sr));
            }
            _clickAmp = click * vel * 0.4f;
            _clickDecay = (float)Math.Exp(-1.0 / (0.008 * _sr));   // 8ms click
            _active = true;
        }
        public void Kill() { _active = false; for (int i = 0; i < NModes; i++) _amp[i] = 0; _clickAmp = 0; }
        public float Render()
        {
            if (!_active) return 0f;
            float s = 0f;
            for (int i = 0; i < NModes; i++)
            {
                _phase[i] += _inc[i]; if (_phase[i] > 2 * Math.PI) _phase[i] -= 2 * Math.PI;
                s += (float)Math.Sin(_phase[i]) * _amp[i];
                _amp[i] *= _decay[i];
            }
            // Click du bois : bruit blanc filtré modulé par l'enveloppe
            if (_clickAmp > 0.0001f)
            {
                s += (float)(_r.NextDouble() * 2 - 1) * _clickAmp;
                _clickAmp *= _clickDecay;
            }
            float peak = 0f; for (int i = 0; i < NModes; i++) if (_amp[i] > peak) peak = _amp[i];
            if (peak < 1e-6f && _clickAmp < 1e-6f) _active = false;
            return s;
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
