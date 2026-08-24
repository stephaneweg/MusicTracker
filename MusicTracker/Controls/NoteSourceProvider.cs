using System;
using System.Collections.Generic;
using System.Windows.Input;
using NAudio.Midi;
using NAudio.Wave;
using MusicTracker.Engine;

namespace MusicTracker.Controls
{
    /// <summary>
    /// A source of note on/off events feeding the riff editor — PC keyboard, MIDI, or audio. Each implementation
    /// decides WHEN a pitch is on/off; the editor turns the events into growing notes (cursor / snap stay there).
    /// Note numbers are app note indices (0..95, note 0 = MIDI 12 = C0).
    /// </summary>
    public interface INoteSourceProvider : IDisposable
    {
        event Action<int> NoteOn;
        event Action<int> NoteOff;
        void Start();
        void Stop();
    }

    /// <summary>
    /// PC keyboard: letters → notes via an editor-supplied mapping (octave / scale / accidental, read live). The
    /// editor forwards KeyDown/KeyUp and calls <see cref="Poll"/> each frame to catch a missed KeyUp (focus loss).
    /// </summary>
    public sealed class KeyboardNoteSourceProvider : INoteSourceProvider
    {
        readonly Func<Key, int> keyToNote;            // -1 if the key isn't a note
        readonly Dictionary<Key, int> held = new Dictionary<Key, int>(); // key -> note

        public event Action<int> NoteOn;
        public event Action<int> NoteOff;

        public KeyboardNoteSourceProvider(Func<Key, int> keyToNote) { this.keyToNote = keyToNote; }

        public void Start() { }
        public void Stop() { ReleaseAll(); }

        public void KeyDown(Key k)
        {
            if (held.ContainsKey(k)) return;          // already held (auto-repeat)
            int note = keyToNote(k);
            if (note < 0) return;
            held[k] = note;
            NoteOn?.Invoke(note);
        }

        public void KeyUp(Key k)
        {
            if (held.TryGetValue(k, out int note)) { held.Remove(k); NoteOff?.Invoke(note); }
        }

        // Finalise keys that are no longer physically down (a KeyUp can be lost on focus change).
        public void Poll()
        {
            if (held.Count == 0) return;
            foreach (var k in new List<Key>(held.Keys))
                if (!Keyboard.IsKeyDown(k)) { int note = held[k]; held.Remove(k); NoteOff?.Invoke(note); }
        }

        void ReleaseAll() { foreach (var n in new List<int>(held.Values)) NoteOff?.Invoke(n); held.Clear(); }
        public void Dispose() => Stop();
    }

    /// <summary>
    /// MIDI input device. <see cref="SetDevice"/> (re)opens the chosen device on the fly. NoteOn/NoteOff messages
    /// are marshalled to the UI thread via the supplied delegate.
    /// </summary>
    public sealed class MidiNoteSourceProvider : INoteSourceProvider
    {
        readonly Action<Action> toUi;
        MidiIn midiIn;
        int deviceIndex = -1;
        bool started;

        public event Action<int> NoteOn;
        public event Action<int> NoteOff;

        public MidiNoteSourceProvider(Action<Action> marshalToUi) { toUi = marshalToUi; }

        public void SetDevice(int index) { deviceIndex = index; if (started) Reopen(); }
        public void Start() { started = true; Reopen(); }
        public void Stop() { started = false; Close(); }

        void Reopen()
        {
            Close();
            if (deviceIndex < 0 || deviceIndex >= MidiIn.NumberOfDevices) return;
            try
            {
                midiIn = new MidiIn(deviceIndex);
                midiIn.MessageReceived += OnMsg;
                midiIn.ErrorReceived += (s, e) => { };
                midiIn.Start();
            }
            catch { midiIn = null; }
        }

        void Close() { if (midiIn != null) { try { midiIn.Stop(); midiIn.Dispose(); } catch { } midiIn = null; } }

        void OnMsg(object sender, MidiInMessageEventArgs e)
        {
            var ne = e.MidiEvent as NoteEvent;
            if (ne == null) return;
            bool on = ne.CommandCode == MidiCommandCode.NoteOn && (!(ne is NoteOnEvent noe) || noe.Velocity > 0);
            bool off = ne.CommandCode == MidiCommandCode.NoteOff || (ne is NoteOnEvent noe2 && noe2.Velocity == 0);
            int note = ne.NoteNumber - 12; // MIDI -> app note
            if (on) toUi(() => NoteOn?.Invoke(note));
            else if (off) toUi(() => NoteOff?.Invoke(note));
        }

