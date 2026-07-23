using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NAudio.Wave;
using MusicTracker.Engine;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Score;
using System.Collections.Generic;
using MeltySynth;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// "Suite d'accord" : a CONTEXT-AWARE functional diagram (harmonic co-pilot). The current chord sits at the centre;
    /// the ranked next chords fan out around it (ordered by <see cref="HarmonySuggest.Rank"/> using the last 2-3 chords,
    /// the bar position and a mood), the best ones flagged ★. Diatonic degrees (colour-aware, degree-locked) and pertinent
    /// chromatics (V/V, ♭VII, ♭VI, iv, ♭II). A mood combo re-ranks live; hovering a candidate PLAYS it. The static shell is
    /// in XAML; the fan-out diagram (canvas nodes/lines) is built dynamically here.
    /// </summary>
    public partial class ChordSuggestionDialog : Window
    {
        static readonly string[] MoodNames = { "Auto", "Joyeux", "Serein", "Mélancolique", "Nostalgique", "Épique", "Lumineux", "Jazzy" };
        static readonly string[] RomanU = { "I", "II", "III", "IV", "V", "VI", "VII" };
        static readonly string[] RomanL = { "i", "ii", "iii", "iv", "v", "vi", "vii" };
        static readonly string[] ColourNames = Engine.Flow.MusicTheory.DiatonicColourNames; // Triade/Sixte/7e/9e(7+9)/9e(add9)

        readonly int[] prevDegrees;
        readonly int barIndex, phraseLen;
        readonly KeySignature key;
        readonly int tonicPc, fullMode, currentDegree;
        readonly Preset previewInstrument;
        WaveOutEvent waveOut;
        bool ready;   // suppress the combos' SelectionChanged while they are being populated in the constructor

        public int ChosenDegree { get; private set; } = -1;
        public int ChosenColour { get; private set; }
        public int ChosenSuspension { get; private set; }
        public int ChosenMode { get; private set; }
        public bool ChosenIsDiatonic { get; private set; } = true;
        public int ChosenRoot { get; private set; }
        public int ChosenQuality { get; private set; }

        static Brush Res(string key) => (Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null) ?? Brushes.Gray;
        static Brush Hex(string h) => (Brush)new BrushConverter().ConvertFromString(h);
        static int Mod(int a) => ((a % 12) + 12) % 12;

        public ChordSuggestionDialog(int[] prevDegrees, int barIndex, int phraseLen, KeySignature key, Preset previewInstrument)
        {
            this.prevDegrees = prevDegrees ?? new[] { 0 };
            this.barIndex = barIndex; this.phraseLen = phraseLen < 1 ? 4 : phraseLen;
            this.key = key ?? new KeySignature();
            this.previewInstrument = previewInstrument;
            tonicPc = Engine.Flow.MusicTheory.TonicPc(this.key);
            fullMode = MusicalMode.Effective(this.key);
            currentDegree = (this.prevDegrees.Length > 0 && this.prevDegrees[this.prevDegrees.Length - 1] >= 0) ? Math.Max(0, Math.Min(6, this.prevDegrees[this.prevDegrees.Length - 1])) : 0;

            InitializeComponent();

            foreach (var m in MoodNames) cboMood.Items.Add(m);
            cboMood.SelectedIndex = 0;
            cboColour.Items.Add("Auto (suggéré)");
            foreach (var c in ColourNames) cboColour.Items.Add(c);
            cboColour.SelectedIndex = 0;
            foreach (var sname in Engine.Flow.MusicTheory.SuspensionNames) cboSusp.Items.Add(sname);
            cboSusp.SelectedIndex = 0;
            foreach (var mname in Engine.Flow.MusicTheory.ModeOverrideNames) cboMode.Items.Add(mname);
            cboMode.SelectedIndex = 0;

            Closed += (s, e) => StopPreview();
            ready = true;
        }

        void Combo_Changed(object sender, SelectionChangedEventArgs e) { if (ready) Rebuild(); }
        void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }

        void Rebuild()
        {
            canvas.Children.Clear();
            // (row, col) around the centre, CLOCKWISE from the TOP-CENTRE — the best candidate first, then clockwise.
            List<Tuple<int, int>> gridPos = new List<Tuple<int, int>>
            {
                new Tuple<int, int>(0,1),   // haut-centre (meilleur)
                new Tuple<int, int>(0,2),   // haut-droite
                new Tuple<int, int>(1,2),   // droite
                new Tuple<int, int>(2,2),   // bas-droite
                new Tuple<int, int>(2,1),   // bas-centre
                new Tuple<int, int>(2,0),   // bas-gauche
                new Tuple<int, int>(1,0),   // gauche
                new Tuple<int, int>(0,0),   // haut-gauche
            };
            var mood = (HarmonyMood)Math.Max(0, cboMood.SelectedIndex);
            var cands = HarmonySuggest.Rank(prevDegrees, barIndex, phraseLen, mood, key);
            if (cands.Count > 8) cands = cands.GetRange(0, 8);


            
          
            // The CENTRE = the current chord, now CLICKABLE too → repeat the same chord (prolongation).
            var self = new SuggestCand { Deg = currentDegree, SuggestColour = 0, Label = RomanLabel(currentDegree) };
            ChordOf(self, out int selfRoot, out int selfQual);
            var centre = Node(1, 1, RomanLabel(currentDegree), ChordName(self, selfRoot, selfQual), null, "#3A3A44", "#7A7A88", true, false);
            centre.Cursor = System.Windows.Input.Cursors.Hand;
            centre.ToolTip = "Répéter le même accord";
            centre.MouseEnter += (s, e) => Preview(self);
            centre.MouseLeave += (s, e) => StopPreview();
            centre.MouseLeftButtonUp += (s, e) =>
            {
                StopPreview();
                ChosenIsDiatonic = true; ChosenDegree = currentDegree;
                ChosenColour = cboColour.SelectedIndex <= 0 ? 0 : cboColour.SelectedIndex - 1;
                ChosenSuspension = cboSusp.SelectedIndex;
                ChosenMode = cboMode.SelectedIndex;
                DialogResult = true;
            };
            canvas.Children.Add(centre);

            for (int i = 0; i < cands.Count; i++)
            {
                double ang = cands.Count == 1 ? -Math.PI / 2 : (Math.PI + i * (Math.PI / (cands.Count - 1)));
                
                var c = cands[i];
                int ecol = cboColour.SelectedIndex <= 0 ? c.SuggestColour : cboColour.SelectedIndex - 1;
                string label = (c.Deg >= 0 ? c.Label + ColourSuffix(ecol, cboSusp.SelectedIndex) : c.Label);
                if (c.Recommended) label = "★ " + label;

                ChordOf(c, out int cRoot, out int cQual);
                var pos = gridPos[i];
                var box = Node(pos.Item2, pos.Item1, label, ChordName(c, cRoot, cQual), c.Effect, "#2A2A33", EffectColor(c.Effect), false, c.Recommended);
                var cc = c;
                box.Cursor = System.Windows.Input.Cursors.Hand;
                box.MouseEnter += (s, e) => Preview(cc);
                box.MouseLeave += (s, e) => StopPreview();
                box.MouseLeftButtonUp += (s, e) =>
                {
                    StopPreview();
                    if (cc.Deg >= 0)
                    {
                        ChosenIsDiatonic = true; ChosenDegree = cc.Deg;
                        ChosenColour = cboColour.SelectedIndex <= 0 ? cc.SuggestColour : cboColour.SelectedIndex - 1;
                        ChosenSuspension = cboSusp.SelectedIndex;
                        ChosenMode = cboMode.SelectedIndex;
                    }
                    else { ChosenIsDiatonic = false; ChosenRoot = Mod(tonicPc + cc.RootOff); ChosenQuality = cc.Quality; }
                    DialogResult = true;
                };
                canvas.Children.Add(box);
            }
        }

        // (root, quality) that a candidate would insert — for the audio preview and to reflect the colour combo.
        void ChordOf(SuggestCand c, out int rootPc, out int quality)
        {
            if (c.Deg >= 0)
            {
                int col = cboColour.SelectedIndex <= 0 ? c.SuggestColour : cboColour.SelectedIndex - 1;
                var ch = Engine.Flow.MusicTheory.DiatonicChord(key, c.Deg, col, cboSusp.SelectedIndex, cboMode.SelectedIndex);
                rootPc = ch.root; quality = ch.quality;
            }
            else { rootPc = Mod(tonicPc + c.RootOff); quality = c.Quality; }
        }

        void Preview(SuggestCand c)
        {
            StopPreview();
            if (previewInstrument == null) return;
            try
            {
                ChordOf(c, out int rootPc, out int quality);
                var pgm = new PatternGeneratorModule { Root = rootPc, Quality = quality, Octave = 4, Style = 0, BeatsPerBar = 2, Repeats = 1 };
                var riff = PatternGenerator.Generate(pgm);
                // Drum kits are identified by the SF2 BANK (128), not the patch number (see RhythmGridControl).
                var ctx = new FlowContext { GmProgram = previewInstrument?.PatchNumber ?? 0, Drum = previewInstrument?.BankNumber == InstrumentCatalog.DrumIndex, Bpm = 88 };
                var lp = new LoopingRiffProvider(() => riff, ctx);
                waveOut = new WaveOutEvent { DesiredLatency = 120 };
                waveOut.Init(lp); waveOut.Play();
            }
            catch { StopPreview(); }
        }

        void StopPreview()
        {
            if (waveOut != null) { try { waveOut.Stop(); waveOut.Dispose(); } catch { } waveOut = null; }
        }

        static Border Node(int col, int row, string roman, string name, string effect, string bg, string border, bool center, bool recommended)
        {
            double w = 150, h = center ? 90 : 80;
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = roman, Foreground = Brushes.White, FontSize = center ? 24 : (roman != null && roman.Length > 4 ? 14 : 19), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            if (!string.IsNullOrEmpty(name))
                sp.Children.Add(new TextBlock { Text = name, Foreground = Hex("#BBBBBB"), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) });
            if (!string.IsNullOrEmpty(effect))
                sp.Children.Add(new TextBlock { Text = effect, Foreground = Hex(border), FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0), TextAlignment = TextAlignment.Center });
            var b = new Border { Width = w, Height = h, Background = Hex(recommended ? "#33333F" : bg), BorderBrush = Hex(recommended ? "#FFFFFF" : border), BorderThickness = new Thickness(center ? 2 : (recommended ? 2.5 : 1.5)), CornerRadius = new CornerRadius(8), Child = sp };
           
            Grid.SetColumn(b, col); Grid.SetRow(b, row);
            return b;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {

            Rebuild();
        }

        static string ColourSuffix(int colour, int suspension)
        {
            string s;
            switch (colour) { case 1: s = "6"; break; case 2: s = "7"; break; case 3: s = "9"; break; case 4: s = "add9"; break; default: s = ""; break; }
            if (suspension == 1) s += "sus2"; else if (suspension == 2) s += "sus4";
            return s;
        }

        // Concrete chord NAME (note + compact quality symbol), e.g. "Sol 7", "Ré m", "Fa maj7" — the smaller label under the
        // degree. The note is spelled FUNCTIONALLY (by the chord's degree + chromatic alter), so a ♭VII reads "Si♭" not "La♯".
        string ChordName(SuggestCand c, int rootPc, int quality)
        {
            int degree, alter;
            if (c.Deg >= 0) { degree = c.Deg; alter = 0; }        // diatonic: its own scale degree
            else FunctionalDegree(c.RootOff, out degree, out alter); // chromatic: derive (degree, ♭/♯) from the offset
            return Engine.Score.KeySig.SpellPc(rootPc, key, degree, alter) + QualSym(quality);
        }

        // Functional (letter-degree 0..6, chromatic alter) of a chromatic root RootOff semitones above the tonic: the nearest
        // scale degree (natural), else the degree a semitone ABOVE it (♭), else a semitone below (♯). So ♭VII → (VII, ♭).
        void FunctionalDegree(int rootOff, out int degree, out int alter)
        {
            var scale = Engine.Score.MusicalMode.Scale(Engine.Score.MusicalMode.Effective(key));
            int off = ((rootOff % 12) + 12) % 12;
            for (int d = 0; d < 7 && d < scale.Length; d++) if ((((scale[d] % 12) + 12) % 12) == off) { degree = d; alter = 0; return; }
            for (int d = 0; d < 7 && d < scale.Length; d++) if (((((scale[d] - 1) % 12) + 12) % 12) == off) { degree = d; alter = -1; return; }
            for (int d = 0; d < 7 && d < scale.Length; d++) if (((((scale[d] + 1) % 12) + 12) % 12) == off) { degree = d; alter = +1; return; }
            degree = -1; alter = 0;   // no clean spelling → pc-based fallback in SpellPc
        }
        static string QualSym(int q)
        {
            switch (q)
            {
                case 1: return " m"; case 2: return " dim"; case 3: return " aug"; case 4: return " sus2"; case 5: return " sus4";
                case 6: return " maj7"; case 7: return " m7"; case 8: return " 7"; case 9: return " m7♭5"; case 10: return " dim7";
                case 11: return " 6"; case 12: return " m6"; case 13: return " add9"; case 14: return " m(add9)"; case 15: return " 9";
                case 16: return " maj9"; case 17: return " m9"; default: return ""; // 0 = major, and rarer qualities → just the note
            }
        }

        string RomanLabel(int degree)
        {
            var ch = Engine.Flow.MusicTheory.DiatonicChord(key, degree);
            int q = ch.quality;
            bool minorish = q == 1 || q == 7 || q == 14 || q == 17 || q == 2 || q == 9 || q == 10;
            bool dim = q == 2 || q == 9 || q == 10;
            string r = minorish ? RomanL[Math.Max(0, Math.Min(6, degree))] : RomanU[Math.Max(0, Math.Min(6, degree))];
            return dim ? r + "°" : r;
        }

        static string EffectColor(string effect)
        {
            switch (effect)
            {
                case "Résolution": case "Cadence plagale": return "#6CCB6C";
                case "Tension": case "Tension lumineuse": case "Tension dramatique": return "#E0A84A";
                case "Cadence rompue": case "Surprise sombre": return "#E06C6C";
                case "Pré-dominante": return "#6C9BE6";
                case "Couleur planante": case "Couleur lointaine": case "Mélancolie": return "#B48CE0";
                default: return "#AAAAAA";
            }
        }
    }
}
