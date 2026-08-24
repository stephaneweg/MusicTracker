using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginDuduk
{
    /// <summary>
    /// Duduk arménien — hautbois à anche double en bois d'abricotier, joué en Arménie depuis un millénaire
    /// et inscrit au patrimoine immatériel de l'UNESCO. Synthèse à formants (voir <see cref="DudukVoice"/>).
    ///
    /// **Ce qui le distingue des autres anches doubles** : sa perce est cylindrique — donc harmoniques
    /// impaires et grave creux, là où le hautbois est conique et brillant — et son anche est démesurément
    /// large et molle, ce qui étouffe les aigus. D'où ce timbre sombre, sans éclat, dont l'ambitus tient
    /// dans une octave et demie et que tout le monde décrit comme proche de la voix humaine.
    ///
    /// **Jeu de réglages volontairement restreint** pour cette première version : de quoi façonner le
    /// timbre et le vibrato, rien de plus. On enrichira au fur et à mesure de ce que l'oreille réclame
    /// plutôt que d'exposer d'emblée une vingtaine de curseurs dont on ne saura pas quoi faire.
    /// </summary>
    [KotonInstrument("Duduk", Id = "koton.duduk", Category = "Physical Model", Version = "2.0", Vendor = "Koton Studio")]
    public sealed class DudukPlugin : IKotonInstrument
    {
        public string Id => "koton.duduk";
        public string DisplayName => "Duduk";

        readonly KotonParameter _brightness = new KotonParameter("brightness",    "Brillance",         0.0, 1.0, 0.30);
        // Jusqu'où monte la série d'harmoniques. C'est le réglage qui décide si l'on entend un duduk
        // (série courte, anche molle) ou quelque chose qui tire vers le hautbois.
        readonly KotonParameter _harmonics  = new KotonParameter("harmonics",     "Harmoniques",       2.0, 24.0, 13.0);
        // Dominance des rangs impairs — la marque d'une perce cylindrique fermée côté anche. À 1 le
        // spectre devient franchement clarinette ; le duduk vit plutôt vers la moitié de la course.
        readonly KotonParameter _odd        = new KotonParameter("odd",           "Impaires",          0.0, 1.0, 0.60);
        readonly KotonParameter _voice      = new KotonParameter("voice",         "Voyelle",           0.0, 1.0, 0.55);
        readonly KotonParameter _breath     = new KotonParameter("breath",        "Souffle",           0.0, 1.0, 0.22);
        // Au-dessus de zéro, l'instrument devient MONODIQUE À LIAISON : une note qui arrive pendant
        // qu'une autre sonne reprend sa voix et y glisse, au lieu de s'empiler. C'est le jeu réel du
        // duduk, et c'est la seule façon d'avoir un portamento qui veuille dire quelque chose.
        readonly KotonParameter _portamento = new KotonParameter("portamento",    "Portamento",        0.0, 400.0, 70.0, "ms");
        readonly KotonParameter _vibRate    = new KotonParameter("vibrato_rate",  "Vibrato",           0.0, 9.0, 5.0, "Hz");
        readonly KotonParameter _vibDepth   = new KotonParameter("vibrato_depth", "Ampleur vibrato",   0.0, 80.0, 38.0, "ct");
        // Le trait de jeu qui fait le duduk : le vibrato n'est PAS là à l'attaque, il s'installe sur la
        // note tenue. À 0 il est présent tout de suite, ce qui s'entend aussitôt comme une machine.
        readonly KotonParameter _vibRise    = new KotonParameter("vibrato_rise",  "Montée du vibrato", 0.0, 2.5, 0.75, "s");
        readonly KotonParameter _attack     = new KotonParameter("attack",        "Attaque",           5.0, 400.0, 60.0, "ms");
        readonly KotonParameter _release    = new KotonParameter("release",       "Chute",             20.0, 800.0, 180.0, "ms");
        readonly KotonParameter _volumeDb   = new KotonParameter("volume",        "Volume",            -30.0, 6.0, -6.0, "dB");

        // Coup de langue partagé (0 = tenu). La lane Staccato de la timeline pilote ce paramètre-là quand
        // la piste en a une, plutôt que de rejouer les notes depuis l'extérieur.
        readonly KotonStudio.Plugins.Shared.KotonReAttack _tongue =
            new KotonStudio.Plugins.Shared.KotonReAttack("Coup de langue", 16.0, 0.0)
            { ArticulationSec = 0.022f };

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        DudukVoice[] _voices;
        int _stealCursor;
        const int Poly = 4;   // le duduk est monodique ; la marge sert aux enchaînements liés

        public DudukPlugin()
        {
            _params = new List<KotonParameter>
            {
                _brightness, _harmonics, _odd, _voice, _breath, _portamento, _vibRate, _vibDepth, _vibRise,
                _attack, _release, _tongue.Rate, _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new DudukEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _voices = new DudukVoice[Poly];
            for (int i = 0; i < Poly; i++)
            {
                _voices[i] = new DudukVoice(sampleRate);
                _voices[i].SetupFormants();
            }
            _tongue.Prepare(sampleRate);
        }

        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
            _tongue.Reset();
        }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            _tongue.NoteOn(note, velocity);
            StartVoice(note, velocity);
        }

        /// <summary>Alloue et attaque une voix. Séparé du <see cref="NoteOn"/> public parce que le coup de
        /// langue rappelle ce chemin : y repasser remettrait sa phase à zéro à chaque coup.</summary>
        void StartVoice(int note, int velocity)
        {
            var p = ToVoiceParams();
            float vel = velocity / 127f;
            DudukVoice target = null;
            for (int i = 0; i < Poly; i++) if (_voices[i].IsActive && _voices[i].Note == note) { target = _voices[i]; break; }
            // Portamento actif : on reprend la voix qui sonne déjà plutôt que d'en allouer une neuve,
            // sinon les deux notes se superposeraient et il n'y aurait rien à faire glisser. Le duduk
            // étant monodique, c'est aussi le comportement juste. À portamento nul on garde l'allocation
            // polyphonique normale, pour qui voudrait s'en servir comme d'une nappe.
            if (target == null && p.PortamentoSec > 0.001f)
                for (int i = 0; i < Poly; i++) if (_voices[i].IsActive) { target = _voices[i]; break; }
            if (target == null)
                for (int i = 0; i < Poly; i++) if (!_voices[i].IsActive) { target = _voices[i]; break; }
            if (target == null) { target = _voices[_stealCursor]; _stealCursor = (_stealCursor + 1) % Poly; }
            target.NoteOn(note, vel, p);
        }

        public void NoteOff(int note, int sampleOffset = 0)
        {
            if (_voices == null) return;
            _tongue.NoteOff(note);
            for (int i = 0; i < Poly; i++) if (_voices[i].IsActive && _voices[i].Note == note) _voices[i].NoteOff();
        }

        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }
            var p = ToVoiceParams();
            float vol = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                if (_tongue.Tick())
                {
                    _tongue.BeginStroke();
                    for (int t = 0; t < _tongue.Count; t++) StartVoice(_tongue.NoteAt(t), _tongue.VelocityAt(t));
                    _tongue.EndStroke();
                }

                float sum = 0f;
                for (int v = 0; v < Poly; v++) if (_voices[v].IsActive) sum += _voices[v].RenderSample(p);

                float s = sum * vol * _tongue.Gain;
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                left[i] = s; right[i] = s;
            }
        }

        DkParams ToVoiceParams() => new DkParams
        {
            Brightness        = (float)_brightness.Value,
            Harmonics         = (float)_harmonics.Value,
            OddBias           = (float)_odd.Value,
            Voice             = (float)_voice.Value,
            Breath            = (float)_breath.Value,
            PortamentoSec     = (float)(_portamento.Value / 1000.0),
            VibratoRateHz     = (float)_vibRate.Value,
            VibratoDepthCents = (float)_vibDepth.Value,
            VibratoRiseSec    = (float)_vibRise.Value,
            AttackSec         = (float)(_attack.Value / 1000.0),
            ReleaseSec        = (float)(_release.Value / 1000.0),
        };

        public byte[] SaveState()
        {
            try
            {
                var d = new Dictionary<string, double>();
                foreach (var k in _params) d[k.Id] = k.Value;
                return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d));
            }
            catch { return Array.Empty<byte>(); }
        }

        public void LoadState(byte[] s)
        {
            if (s == null || s.Length == 0) return;
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(s));
                if (d == null) return;
                foreach (var k in _params) if (d.TryGetValue(k.Id, out var v)) k.Value = v;
            }
            catch { }
        }

        public void Dispose() { }
        public void SetParam(string id, double v) { foreach (var k in _params) if (k.Id == id) { k.Value = v; return; } }
    }

    internal sealed class DudukEditor : UserControl, IKotonEditor
    {
        readonly DudukPlugin _plugin;

        public DudukEditor(DudukPlugin p)
        {
            _plugin = p;
            MinWidth = 460; MinHeight = 210;
            Background = System.Windows.Media.Brushes.Transparent;
            Build();
        }

        void Build()
        {
            var g = new Grid { Margin = new Thickness(14) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int n = _plugin.Parameters.Count, rows = (n + 1) / 2;
            for (int r = 0; r < rows; r++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < n; i++)
            {
                var kp = _plugin.Parameters[i];
                var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
                var hg = new Grid();
                hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var lbl = new TextBlock
                {
                    Text = kp.Name, FontSize = 11,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0)),
                };
                var val = new TextBlock
                {
                    Text = Fmt(kp), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x8A, 0x8A)),
                };
                Grid.SetColumn(lbl, 0); Grid.SetColumn(val, 1);
                hg.Children.Add(lbl); hg.Children.Add(val);
                sp.Children.Add(hg);

                var s = new Slider { Minimum = kp.Min, Maximum = kp.Max, Value = kp.Value };
                s.ValueChanged += (o, e) => { kp.Value = e.NewValue; val.Text = Fmt(kp); };
                kp.Changed += _ => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (Math.Abs(s.Value - kp.Value) > 1e-9) s.Value = kp.Value;
                    val.Text = Fmt(kp);
                }));
                sp.Children.Add(s);

                Grid.SetRow(sp, i / 2); Grid.SetColumn(sp, (i % 2) * 2);
                g.Children.Add(sp);
            }
            Content = g;
        }

        static string Fmt(KotonParameter kp)
        {
            string u = string.IsNullOrEmpty(kp.Unit) ? "" : " " + kp.Unit;
            return (kp.Max - kp.Min > 10) ? kp.Value.ToString("F0") + u : kp.Value.ToString("F2") + u;
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