        public void Dispose() => Stop();
    }

    /// <summary>
    /// Audio input: live MONOPHONIC pitch detection (MPM). A background WaveIn callback fills a rolling window and
    /// detects the pitch each hop; a new pitch (or silence) must persist for <c>holdSeconds()</c> before it replaces
    /// the current note (NoteOff old + NoteOn new), snapped to <c>scaleMask()</c>. Events marshalled to the UI thread.
    /// </summary>
    public sealed class WaveNoteSourceProvider : INoteSourceProvider
    {
        readonly Action<Action> toUi;
        readonly Func<int> scaleMask;
        readonly Func<double> holdSeconds;
        readonly Func<double> onsetSensitivity; // 0..1: re-attack sensitivity (détaché on the same pitch)
        readonly int sampleRate;
        const double Rms = 0.012;   // seuil de voisement par défaut, hérité de l'éditeur de riff
        /// <summary>Valeur par défaut de <see cref="SilenceThreshold"/> — celle avec laquelle l'éditeur de
        /// riff a été réglé à l'oreille.</summary>
        public const double DefaultSilenceThreshold = Rms;
        const double EnvDecay = 0.92;     // amplitude-envelope decay per hop (~150 ms) for onset detection
        const double MaxHoldSeconds = 0.03; // cap on the pitch-change debounce (decoupled from tempo → low latency)

        WaveInEvent waveIn;
        int deviceIndex = -1;
        bool started, prioritySet;

        float[] ring, frame;              // sized to AudioPitch.FrameSize at Reopen (picks up a settings change)
        int ringPos, ringFill, sinceHop;
        int curNote = -1, cand = -1, candCount; // hysteresis (callback thread)
        int raised = -1;                         // currently-raised NoteOn
        double env;                              // amplitude envelope (for onset/re-attack detection)
        int framesSinceOnset;
        int onsetRefractory;                     // min frames between onsets (debounce)
        int hopLen;                              // analysis hop = frame/4 — smaller = analysed more often → lower latency
        double[] pitchHist;                      // hauteurs continues (MIDI flottant) des dernières fenêtres voisées
        int pitchCount;                          // remplissage de pitchHist (remise à zéro à chaque silence)

        public event Action<int> NoteOn;
        public event Action<int> NoteOff;

        /// <summary>Niveau (RMS 0..1) de la dernière fenêtre analysée. Purement informatif : permet à une UI
        /// d'afficher un vu-mètre de l'entrée de DÉTECTION (qui n'est pas forcément celle du moteur audio) et
        /// de voir si le signal atteint le seuil de voisement — sans ça, un micro trop faible se traduit par
        /// « aucune note » sans la moindre explication.</summary>
        public double InputLevel { get; private set; }

        /// <summary>Seuil de voisement : en dessous de ce niveau (RMS de la fenêtre d'analyse), le signal est
        /// tenu pour du silence — ni note, ni ré-attaque. L'abaisser rend la détection plus sensible (voix
        /// douce, instrument éloigné du micro) au prix de fausses notes sur le bruit de fond ; le monter fait
        /// l'inverse. Lu depuis le thread de capture, écrit depuis l'UI : un double se lit d'un bloc, aucun
        /// verrou nécessaire.</summary>
        public double SilenceThreshold { get; set; } = Rms;

        /// <summary>Durée minimale, en secondes, qu'un changement doit tenir avant d'être joué (0 = comportement
        /// historique, réaction immédiate). Un candidat — autre hauteur, ou silence — qui ne tient pas ce temps
        /// est PUREMENT IGNORÉ : aucun événement n'est émis et la note en cours continue, comme si le
        /// décrochage n'avait pas eu lieu. C'est ce qui nettoie les micro-glissements d'un archet ou d'une voix
        /// sans avoir à filtrer après coup. Le prix est de la latence à l'attaque, d'où le réglage exposé.</summary>
        public double MinNoteSeconds { get; set; }

