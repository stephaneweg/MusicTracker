using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginEtherealChorus
{
    /// <summary>
    /// Ethereal Chorus — 6 voix chorus ultra-lentes (LFO 0.05-0.5 Hz), micro-pitch drift ±3-8 cents,
    /// panoramique large. Le son "chorale d'anges", parfait pour pads, voix, cordes → texture éthérée.
    /// </summary>
    [KotonEffect("Ethereal Chorus", Id = "koton.etherealchorus", Category = "Modulation", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class EtherealChorusPlugin : IKotonEffect
    {
        public string Id => "koton.etherealchorus";
        public string DisplayName => "Ethereal Chorus";

        readonly KotonParameter _rate    = new KotonParameter("rate",    "LFO rate",      0.02, 1.0, 0.15, "Hz");
        readonly KotonParameter _depth   = new KotonParameter("depth",   "Depth",         0.0, 1.0, 0.55);
        readonly KotonParameter _spread  = new KotonParameter("spread",  "Voice spread",  0.0, 1.0, 0.70);
        readonly KotonParameter _detune  = new KotonParameter("detune",  "Detune",        0.0, 1.0, 0.40);
        readonly KotonParameter _width   = new KotonParameter("width",   "Stereo width",  0.0, 1.0, 0.95);
        readonly KotonParameter _highCut = new KotonParameter("high_cut","High cut",      500, 20000, 8000, "Hz");
        readonly KotonParameter _mix     = new KotonParameter("mix",     "Mix",           0.0, 1.0, 0.55);
        readonly KotonParameter _outGain = new KotonParameter("out_gain","Output",        -30, 6, 0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;
        int _sr; EtherealCore _core;
        public EtherealChorusPlugin() { _params = new List<KotonParameter> { _rate, _depth, _spread, _detune, _width, _highCut, _mix, _outGain }; }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new EtherealEditor(this);
        public void Prepare(int sr, int max) { _sr = sr; _core = new EtherealCore(sr); }
        public void Reset() => _core?.Reset();
        public void Process(Span<float> l, Span<float> r)
        {
            if (_core == null) return;
            _core.Process(l, r, (float)_rate.Value, (float)_depth.Value, (float)_spread.Value,
                (float)_detune.Value, (float)_width.Value, (float)_highCut.Value,
                (float)_mix.Value, (float)_outGain.Value);
        }
        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var k in _params) d[k.Id] = k.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] s) { if (s == null || s.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(s)); if (d == null) return; foreach (var k in _params) if (d.TryGetValue(k.Id, out var v)) k.Value = v; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double v) { foreach (var k in _params) if (k.Id == id) { k.Value = v; return; } }
    }

    internal sealed class EtherealCore
    {
        const int NVoices = 6;
        readonly int _sr;
        readonly float[] _bufL, _bufR;
        int _writeL, _writeR;
        readonly double[] _lfoPhase = new double[NVoices];
        readonly float[] _pan = new float[NVoices];
        float _lpL, _lpR;

        public EtherealCore(int sr)
        {
            _sr = sr;
            int max = (int)(0.08 * sr);   // 80 ms max delay (chorus)
            _bufL = new float[max]; _bufR = new float[max];
            for (int i = 0; i < NVoices; i++) { _lfoPhase[i] = i * 2 * Math.PI / NVoices; _pan[i] = (i - (NVoices - 1) / 2f) / ((NVoices - 1) / 2f); }
        }
        public void Reset() { Array.Clear(_bufL, 0, _bufL.Length); Array.Clear(_bufR, 0, _bufR.Length); _lpL = _lpR = 0; }
        public void Process(Span<float> l, Span<float> r, float rateHz, float depth, float spread, float detune, float width, float hcHz, float mix, float outDb)
        {
            float outLin = (float)Math.Pow(10.0, outDb / 20.0);
            float dryG = 1f - mix, wetG = mix;
            float lpCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * hcHz / _sr);
            float baseDelay = 0.020f * _sr;   // 20 ms centre
            float depthS = depth * 0.010f * _sr;   // ±10 ms max
            int bufLen = _bufL.Length;
            for (int n = 0; n < l.Length; n++)
            {
                float inL = l[n], inR = r[n];
                _bufL[_writeL] = inL;
                _bufR[_writeR] = inR;
                float wetLeft = 0f, wetRight = 0f;
                for (int v = 0; v < NVoices; v++)
                {
                    // Chaque voix a un rate LFO légèrement décalé (spread) et sa propre phase.
                    double phaseInc = 2.0 * Math.PI * rateHz * (0.6f + 0.4f * (v / (float)NVoices) * (1f + spread * 2f)) / _sr;
                    _lfoPhase[v] += phaseInc; if (_lfoPhase[v] > 2 * Math.PI) _lfoPhase[v] -= 2 * Math.PI;
                    float mod = (float)Math.Sin(_lfoPhase[v]);
                    // Detune subtil : chaque voix a un offset de délai fixe légèrement différent (crée pseudo-pitch drift)
                    float detuneOff = (v - (NVoices - 1) / 2f) * detune * 12f;
                    float len = baseDelay + mod * depthS + detuneOff;
                    if (len < 1) len = 1; if (len > bufLen - 2) len = bufLen - 2;
                    int li = (int)len; float f = len - li;
                    int rL0 = _writeL - li; if (rL0 < 0) rL0 += bufLen;
                    int rL1 = rL0 - 1; if (rL1 < 0) rL1 += bufLen;
                    float sL = _bufL[rL0] * (1f - f) + _bufL[rL1] * f;
                    int rR0 = _writeR - li; if (rR0 < 0) rR0 += bufLen;
                    int rR1 = rR0 - 1; if (rR1 < 0) rR1 += bufLen;
                    float sR = _bufR[rR0] * (1f - f) + _bufR[rR1] * f;
                    float voice = 0.5f * (sL + sR);
                    // Panoramique de la voix
                    float p = _pan[v] * width;
                    float gL = 0.5f * (1f - p); float gR = 0.5f * (1f + p);
                    wetLeft += voice * gL;
                    wetRight += voice * gR;
                }
                wetLeft *= 2f / NVoices;
                wetRight *= 2f / NVoices;
                _lpL += lpCoef * (wetLeft - _lpL);
                _lpR += lpCoef * (wetRight - _lpR);
                l[n] = (dryG * inL + wetG * _lpL) * outLin;
                r[n] = (dryG * inR + wetG * _lpR) * outLin;
                _writeL++; if (_writeL >= bufLen) _writeL = 0;
                _writeR++; if (_writeR >= bufLen) _writeR = 0;
            }
        }
    }

    internal sealed class EtherealEditor : UserControl, IKotonEditor
    {
        readonly EtherealChorusPlugin _plugin;
        public EtherealEditor(EtherealChorusPlugin p) { _plugin = p; MinWidth = 500; MinHeight = 300; Background = System.Windows.Media.Brushes.Transparent; Build(); }
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
