using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MusicTracker.Engine.Score;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// "Créer structure" — a dark-themed dialog that drives the Orchestrateur: a Modèle + Forme + Style + Tonalité + Mode +
    /// Humeur + Développement (all from the selected composer's own options) and the FORM skeleton (tonic, tempo,
    /// intro/theme/outro bar counts + variation count), plus a "Générer la musique" toggle (off = only the harmonic bed).
    /// Produces an editable arrangement (chord trame + sections + theme). Layout in XAML, logic here (was code-only WPF).
    /// </summary>
    public partial class CreateStructureDialog : Window
    {
        // tonic note → (diatonic letter 0=C..6=B, accidental). pc = {0,2,4,5,7,9,11}[letter] + accidental.
        static readonly string[] ToneLabels = { "Do", "Do♯", "Ré", "Mi♭", "Mi", "Fa", "Fa♯", "Sol", "La♭", "La", "Si♭", "Si" };
        static readonly int[] ToneLetter = { 0, 0, 1, 2, 2, 3, 3, 4, 5, 5, 6, 6 };
        static readonly int[] ToneAccid = { 0, 1, 0, -1, 0, 0, 1, 0, -1, 0, -1, 0 };
        static readonly int[] LetterPc = { 0, 2, 4, 5, 7, 9, 11 };

        List<MusicComposer> composers = new List<MusicComposer>();   // styleBox index → composer

        // ---- results (valid after DialogResult == true) ----
        public MusicComposer ChosenComposer { get; private set; }   // the posed-form hybrid to run
        public KeySignature ChosenKey { get; private set; }
        public Dictionary<string, int> Options { get; private set; }
        public int IntroBars { get; private set; }
        public int ThemeBars { get; private set; }
        public int ThemeReps { get; private set; }
        public int OutroBars { get; private set; }
        public double Bpm { get; private set; }
        public bool GenerateMusic { get; private set; }
        public int MeterNum { get; private set; } = 4;
        public int MeterDen { get; private set; } = 4;
        public bool IncludePad { get; private set; } = true;
        public bool IncludeBass { get; private set; } = true;
        public bool IncludeCounter { get; private set; } = true;
        public bool IncludeIntroMelody { get; private set; } = true;
        public bool CounterSameStaff { get; private set; }
        public int MelodyInstrument { get; private set; } = -1;   // -1 = style default
        public int AccompInstrument { get; private set; } = -1;
        public int PadInstrument { get; private set; } = -1;

        static Brush Res(string key) => (Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null) ?? Brushes.Gray;

        public CreateStructureDialog(KeySignature projectKey, double projectBpm, int projectMeterNum = 4, int projectMeterDen = 4)
        {
            InitializeComponent();
            // pre-select the project's meter (else 4/4 from the XAML default)
            string want = projectMeterNum + "/" + projectMeterDen;
            for (int i = 0; i < meterBox.Items.Count; i++)
                if ((meterBox.Items[i] as ComboBoxItem)?.Content?.ToString() == want) { meterBox.SelectedIndex = i; break; }

            // The MODÈLE list = the Orchestrateur entries the registry built (one per corpus model). Selecting a model
            // refreshes the Forme + Style + Tonalité + Humeur + Développement combos from that composer's own options.
            composers = MusicComposers.All.Where(c => c is Orchestrateur).ToList();
            foreach (var c in composers) styleBox.Items.Add(c.Name);
            styleBox.SelectedIndex = composers.Count > 0 ? 0 : -1;   // fires StyleBox_Changed → RefreshOptionCombos
            RefreshOptionCombos();

            // Instrument pickers: "Auto (style)" (= keep the style's default) then the full instrument list.
            foreach (var box in new[] { melInstBox, accInstBox, padInstBox })
            {
                box.Items.Add("Auto (style)");
                foreach (var n in Engine.InstrumentCatalog.Names()) box.Items.Add(n);
                box.SelectedIndex = 0;
            }

            foreach (var t in ToneLabels) toneBox.Items.Add(t);
            int keyPc = projectKey != null ? Mod12(LetterPc[Clamp(projectKey.TonicLetter, 0, 6)] + projectKey.Accidental) : 0;
            toneBox.SelectedIndex = ToneIndexForPc(keyPc);
            tempoBox.Text = ((int)(projectBpm > 0 ? projectBpm : 60)).ToString();

            foreach (var tb in new[] { tempoBox, introBox, themeBox, repsBox, outroBox })
            { tb.Foreground = Res("CommonForeground"); tb.Background = Res("TextBoxOuterBackground"); tb.BorderBrush = Res("TextBoxOuterBorder"); tb.Padding = new Thickness(2, 1, 2, 1); }

            // Restore the last-used selections (so the user needn't re-pick every time). Model by NAME; option combos by
            // index (clamped). Setting the model first re-fills the option combos, then we apply the saved indices.
            var st = AppSettings.Instance.Structure;
            if (st != null && st.Saved)
            {
                int mi = composers.FindIndex(c => c.Name == st.Model);
                if (mi >= 0) styleBox.SelectedIndex = mi;
                SelectClamped(formBox, st.Form);
                SelectClamped(compStyleBox, st.Style);
                SelectClamped(modeBox, st.Mode);
                SelectByTag(charBox, st.Char);   // charBox items are Tagged with their original index
                SelectClamped(devBox, st.Dev);
                SelectClamped(toneBox, st.Tone);
                tempoBox.Text = st.Tempo.ToString();
                themeBox.Text = st.ThemeBars.ToString();
                introBox.Text = st.IntroBars.ToString();
                outroBox.Text = st.OutroBars.ToString();
                repsBox.Text = st.Reps.ToString();
                musicBox.IsChecked = st.GenerateMusic;
                counterSameStaffBox.IsChecked = st.CounterSameStaff;
                SelectClamped(melInstBox, st.MelodyInst + 1);   // -1 (Auto) → index 0
                SelectClamped(accInstBox, st.AccompInst + 1);
                SelectClamped(padInstBox, st.PadInst + 1);
            }
        }

        static void SelectClamped(ComboBox box, int idx)
        {
            if (box.Items.Count == 0) return;
            box.SelectedIndex = Math.Min(Math.Max(0, idx), box.Items.Count - 1);
        }

        static void FillFromOption(ComboBox box, MusicComposer comp, string key)
        {
            ComposerOption opt = null;
            foreach (var o in comp.Options) if (o.Key == key) { opt = o; break; }
            if (opt != null) { foreach (var c in opt.Choices) box.Items.Add(c); box.SelectedIndex = Math.Min(opt.Default, opt.Choices.Length - 1); }
        }

        MusicComposer SelectedComposer() =>
            (composers.Count > 0 && styleBox.SelectedIndex >= 0 && styleBox.SelectedIndex < composers.Count) ? composers[styleBox.SelectedIndex] : null;

        // Refill the Forme + Style + Mode + Humeur + Développement combos from the selected composer's declared options.
        void RefreshOptionCombos()
        {
            if (formBox == null) return;   // not yet initialized
            formBox.Items.Clear(); modeBox.Items.Clear(); charBox.Items.Clear(); compStyleBox.Items.Clear(); devBox.Items.Clear();
            var comp = SelectedComposer();
            if (comp == null) return;
            FillFromOption(formBox, comp, "form");
            FillFromOption(compStyleBox, comp, "style");
            FillFromOption(modeBox, comp, "mode");
            FillChar(comp);   // Humeur = only the moods AVAILABLE in the catalogue (items tagged with their original index)
            FillFromOption(devBox, comp, "dev");
        }

        // Fill the Humeur combo with only the moods the catalogue provides for this family (Auto always). Each item's Tag
        // carries its ORIGINAL "char" index (so pruning doesn't shift the value the Orchestrateur expects). No catalogue → all.
        void FillChar(MusicComposer comp)
        {
            ComposerOption opt = null;
            foreach (var o in comp.Options) if (o.Key == "char") { opt = o; break; }
            if (opt == null) return;
            var idxs = (comp as Orchestrateur)?.CatalogueMoodIndices();
            if (idxs == null || idxs.Count == 0)
                for (int i = 0; i < opt.Choices.Length; i++) charBox.Items.Add(new ComboBoxItem { Content = opt.Choices[i], Tag = i });
            else
                foreach (int i in idxs) if (i >= 0 && i < opt.Choices.Length) charBox.Items.Add(new ComboBoxItem { Content = opt.Choices[i], Tag = i });
            if (charBox.Items.Count > 0) charBox.SelectedIndex = 0;
        }

        static void SelectByTag(ComboBox box, int tag)
        {
            for (int i = 0; i < box.Items.Count; i++)
                if (box.Items[i] is ComboBoxItem cbi && cbi.Tag is int t && t == tag) { box.SelectedIndex = i; return; }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }
        static int SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag is int t ? t : 0;

        void StyleBox_Changed(object sender, SelectionChangedEventArgs e) => RefreshOptionCombos();
        void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
        void Ok_Click(object sender, RoutedEventArgs e) => OnCreate();

        void OnCreate()
        {
            double bpm = ParseInt(tempoBox.Text, 60, 20, 300);
            int ti = Math.Max(0, toneBox.SelectedIndex);

            ChosenComposer = SelectedComposer();
            ChosenKey = new KeySignature { TonicLetter = ToneLetter[ti], Accidental = ToneAccid[ti] };
            // The FORM drives the skeleton; these sizes OVERRIDE the section lengths (intro/outro 0 = omit).
            Options = new Dictionary<string, int>
            {
                { "form", Math.Max(0, formBox.SelectedIndex) },        // chosen form
                { "style", Math.Max(0, compStyleBox.SelectedIndex) },  // composer texture/movement
                { "mode", Math.Max(0, modeBox.SelectedIndex) },        // Majeur / Mineur
                { "char", SelectedTag(charBox) },                      // Humeur (original char index via item Tag)
                { "dev", Math.Max(0, devBox.SelectedIndex) },          // méthode de développement (0 = Auto)
            };
            ThemeBars = ParseInt(themeBox.Text, 4, 1, 64);
            IntroBars = ParseInt(introBox.Text, 4, 0, 32);
            OutroBars = ParseInt(outroBox.Text, 4, 0, 32);
            ThemeReps = ParseInt(repsBox.Text, 2, 1, 12);   // number of variations / development repetitions
            Bpm = bpm;
            GenerateMusic = musicBox.IsChecked == true;
            var mparts = ((meterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "4/4").Split('/');
            if (mparts.Length == 2 && int.TryParse(mparts[0], out int mn) && int.TryParse(mparts[1], out int md) && mn > 0 && md > 0)
            { MeterNum = mn; MeterDen = md; }
            IncludePad = padBox.IsChecked == true;
            IncludeBass = bassBox.IsChecked == true;
            IncludeCounter = counterBox.IsChecked == true;
            IncludeIntroMelody = introMelBox.IsChecked == true;
            CounterSameStaff = counterSameStaffBox.IsChecked == true;
            MelodyInstrument = melInstBox.SelectedIndex - 1;   // 0 = "Auto (style)" → -1
            AccompInstrument = accInstBox.SelectedIndex - 1;
            PadInstrument = padInstBox.SelectedIndex - 1;

            // Remember everything for next time (persisted to settings.json).
            var st = AppSettings.Instance.Structure;
            st.Saved = true;
            st.Model = ChosenComposer != null ? ChosenComposer.Name : "";
            st.Form = Math.Max(0, formBox.SelectedIndex);
            st.Style = Math.Max(0, compStyleBox.SelectedIndex);
            st.Mode = Math.Max(0, modeBox.SelectedIndex);
            st.Char = SelectedTag(charBox);
            st.Dev = Math.Max(0, devBox.SelectedIndex);
            st.Tone = ti;
            st.Tempo = (int)bpm;
            st.ThemeBars = ThemeBars; st.IntroBars = IntroBars; st.OutroBars = OutroBars; st.Reps = ThemeReps;
            st.GenerateMusic = GenerateMusic;
            st.CounterSameStaff = CounterSameStaff;
            st.MelodyInst = MelodyInstrument; st.AccompInst = AccompInstrument; st.PadInst = PadInstrument;
            AppSettings.Instance.Save();

            DialogResult = true;
        }

        static int ParseInt(string s, int dflt, int lo, int hi)
        {
            int v;
            if (!int.TryParse((s ?? "").Trim(), out v)) v = dflt;
            return Math.Max(lo, Math.Min(hi, v));
        }

        static int Mod12(int x) { int r = x % 12; return r < 0 ? r + 12 : r; }
        static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
        static int ToneIndexForPc(int pc)
        {
            for (int i = 0; i < ToneLabels.Length; i++) if (Mod12(LetterPc[ToneLetter[i]] + ToneAccid[i]) == pc) return i;
            return 0;
        }
    }
}