        /// <summary>Écart maximal, en demi-tons, entre la note en cours et une nouvelle hauteur acceptable
        /// (0 = pas de limite, comportement historique). Au-delà, l'analyse s'est presque sûrement trompée
        /// d'octave ou a mordu sur une harmonique : la trame est IGNORÉE — aucun événement, la note en cours
        /// continue, et le candidat en cours d'accumulation n'est même pas perturbé. Le retour au silence,
        /// lui, n'est jamais un « saut » et passe toujours : arrêter de jouer une seconde suffit donc à
        /// atteindre n'importe quelle hauteur, si loin soit-elle.</summary>
        public int MaxLeapSemitones { get; set; }

        /// <summary>Taille de la fenêtre d'analyse en échantillons ; 0 = suivre le réglage global
        /// <see cref="AudioPitch.FrameSize"/>. Une fenêtre plus large lève l'ambiguïté sur les notes graves
        /// (il faut plusieurs périodes pour reconnaître une période) au prix de la latence — 2048 à 44,1 kHz
        /// = 46 ms de signal. Propriété d'INSTANCE : le rack live peut donc choisir la sienne sans déplacer
        /// le réglage de l'éditeur de riff. Prise en compte au prochain <see cref="Start"/>.</summary>
        public int FrameSize { get; set; }

        /// <summary>Nombre de fenêtres sur lesquelles la hauteur CONTINUE est filtrée par médiane avant d'être
        /// arrondie au demi-ton (1 = aucun lissage). C'est le point qui fait le plus pour la justesse : arrondir
        /// chaque fenêtre isolément fait papilloter une note chantée 40 cents bas entre deux demi-tons, alors
        /// que la médiane des dernières hauteurs tranche pour celle qui domine — exactement ce que fait déjà la
        /// transcription hors-ligne (<see cref="AudioPitch"/>) avant de quantifier. Coûte (k-1)/2 fenêtres de
        /// latence, soit ~12 ms pour 3 à 44,1 kHz.</summary>
        public int MedianFrames { get; set; } = 3;

        /// <summary>Marge d'accroche, en cents, autour de la note en cours (0 = aucune). Tant que la hauteur
        /// reste à moins d'un demi-demi-ton PLUS cette marge du centre de la note tenue, on ne bascule pas.
        /// C'est ce qui manque le plus à une détection live : une voix qui se pose 45 cents bas est PILE sur la
        /// frontière, et le moindre vibrato la fait alterner entre les deux demi-tons voisins — un filtre
        /// temporel n'y peut rien, puisque le signal traverse vraiment la frontière. Un accordeur chromatique
        /// résout ça exactement comme ici : il verrouille la note et demande un écart franc pour en changer.
        /// Une vraie note voisine, elle, est à 100 cents : elle passe sans délai.</summary>
        public double SnapHysteresisCents { get; set; } = 20;

        /// <summary>Fréquence la plus grave recherchée, en Hz (0 = la valeur par défaut de l'analyseur, 70 Hz).
        /// C'est le remède le plus direct aux sauts vers le grave : une erreur d'octave basse consiste à
        /// verrouiller sur la période DOUBLE, donc sur une fréquence moitié — la placer hors de la plage
        /// recherchée la rend impossible. À régler sur la note la plus grave que l'instrument produit.</summary>
        public double MinFrequency { get; set; }

        /// <summary>Fréquence la plus aiguë recherchée, en Hz (0 = défaut, 1200 Hz).</summary>
        public double MaxFrequency { get; set; }

        /// <summary>Biais anti-octave-basse, 0..1 (0 = comportement par défaut). Abaisse le seuil MPM, donc
        /// fait retenir un pic plus précoce — une période plus courte, une fréquence plus haute. À monter si
        /// des sauts vers le grave persistent malgré <see cref="MinFrequency"/>, à laisser bas si l'inverse se
        /// produit (sauts vers l'aigu).</summary>
        public double OctaveBias { get; set; }

        public WaveNoteSourceProvider(Action<Action> marshalToUi, Func<int> scaleMask, Func<double> holdSeconds, Func<double> onsetSensitivity, int sampleRate)
        {
            toUi = marshalToUi; this.scaleMask = scaleMask; this.holdSeconds = holdSeconds; this.onsetSensitivity = onsetSensitivity; this.sampleRate = sampleRate;
        }

