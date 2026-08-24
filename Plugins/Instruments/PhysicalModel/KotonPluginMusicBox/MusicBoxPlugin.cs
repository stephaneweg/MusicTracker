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
    /// Music Box (boîte à musique) — synthèse MODALE d'une lame de peigne métallique.
    ///
    /// **Pourquoi pas Karplus-Strong** : une ligne à retard rebouclée produit une série strictement
    /// HARMONIQUE (f, 2f, 3f…), c'est-à-dire une CORDE. Une dent de peigne est une barre métallique
    /// rigide : ses modes de flexion sont très étirés et sans rapport entier entre eux, et c'est
    /// précisément cette inharmonicité qu'on entend comme « clochette ». Aucun réglage de KS ne peut
    /// la produire — d'où le retour au modèle modal, mais excité correctement (voir <see cref="MbVoice"/>).
    ///
    /// Signature visée : un « ting » métallique bref (modes hauts, quelques centaines de ms) posé sur
    /// une fondamentale qui chante plusieurs secondes, le tout coloré par la résonance du sommier.
    /// </summary>
    [KotonInstrument("Music Box", Id = "koton.musicbox", Category = "Physical Model", Version = "2.0", Vendor = "Koton Studio")]
    public sealed class MusicBoxPlugin : IKotonInstrument
    {
        public string Id => "koton.musicbox";
        public string DisplayName => "Music Box";

        readonly KotonParameter _strike     = new KotonParameter("strike",     "Attaque",       0.0, 1.0, 0.70);
        readonly KotonParameter _sustain    = new KotonParameter("sustain",    "Sustain",       0.0, 1.0, 0.38);
        readonly KotonParameter _brightness = new KotonParameter("brightness", "Brillance",     0.0, 1.0, 0.70);
        // Le curseur qui va de « corde pincée » (partiels harmoniques) à « clochette » (partiels de
        // barre rigide). C'est LE réglage de caractère de l'instrument, d'où un défaut déjà bien marqué.
        readonly KotonParameter _inharm     = new KotonParameter("inharm",     "Inharmonicité", 0.0, 1.0, 0.85);
        readonly KotonParameter _body       = new KotonParameter("body",       "Résonance",     0.0, 1.0, 0.28);
        readonly KotonParameter _bodyClick  = new KotonParameter("body_click", "Click méca",    0.0, 1.0, 0.30);
        readonly KotonParameter _volumeDb   = new KotonParameter("volume",     "Volume",        -30, 6, -3, "dB");
        // Ré-attaque périodique : rejoue la note tenue tous les 1/taux de seconde.
        // 0 Hz = une seule attaque, donc aucun projet existant ne change.
        readonly KotonStudio.Plugins.Shared.KotonReAttack _retrig =
            new KotonStudio.Plugins.Shared.KotonReAttack("Trémolo", 20.0, 0.0);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;
        int _sr;
        MbVoice[] _voices; int _stealCursor; const int Poly = 12;
        // Résonance du sommier + de la caisse, PARTAGÉE par toutes les voix (c'est une seule caisse) :
        // deux résonateurs larges qui prolongent et colorent l'ensemble. C'est ce qui manquait le plus —
        // sans eux chaque note vit dans le vide et l'instrument sonne synthétique.
        readonly MbMode _bodyLo = new MbMode(), _bodyHi = new MbMode();
        float _clickPending;   // click mécanique en attente d'injection dans la caisse (posé au NoteOn)

        public MusicBoxPlugin() { _params = new List<KotonParameter> { _strike, _sustain, _brightness, _inharm, _body, _bodyClick, _volumeDb, _retrig.Rate }; }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new KotonPluginMusicBox.Editor(this);
        public void Prepare(int sr, int max)
        {
            _sr = sr;
            _voices = new MbVoice[Poly];
            for (int i = 0; i < Poly; i++) _voices[i] = new MbVoice(sr);
            // Coffret ~330 Hz et base du peigne ~2,3 kHz, décroissances TRÈS courtes. Une boîte à
            // musique tient dans la main : sa caisse ne peut ni descendre bas ni résonner longtemps.
            // Descendre ces fréquences ou allonger ces queues fabrique immédiatement une grosse cloche.
            _bodyLo.Set(330f, sr, 0.13f, 1f, driven: true);
            _bodyHi.Set(2300f, sr, 0.07f, 0.6f, driven: true);
            _retrig.Prepare(sr);
        }
        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
            _bodyLo.Clear(); _bodyHi.Clear(); _clickPending = 0f;
            _retrig.Reset();
        }
        public void NoteOn(int note, int vel, int off = 0)
        {
            _retrig.NoteOn(note, vel);
            if (_voices == null || vel == 0) return;
            MbVoice t = null;
            // Rejouer la MÊME note reprend sa voix au lieu d'en allouer une neuve : sans ça les coups
            // répétés s'empilent (mesure : pic 0,33 → 0,68 à 9 coups/s). C'est aussi le comportement
            // physique — repincer une lame déjà en vibration l'arrête.
            for (int i = 0; i < Poly; i++) if (_voices[i].IsActive && _voices[i].Note == note) { t = _voices[i]; t.Kill(); break; }
            if (t == null) for (int i = 0; i < Poly; i++) if (!_voices[i].IsActive) { t = _voices[i]; break; }
            if (t == null) { t = _voices[_stealCursor]; _stealCursor = (_stealCursor + 1) % Poly; t.Kill(); }
            t.NoteOn(note, vel / 127f, (float)_strike.Value, (float)_sustain.Value, (float)_brightness.Value, (float)_inharm.Value);
            // Le picot qui relâche la dent fait un bruit mécanique SEC, qui appartient à la mécanique et
            // non à la lame : on l'envoie donc dans la caisse, pas dans les modes de la dent.
            _clickPending = (float)_bodyClick.Value * (vel / 127f);
        }
        public void NoteOff(int note, int off = 0) { }   // music box = pas d'étouffoir, la lame s'éteint seule
        public void MidiCC(int cc, int val, int off = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float v, int off = 0) { }
        public void Render(Span<float> l, Span<float> r)
        {
            if (_voices == null) { l.Clear(); r.Clear(); return; }
            float g = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            float bodyAmt = (float)_body.Value;
            for (int i = 0; i < l.Length; i++)
            {
                // Ré-attaque : à l'échéance, la note tenue est rejouée (BeginStroke neutralise
                // la notification que NoteOn va renvoyer à l'engin).
                if (_retrig.Tick()) { _retrig.BeginStroke(); for (int rt = 0; rt < _retrig.Count; rt++) NoteOn(_retrig.NoteAt(rt), _retrig.VelocityAt(rt)); _retrig.EndStroke(); }

                float sum = 0f;
                foreach (var v in _voices) if (v.IsActive) sum += v.Render();

                // Click mécanique : un très court train de bruit injecté dans la caisse au moment du NoteOn.
                float drive = sum;
                if (_clickPending > 0f)
                {
                    drive += (float)(_r.NextDouble() * 2 - 1) * _clickPending * 0.8f;
                    _clickPending *= 0.86f;                  // ~1 ms de bruit à 44,1 kHz
                    if (_clickPending < 1e-3f) _clickPending = 0f;
                }

                float body = _bodyLo.Process(drive) + _bodyHi.Process(drive);
                float s = (sum + body * bodyAmt * 0.40f) * g * 0.5f;
                if (s > 1) s = 1; else if (s < -1) s = -1;
                l[i] = s; r[i] = s;
            }
        }
        static readonly Random _r = new Random();
        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var k in _params) d[k.Id] = k.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] s) { if (s == null || s.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(s)); if (d == null) return; foreach (var k in _params) if (d.TryGetValue(k.Id, out var v)) k.Value = v; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double v) { foreach (var k in _params) if (k.Id == id) { k.Value = v; return; } }
    }

    /// <summary>
    /// Un mode de résonance : filtre récursif à 2 pôles (<c>y = c·y₁ − R²·y₂ + x</c>), qui sonne comme
    /// une sinusoïde à décroissance exponentielle quand on l'excite. Excité par du BRUIT plutôt que par
    /// une impulsion, il donne une attaque naturelle au lieu du « bip » d'une sinusoïde nue — c'est la
    /// différence entre une synthèse modale qui sonne instrument et une qui sonne chiptune.
    /// L'entrée est pondérée par sin(ω) pour que la réponse impulsionnelle culmine à ~1 quelle que
    /// soit la fréquence (sans quoi les modes graves écraseraient les aigus).
    /// </summary>
    internal sealed class MbMode
    {
        float _y1, _y2, _c, _r2, _inGain, _outGain;

        /// <param name="driven">
        /// Faux (défaut) = résonateur PINCÉ : il ne reçoit qu'un bref train de bruit puis vibre seul.
        /// L'entrée est pondérée par sin(ω), ce qui fait culminer la réponse impulsionnelle à ~1 quelle
        /// que soit la fréquence.
        ///
        /// Vrai = résonateur ENTRETENU : il est traversé en permanence par un signal (c'est le cas de la
        /// caisse, qui reçoit la somme des voix). ⚠️ La normalisation impulsionnelle est alors
        /// catastrophique : le gain en régime établi à la résonance vaut 1/(1−R), soit ~18 500 pour une
        /// décroissance de 0,4 s — mesuré, ça saturait toute la sortie du plugin. La normalisation
        /// correcte est (1−R)·2·sin(ω), qui ramène le gain à la résonance à l'unité.
        /// </param>
        public void Set(float freq, float sr, float tauSec, float gain, bool driven = false)
        {
            double w = 2.0 * Math.PI * freq / sr;
            // Au-delà de ~Nyquist le mode n'existe pas : on le rend muet plutôt que de le replier
            // dans l'audible (un partiel de barre peut monter à 18× la fondamentale).
            if (freq <= 0f || w >= Math.PI * 0.98) { _c = 0; _r2 = 0; _inGain = 0; _outGain = 0; _y1 = _y2 = 0; return; }
            double R = Math.Exp(-1.0 / Math.Max(1.0, tauSec * sr));
            _c = (float)(2.0 * R * Math.Cos(w));
            _r2 = (float)(R * R);
            _inGain = (float)(driven ? (1.0 - R) * 2.0 * Math.Sin(w) : Math.Sin(w));
            _outGain = gain;
            _y1 = _y2 = 0f;
        }

        public float Process(float x)
        {
            float y = _c * _y1 - _r2 * _y2 + _inGain * x;
            _y2 = _y1; _y1 = y;
            return _outGain * y;
        }

        public void Clear() { _y1 = _y2 = 0f; }
    }

    internal sealed class MbVoice
    {
        // Une dent de peigne est une barre métallique effilée, encastrée d'un côté et pincée par un
        // picot du cylindre. On la modélise par sa décomposition MODALE : quelques résonances de
        // flexion, chacune avec sa fréquence, son amplitude et sa propre durée de vie.
        //
        // Trois choses font la différence entre « clochette » et « bip de synthé » :
        //
        // 1. Les RAPPORTS de fréquence. Une corde donne 1, 2, 3, 4… Une barre libre donne
        //    1 : 2,76 : 5,40 : 8,93 : 13,3 : 18,6 (Fletcher & Rossing, *The Physics of Musical
        //    Instruments*) — les mêmes que le glockenspiel et le célesta, cousin orchestral direct de
        //    la boîte à musique. Ce sont ces rapports non entiers qu'on entend comme « métal ».
        //    Le paramètre Inharmonicité interpole entre les deux séries.
        //
        // 2. Les DÉCROISSANCES par mode. Les modes hauts rayonnent beaucoup plus d'énergie et meurent
        //    bien plus vite : le « ting » dure quelques centaines de ms, la fondamentale chante des
        //    secondes. Un modal à décroissance unique sonne électronique, précisément parce que son
        //    timbre ne change pas dans le temps.
        //
        // 3. L'EXCITATION. Chaque mode est attaqué par un court train de bruit, pas par une impulsion :
        //    l'onset de chaque partiel est ainsi bruité et légèrement désynchronisé, comme sur une vraie
        //    lame relâchée par un picot.
        //
        // S'ajoutent le battement de la fondamentale dédoublée (les dents vont souvent par paires et ne
        // sont jamais accordées à l'identique) — c'est le miroitement caractéristique — et une décroissance
        // qui raccourcit vers l'aigu, les dents aiguës étant plus courtes et plus rigides.
        const int Modes = 6;

        // Barre libre en flexion (métal rigide) vs corde (série harmonique).
        static readonly float[] BarRatio  = { 1f, 2.76f, 5.40f, 8.93f, 13.34f, 18.64f };
        static readonly float[] HarmRatio = { 1f, 2f,    3f,    4f,     5f,     6f    };
        // Répartition d'énergie d'un pincement près de l'encastrement : la fondamentale domine, les
        // partiels décroissent régulièrement mais restent bien présents (c'est le brillant du métal).
        static readonly float[] ModeAmp   = { 1f, 0.55f, 0.34f, 0.21f, 0.13f, 0.075f };

        readonly int _sr;
        readonly MbMode[] _modes = new MbMode[Modes];
        readonly MbMode _beat = new MbMode();   // jumelle légèrement désaccordée de la fondamentale
        int _note;
        int _exLeft;        // échantillons de bruit d'excitation restants
        float _exGain, _exTilt, _exLp;
        float _peak;
        bool _active;
        public bool IsActive => _active;
        public int Note => _note;

        public MbVoice(int sr)
        {
            _sr = sr;
            for (int i = 0; i < Modes; i++) _modes[i] = new MbMode();
        }

        public void NoteOn(int note, float vel, float strike, float sustain, float bright, float inharm)
        {
            _note = note;
            float freq = (float)(440.0 * Math.Pow(2.0, (note - 69) / 12.0));

            // Durée de vie de la fondamentale : 0,45 s à 2,75 s. Une dent de boîte à musique est une
            // toute petite lame : elle TINTE. Au-delà de ~3 s on bascule dans le registre perceptif de
            // la cloche d'église — c'est une question d'échelle de l'objet, pas de goût. Les dents
            // aiguës, plus courtes et plus rigides, s'éteignent en plus nettement plus vite.
            float tau0 = 0.45f + sustain * 2.3f;
            float reg = (float)Math.Pow(440.0 / Math.Max(60.0, freq), 0.45);
            if (reg < 0.45f) reg = 0.45f; else if (reg > 1.9f) reg = 1.9f;
            tau0 *= reg;

            // Brillance = niveau des partiels supérieurs. À 0 il ne reste presque que la fondamentale
            // (dent sourde), à 1 le métal est franc.
            float upper = 0.30f + bright * 1.25f;

            for (int i = 0; i < Modes; i++)
            {
                float ratio = HarmRatio[i] + (BarRatio[i] - HarmRatio[i]) * inharm;
                // Les modes hauts meurent beaucoup plus vite : c'est l'évolution « ting → chant » qui
                // fait qu'on reconnaît un objet métallique frappé.
                float tau = tau0 / (1f + 3.0f * i);
                float amp = ModeAmp[i] * (i == 0 ? 1f : upper);
                _modes[i].Set(freq * ratio, _sr, tau, amp * vel);
            }
            // Jumelle à +6 cents : battement d'environ 1,5 Hz à 440 Hz, un peu plus rapide dans l'aigu.
            // Discrète : deux fondamentales de force comparable qui battent, c'est le timbre d'une
            // cloche, pas d'un tintement. Ici elle ne fait que faire respirer la queue.
            _beat.Set(freq * 1.00347f, _sr, tau0 * 0.8f, 0.26f * vel);

            // Excitation : 1,2 ms (attaque dure) à 3,5 ms (attaque douce) de bruit. Court et brillant
            // pour une attaque dure, plus long et plus sombre pour une attaque molle.
            //
            // ⚠️ Normalisation par √longueur : un résonateur excité par un train de bruit de N
            // échantillons INTÈGRE ce bruit (marche aléatoire), et son amplitude finale croît donc en
            // √N. Sans ce facteur, allonger l'attaque rend la note plus forte au lieu de la rendre plus
            // douce, et l'ensemble part en écrêtage — mesuré : pic 1,000 et RMS 0,72, c'est-à-dire un
            // signal quasi carré où la fondamentale disparaît sous les produits de distorsion.
            _exLeft = Math.Max(8, (int)(_sr * (0.0035f - strike * 0.0023f)));
            _exGain = vel * (0.55f + strike * 0.45f) * 3.2f / (float)Math.Sqrt(_exLeft);
            _exTilt = 0.15f + strike * 0.55f;   // part de bruit brut (HF) vs bruit lissé
            _exLp = 0f;

            _peak = 1f;
            _active = true;
        }

        public void Kill()
        {
            _active = false;
            for (int i = 0; i < Modes; i++) _modes[i].Clear();
            _beat.Clear();
            _exLeft = 0; _peak = 0f;
        }

        public float Render()
        {
            if (!_active) return 0f;

            float x = 0f;
            if (_exLeft > 0)
            {
                float raw = (float)(_r.NextDouble() * 2 - 1);
                _exLp += 0.25f * (raw - _exLp);                       // composante sombre
                x = (raw * _exTilt + _exLp * (1f - _exTilt)) * _exGain;
                _exLeft--;
            }

            float sum = 0f;
            for (int i = 0; i < Modes; i++) sum += _modes[i].Process(x);
            sum += _beat.Process(x);
            sum *= 0.42f;                                             // les 7 résonateurs se somment

            float abs = sum < 0 ? -sum : sum;
            _peak = Math.Max(_peak * 0.99985f, abs);
            if (_peak < 1e-5f) { _active = false; return 0f; }
            return sum;
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
