using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginSingingBowl
{
    /// <summary>
    /// Singing Bowl (bol chantant tibétain) — synthèse modale de 8 partiels inharmoniques avec
    /// décroissance longue (10-30 s) et battements. Timbre méditation / temple / cérémonie.
    ///
    /// Ratios de partiels approximés de bols tibétains réels (mesures Rossing 2010) :
    /// 1.000 (fond), 2.756, 5.404, 8.933, 13.35, 18.65, 24.81, 31.87. Non-harmoniques, ce qui
    /// donne le son "shimmering" caractéristique. Chaque partiel a sa propre décroissance et un
    /// léger detune pour créer les battements (~2-4 Hz) audibles.
    /// </summary>
    [KotonInstrument("Singing Bowl", Id = "koton.singingbowl", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class SingingBowlPlugin : IKotonInstrument
    {
        public string Id => "koton.singingbowl";
        public string DisplayName => "Singing Bowl";

        readonly KotonParameter _strikeAmt   = new KotonParameter("strike",     "Strike (mallet)", 0.0, 1.0, 0.60);
        readonly KotonParameter _sustainLen  = new KotonParameter("sustain",    "Sustain",         0.0, 1.0, 0.85);
        readonly KotonParameter _shimmer     = new KotonParameter("shimmer",    "Shimmer (haut)",  0.0, 1.0, 0.50);
        readonly KotonParameter _beat        = new KotonParameter("beat",       "Battements",      0.0, 1.0, 0.35);
        readonly KotonParameter _brightness  = new KotonParameter("brightness", "Brillance",       0.0, 1.0, 0.55);
        readonly KotonParameter _volumeDb    = new KotonParameter("volume",     "Volume",          -30, 6, -6, "dB");
        // Ré-attaque périodique : rejoue la note tenue tous les 1/taux de seconde.
        // 0 Hz = une seule attaque, donc aucun projet existant ne change.
        readonly KotonStudio.Plugins.Shared.KotonReAttack _retrig =
            new KotonStudio.Plugins.Shared.KotonReAttack("Trémolo", 20.0, 0.0);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;
        int _sr;
        BowlVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 6;

        public SingingBowlPlugin()
        {
            _params = new List<KotonParameter> { _strikeAmt, _sustainLen, _shimmer, _beat, _brightness, _volumeDb, _retrig.Rate };
        }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new SimpleEditor(this);
        public void Prepare(int sr, int max)
        {
            _sr = sr; _voices = new BowlVoice[Polyphony]; for (int i = 0; i < Polyphony; i++) _voices[i] = new BowlVoice(sr);
            _retrig.Prepare(sr);
        }
        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
            _retrig.Reset();
        }
        public void NoteOn(int note, int vel, int off = 0)
        {
            _retrig.NoteOn(note, vel);
            if (_voices == null || vel == 0) return;
            BowlVoice t = null;
            // Rejouer la MÊME note reprend sa voix au lieu d'en allouer une neuve : sans ça les coups
            // répétés s'empilent (mesure : pic 0,33 → 0,68 à 9 coups/s). C'est aussi le comportement
            // physique — repincer une corde déjà en vibration l'arrête.
            for (int i = 0; i < Polyphony; i++) if (_voices[i].IsActive && _voices[i].Note == note) { t = _voices[i]; t.Kill(); break; }
            if (t == null) for (int i = 0; i < Polyphony; i++) if (!_voices[i].IsActive) { t = _voices[i]; break; }
            if (t == null) { t = _voices[_stealCursor]; _stealCursor = (_stealCursor + 1) % Polyphony; t.Kill(); }
            t.NoteOn(note, vel / 127f, (float)_strikeAmt.Value, (float)_sustainLen.Value, (float)_shimmer.Value, (float)_beat.Value, (float)_brightness.Value);
        }
        public void NoteOff(int note, int off = 0) { if (_voices == null) return; foreach (var v in _voices) if (v.IsActive && v.Note == note) v.NoteOff(); }
        public void MidiCC(int cc, int val, int off = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float v, int off = 0) { }
        public void Render(Span<float> l, Span<float> r)
        {
            if (_voices == null) { l.Clear(); r.Clear(); return; }
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            int n = l.Length;
            for (int i = 0; i < n; i++)
            {
                // Ré-attaque : à l'échéance, la note tenue est rejouée (BeginStroke neutralise
                // la notification que NoteOn va renvoyer à l'engin).
                if (_retrig.Tick()) { _retrig.BeginStroke(); for (int rt = 0; rt < _retrig.Count; rt++) NoteOn(_retrig.NoteAt(rt), _retrig.VelocityAt(rt)); _retrig.EndStroke(); }

                float sum = 0f;
                foreach (var v in _voices) if (v.IsActive) sum += v.Render();
                float s = sum * volLin * 0.5f;
                if (s > 1) s = 1; else if (s < -1) s = -1;
                l[i] = s; r[i] = s;
            }
        }
        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var k in _params) d[k.Id] = k.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] s) { if (s == null || s.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(s)); if (d == null) return; foreach (var k in _params) if (d.TryGetValue(k.Id, out var v)) k.Value = v; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double v) { foreach (var k in _params) if (k.Id == id) { k.Value = v; return; } }
    }

    internal sealed class BowlVoice
    {
        // Ratios modaux d'un bol tibétain réel (Rossing 2010, mesures de bols himalayens)
        static readonly float[] Ratios = { 1.000f, 2.756f, 5.404f, 8.933f, 13.35f, 18.65f, 24.81f, 31.87f };
        const int NModes = 8;
        readonly int _sr;
        readonly double[] _phase = new double[NModes];
        readonly double[] _inc = new double[NModes];
        readonly float[] _amp = new float[NModes];
        readonly float[] _decay = new float[NModes];   // multiplicateur par sample (< 1)
        readonly double[] _beatPhase = new double[NModes];   // pour légère modulation d'amplitude (battements)
        readonly float[] _beatRateHz = new float[NModes];
        int _note;
        bool _active;
        float _globalRelease = 1f;
        public bool IsActive => _active;
        public int Note => _note;
        public BowlVoice(int sr) { _sr = sr; }
        public void NoteOn(int note, float vel, float strike, float sustain, float shimmer, float beat, float bright)
        {
            _note = note;
            double f0 = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            for (int i = 0; i < NModes; i++)
            {
                _phase[i] = 0;
                double f = f0 * Ratios[i];
                if (f > _sr * 0.45) f = _sr * 0.45;   // anti-alias
                _inc[i] = 2.0 * Math.PI * f / _sr;
                // Amplitude selon rang du partiel × brillance × shimmer (haut partiel dopé par shimmer)
                float rankAtt = 1f / (1f + i * 0.6f);   // f, 0.62, 0.45, 0.35, 0.28, ...
                float brightBoost = 1f + bright * (i / (float)NModes);
                float shimmerBoost = (i >= 3) ? 1f + shimmer * 1.2f : 1f;
                _amp[i] = vel * rankAtt * brightBoost * shimmerBoost * 0.4f;
                // Décroissance : + long pour les fondamentaux, + court pour les aigus
                float decaySec = 3f + sustain * 25f - i * 0.8f;
                if (decaySec < 0.3f) decaySec = 0.3f;
                _decay[i] = (float)Math.Exp(-1.0 / (decaySec * _sr));
                // Attaque = petit boost initial simulant le mallet (les partiels aigus attaquent + fort)
                float atkBoost = 1f + strike * (0.5f + i * 0.15f);
                _amp[i] *= atkBoost;
                // Battements : chaque partiel bat légèrement à un rythme différent (2-4 Hz)
                _beatPhase[i] = i * 0.3;
                _beatRateHz[i] = 1.5f + i * 0.4f + beat * 3f;
            }
            _globalRelease = 1f;
            _active = true;
        }
        public void NoteOff() { /* release rapide */ _globalRelease = 0.9995f; }   // 20s tail
        public void Kill() { _active = false; for (int i = 0; i < NModes; i++) _amp[i] = 0; }
        public float Render()
        {
            if (!_active) return 0f;
            float s = 0f;
            float peak = 0f;
            for (int i = 0; i < NModes; i++)
            {
                _phase[i] += _inc[i]; if (_phase[i] > 2 * Math.PI) _phase[i] -= 2 * Math.PI;
                _beatPhase[i] += 2.0 * Math.PI * _beatRateHz[i] / _sr;
                float beatMod = 1f + 0.15f * (float)Math.Sin(_beatPhase[i]);
                s += (float)Math.Sin(_phase[i]) * _amp[i] * beatMod;
                _amp[i] *= _decay[i];
                if (_amp[i] > peak) peak = _amp[i];
            }
            if (_globalRelease < 1f)
                for (int i = 0; i < NModes; i++) _amp[i] *= _globalRelease;
            if (peak < 1e-6f) _active = false;
            return s;
        }
    }

    // Éditeur générique (défini dans SimpleEditor.cs pour ne pas répéter le code)
    internal sealed class SimpleEditor : UserControl, IKotonEditor
    {
        readonly SingingBowlPlugin _plugin;
        public SimpleEditor(SingingBowlPlugin p) { _plugin = p; MinWidth = 480; MinHeight = 260; Background = System.Windows.Media.Brushes.Transparent; Build(); }
        void Build()
        {
            var g = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(14) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int n = _plugin.Parameters.Count, rows = (n + 1) / 2;
            for (int r = 0; r < rows; r++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < n; i++)
            {
                var kp = _plugin.Parameters[i];
                var sp = new StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 10) };
                var hg = new Grid();
                hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var lbl = new TextBlock { Text = kp.Name, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0)), FontSize = 11 };
                var val = new TextBlock { Text = FormatVal(kp), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(lbl, 0); Grid.SetColumn(val, 1); hg.Children.Add(lbl); hg.Children.Add(val); sp.Children.Add(hg);
                var s = new Slider { Minimum = kp.Min, Maximum = kp.Max, Value = kp.Value };
                s.ValueChanged += (o, e) => { kp.Value = e.NewValue; val.Text = FormatVal(kp); };
                sp.Children.Add(s);
                Grid.SetRow(sp, i / 2); Grid.SetColumn(sp, (i % 2) * 2);
                g.Children.Add(sp);
            }
            Content = g;
        }
        static string FormatVal(KotonParameter kp)
        {
            string unit = string.IsNullOrEmpty(kp.Unit) ? "" : " " + kp.Unit;
            return (kp.Max - kp.Min > 10) ? kp.Value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + unit
                                          : kp.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + unit;
        }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