        public void SetDevice(int index) { deviceIndex = index; if (started) Reopen(); }
        public void Start() { started = true; Reopen(); }

        public void Stop()
        {
            started = false; Close();
            if (raised >= 0) { int n = raised; raised = -1; toUi(() => NoteOff?.Invoke(n)); }
        }

        void Reopen()
        {
            Close();
            int fr = Math.Max(256, FrameSize > 0 ? FrameSize : AudioPitch.FrameSize);
            if (ring == null || ring.Length != fr) { ring = new float[fr]; frame = new float[fr]; }
            hopLen = Math.Max(64, fr / 4); // analyse 4×/window → fast confirmation; CPU stays bounded (scales with fr)
            onsetRefractory = Math.Max(2, (int)Math.Round(0.06 / ((double)hopLen / sampleRate))); // ~60 ms debounce
            ringPos = ringFill = sinceHop = 0; curNote = cand = -1; candCount = 0; env = 0; framesSinceOnset = 0; prioritySet = false;
            pitchCount = 0;
            if (WaveInEvent.DeviceCount == 0) return;
            try
            {
                // Small input buffer = low latency (3 buffers keep it stable). Detection latency is still bounded
                // below by the MPM analysis window (FrameSize samples) — use a smaller window for low latency.
                waveIn = new WaveInEvent { WaveFormat = new WaveFormat(sampleRate, 16, 1), BufferMilliseconds = 10, NumberOfBuffers = 3 };
                if (deviceIndex >= 0 && deviceIndex < WaveInEvent.DeviceCount) waveIn.DeviceNumber = deviceIndex;
                waveIn.DataAvailable += OnData;
                waveIn.StartRecording();
            }
            catch { waveIn = null; }
        }

        void Close() { if (waveIn != null) { try { waveIn.StopRecording(); waveIn.Dispose(); } catch { } waveIn = null; } }

        void OnData(object sender, WaveInEventArgs e)
        {
            // Bump the capture/detection thread above normal so analysis isn't preempted (lower jitter/latency).
            // NOT process-level realtime — that would starve the rest of the app.
            if (!prioritySet) { prioritySet = true; try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest; } catch { } }

            int fr = ring.Length, hop = hopLen;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                short s16 = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                ring[ringPos] = s16 / 32768f; ringPos = (ringPos + 1) % fr; if (ringFill < fr) ringFill++;
                if (++sinceHop >= hop && ringFill >= fr) { sinceHop = 0; Analyze(); }
            }
        }

        void Analyze()
        {
            int fr = frame.Length;
            double energy = 0;
            for (int i = 0; i < fr; i++) { float v = ring[(ringPos + i) % fr]; frame[i] = v; energy += v * v; } // unroll + RMS
            double rms = Math.Sqrt(energy / fr);
            InputLevel = rms;

            // Onset = a re-attack louder than the running envelope (a fresh bow stroke / pluck on the SAME pitch).
            framesSinceOnset++;
            double sens = onsetSensitivity != null ? Math.Max(0, Math.Min(1, onsetSensitivity())) : 0;
            double rise = 2.2 - 1.05 * sens;   // sens 0 -> needs a 2.2× spike; sens 1 -> 1.15× (very sensitive)
            double gate = SilenceThreshold;
            // Une ré-attaque ne peut pas non plus produire une note plus courte que la durée minimale.
            int minFrames = MinNoteSeconds > 0 ? Math.Max(1, (int)Math.Round(MinNoteSeconds / ((double)hopLen / sampleRate))) : 0;
            bool onset = rms > gate && rms > env * rise && framesSinceOnset >= Math.Max(onsetRefractory, minFrames);
            env = Math.Max(rms, env * EnvDecay);

            // Plage de recherche et seuil MPM : réglables ici (le rack live les expose), défauts inchangés
            // pour tout le reste de l'application. Biais 0..1 -> seuil 0,90 (défaut) à 0,70 (agressif).
            double f = (MinFrequency > 0 || MaxFrequency > 0 || OctaveBias > 0)
                ? AudioPitch.DetectFramePitch(frame, 0, sampleRate, gate,
                      MinFrequency > 0 ? MinFrequency : 70,
                      MaxFrequency > 0 ? MaxFrequency : 1200,
                      0.90 - 0.20 * Math.Max(0, Math.Min(1, OctaveBias)))
                : AudioPitch.DetectFramePitch(frame, 0, sampleRate, gate);
            int note;
            if (f > 0)
            {
                // Médiane sur la hauteur CONTINUE, puis arrondi : une note tenue un peu fausse se range du
                // côté qui domine au lieu de papilloter, et une fenêtre isolément aberrante est écartée.
                double midi = MedianPitch(69 + 12 * Math.Log(f / 440.0, 2));
                note = AudioPitch.NoteIndexFromMidi(midi, scaleMask());
                // Accroche sur la note tenue : on n'en sort que si la hauteur s'en éloigne franchement.
                if (note != curNote && curNote >= 0 && SnapHysteresisCents > 0
                    && Math.Abs(midi - (curNote + 12)) <= 0.5 + SnapHysteresisCents / 100.0)
                    note = curNote;
            }

            else
            {
                // Silence : on repart de zéro, sinon les premières fenêtres de la note suivante seraient
                // tirées vers la hauteur de la précédente.
                pitchCount = 0;
                note = -1;
            }

            // Saut invraisemblable depuis la note en cours : erreur d'analyse, pas un vrai changement.
            // (Les index de note sont chromatiques, donc l'écart EST le nombre de demi-tons.)
            if (MaxLeapSemitones > 0 && note >= 0 && curNote >= 0 && Math.Abs(note - curNote) > MaxLeapSemitones)
                return;

            if (note == curNote)
            {
                cand = -1; candCount = 0;
                if (onset && curNote >= 0 && raised == curNote) // détaché: re-attack on the same note -> re-articulate
                {
                    framesSinceOnset = 0;
                    int nn = curNote;
                    toUi(() => { NoteOff?.Invoke(nn); NoteOn?.Invoke(nn); });
                }
                return;
            }

            if (note == cand) candCount++; else { cand = note; candCount = 1; }
            // Debounce a pitch change by a FIXED short window (~2-3 hops, max 30 ms), NOT the musical slice
            // duration — otherwise a slow tempo / coarse grid would add up to hundreds of ms of latency.
            double hopTime = (double)hopLen / sampleRate;
            int hold = Math.Max(2, (int)Math.Round(Math.Min(holdSeconds(), MaxHoldSeconds) / hopTime));
            int need = curNote < 0 ? 1 : hold; // attack from silence: accept on the FIRST solid frame (lowest latency)
            // La durée minimale demandée par l'utilisateur prime, y compris à l'attaque : sans ça un couac
            // parti du silence passerait quand même (on l'accepte à la première fenêtre solide).
            if (minFrames > need) need = minFrames;
            if (candCount >= need)
            {
                curNote = note; cand = -1; candCount = 0; framesSinceOnset = 0;
                int nn = note;
                toUi(() =>
                {
                    if (raised >= 0) { int old = raised; raised = -1; NoteOff?.Invoke(old); }
                    if (nn >= 0) { raised = nn; NoteOn?.Invoke(nn); }
                });
            }
        }

        /// <summary>Empile <paramref name="midi"/> et renvoie la médiane des dernières hauteurs continues.
        /// Fenêtre glissante bornée à <see cref="MedianFrames"/> ; tant qu'elle n'est pas pleine, la médiane
        /// porte sur ce qu'on a (pas d'attente au démarrage d'une note).</summary>
        double MedianPitch(double midi)
        {
            int k = Math.Max(1, Math.Min(15, MedianFrames));
            if (k == 1) return midi;
            if (pitchHist == null || pitchHist.Length != k) { pitchHist = new double[k]; pitchCount = 0; }
            // Décalage plutôt que tampon circulaire : k <= 15, c'est gratuit et ça garde le tri trivial.
            if (pitchCount < k) pitchHist[pitchCount++] = midi;
            else { Array.Copy(pitchHist, 1, pitchHist, 0, k - 1); pitchHist[k - 1] = midi; }

            var sorted = new double[pitchCount];
            Array.Copy(pitchHist, sorted, pitchCount);
            Array.Sort(sorted);
            return (pitchCount & 1) == 1 ? sorted[pitchCount / 2]
                                         : 0.5 * (sorted[pitchCount / 2 - 1] + sorted[pitchCount / 2]);
        }

        public void Dispose() => Stop();
    }
}
