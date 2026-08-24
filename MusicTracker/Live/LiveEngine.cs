using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using MusicTracker.Engine;
using MusicTracker.Engine.Timeline.Effects;

namespace MusicTracker.Live
{
    /// <summary>Ce que le moteur fait du signal.</summary>
    public enum LiveMode
    {
        /// <summary>Micro / entrée ligne → chaîne d'effets → carte son. Le programme est alors un simple
        /// rack d'effets temps réel.</summary>
        Insert,
        /// <summary>Micro (détection de hauteur) ou clavier MIDI → instrument (SoundFont, VSTi, Koton) →
        /// chaîne d'effets → carte son.</summary>
        Instrument,
    }

    /// <summary>
    /// Moteur audio temps réel autonome : capture (WASAPI ou ASIO) → [instrument] → chaîne d'inserts →
    /// sortie. C'est le pendant « live » de <c>TimelinePlayer</c>, sans notion de morceau ni de temps
    /// musical : le graphe est fixe et tourne tant qu'on ne l'arrête pas.
    ///
    /// **Deux backends, un seul chemin de rendu.** Quel que soit le backend, la sortie est produite par
    /// <see cref="RenderInto"/> et l'entrée est déposée dans un tampon circulaire par le thread de capture.
    /// En ASIO les deux se produisent dans le MÊME callback pilote (l'événement d'entrée est levé juste
    /// avant que la sortie ne soit tirée du provider), ce qui ramène la boucle micro→haut-parleur à un seul
    /// buffer ; en WASAPI capture et rendu sont deux horloges distinctes, d'où le tampon circulaire qui
    /// absorbe leur dérive.
    ///
    /// **Verrouillage.** Les notes arrivent d'un thread MIDI ou du thread de détection de hauteur, jamais du
    /// thread audio : c'est <see cref="LiveInstrument"/> qui se protège. La chaîne d'effets, elle, est
    /// remplacée par ÉCHANGE DE RÉFÉRENCE (<see cref="RebuildEffects"/>) — le thread audio lit le tableau
    /// une seule fois par bloc, donc ajouter un effet pendant que ça joue ne déchire jamais un buffer.
    /// </summary>
    public sealed class LiveEngine : IDisposable
    {
        /// <summary>Taille de bloc maximale annoncée aux plugins (VST comme Koton) et plafond de découpe des
        /// buffers de rendu. Généreux volontairement : un plugin préparé une fois pour 8192 frames accepte
        /// tous les blocs plus petits, alors que renégocier la taille en cours de route casse beaucoup de
        /// VST natifs.</summary>
        public const int MaxBlockSize = 8192;

        // Découpe du rendu : un buffer WASAPI de 100 ms à 48 kHz fait 4800 frames, on le traite en tranches
        // pour garder des tableaux de travail bornés et une empreinte cache raisonnable.
        const int ChunkFrames = 1024;

        // ---- configuration (lue au moment du Start) ----------------------------------------------------

        public LiveBackend Backend { get; set; } = LiveBackend.Wasapi;
        /// <summary>Id d'endpoint WASAPI de l'entrée ; <c>null</c> = entrée par défaut de Windows.</summary>
        public string InputDeviceId { get; set; }
        /// <summary>Id d'endpoint WASAPI de la sortie ; <c>null</c> = sortie par défaut de Windows.</summary>
        public string OutputDeviceId { get; set; }
        /// <summary>Nom du pilote ASIO (backend ASIO uniquement).</summary>
        public string AsioDriver { get; set; }
        /// <summary>Premier canal d'entrée ASIO utilisé (offset dans les entrées du pilote).</summary>
        public int AsioInputChannel { get; set; }
        /// <summary>Latence demandée en ms (WASAPI). ASIO utilise la taille de buffer réglée dans le panneau
        /// du pilote — d'où le bouton « Panneau ASIO » de la fenêtre.</summary>
        public int LatencyMs { get; set; } = 25;
        public LiveMode Mode { get; set; } = LiveMode.Insert;
        /// <summary>Gain appliqué à l'entrée avant les effets (1 = neutre).</summary>
        public double InputGain { get; set; } = 1.0;
        /// <summary>Gain de sortie appliqué juste avant le limiteur (0.85 = niveau du reste de Koton).</summary>
        public double OutputGain { get; set; } = AudioFormat.OutputGain;
        /// <summary>Mode instrument : laisser AUSSI passer le signal d'entrée (s'entendre jouer du violon
        /// pendant que la détection de hauteur double la ligne au synthé).</summary>
        public bool MonitorInput { get; set; }

        /// <summary>Chaîne d'inserts, format identique à celui d'une piste de timeline (donc VST2, VST3,
        /// plugins Koton et les quatre effets maison). Modifiable depuis l'UI ; appeler
        /// <see cref="RebuildEffects"/> après changement.</summary>
        public List<TrackEffectData> Inserts { get; } = new List<TrackEffectData>();

        // ---- état --------------------------------------------------------------------------------------

        public bool IsRunning { get; private set; }
        /// <summary>Fréquence d'échantillonnage effective, connue seulement après <see cref="Start"/>.</summary>
        public int SampleRate { get; private set; } = 48000;
        /// <summary>Latence de sortie annoncée par le backend, en ms (0 si inconnue).</summary>
        public int ReportedLatencyMs { get; private set; }
        /// <summary>Nombre de fois où le rendu a manqué d'entrée (capture en retard) depuis le démarrage —
        /// un compteur qui grimpe = latence demandée trop basse.</summary>
        public int Underruns { get; private set; }
        /// <summary>Levé quand le moteur s'arrête de lui-même (pilote perdu, exception). Argument = message.</summary>
        public event Action<string> Failed;

        /// <summary>Instrument du mode « Instrument ». Remplaçable à chaud : l'ancien est libéré après
        /// l'échange de référence, une fois qu'aucun bloc ne peut plus le rendre.</summary>
        public LiveInstrument Instrument
        {
            get => _instrument;
            set
            {
                var old = _instrument;
                _instrument = value;               // échange atomique : le thread audio lit la référence une fois par bloc
                if (old != null && !ReferenceEquals(old, value))
                {
                    try { old.AllNotesOff(); } catch { }
                    try { old.Dispose(); } catch { }
                }
            }
        }
        volatile LiveInstrument _instrument;

        // ---- interne -----------------------------------------------------------------------------------

        sealed class FxSlot { public IAudioEffect Fx; public TrackEffectData Src; }
        volatile FxSlot[] _fx = Array.Empty<FxSlot>();

        WasapiOut _wasapiOut;
        WasapiCapture _capture;
        AsioOut _asio;
        InputRing _ring;

        int _outChannels = 2;
        // Format de capture (WASAPI) : décodé à la volée dans PushCapture.
        int _capChannels = 2, _capBytesPerSample = 4, _capRate = 48000;
        bool _capIsFloat = true;
        double _resPos;              // position fractionnaire du rééchantillonnage capture → moteur
        float _prevL, _prevR;        // dernière frame du bloc précédent (continuité de l'interpolation)
        float[] _capScratch;         // frames désentrelacées + rééchantillonnées, entrelacées stéréo

        float[] _l, _r, _instL, _instR;
        float _inPeak, _outPeak;

        /// <summary>Crête d'entrée depuis la dernière lecture (le vumètre lit et remet à zéro).</summary>
        public float ReadInputPeak() { float v = _inPeak; _inPeak = 0; return v; }
        /// <summary>Crête de sortie depuis la dernière lecture.</summary>
        public float ReadOutputPeak() { float v = _outPeak; _outPeak = 0; return v; }

        /// <summary>Vrai si la configuration courante a besoin de capturer l'entrée.</summary>
        public bool NeedsInput => Mode == LiveMode.Insert || MonitorInput;

        // ---- démarrage / arrêt --------------------------------------------------------------------------

        /// <summary>Démarre le moteur. Jette une exception explicite (message affichable) si le backend
        /// refuse de s'ouvrir ; l'appelant reste dans un état propre, rien n'est laissé à moitié démarré.</summary>
        public void Start()
        {
            if (IsRunning) return;
            Underruns = 0;
            try
            {
                if (Backend == LiveBackend.Asio) StartAsio();
                else StartWasapi();
                RebuildEffects();
                IsRunning = true;
            }
            catch
            {
                StopInternal();
                throw;
            }
        }

        void StartWasapi()
        {
            var outDev = LiveDevices.Resolve(OutputDeviceId, DataFlow.Render);
            var mix = outDev.AudioClient.MixFormat;
            SampleRate = mix.SampleRate;
            // On adopte le nombre de canaux du format partagé : le provider colle alors EXACTEMENT au format
            // du mixeur Windows, donc WasapiOut n'insère pas de rééchantillonneur (latence + qualité).
            _outChannels = Math.Max(1, Math.Min(8, mix.Channels));

            if (NeedsInput)
            {
                var inDev = LiveDevices.Resolve(InputDeviceId, DataFlow.Capture);
                _capture = new WasapiCapture(inDev, true, Math.Max(2, LatencyMs));
                var cf = _capture.WaveFormat;
                _capChannels = Math.Max(1, cf.Channels);
                _capBytesPerSample = Math.Max(1, cf.BitsPerSample / 8);
                _capIsFloat = cf.Encoding == WaveFormatEncoding.IeeeFloat;
                _capRate = cf.SampleRate;
                // Un buffer de capture de coussin : c'est le minimum qui absorbe la dérive entre les deux
                // horloges. La latence micro→sortie vaut donc grosso modo 3 × la latence demandée ; qui veut
                // moins prend ASIO (un seul callback, coussin nul).
                PrepareRing(Math.Max(64, SampleRate * Math.Max(2, LatencyMs) / 1000));
                _capture.DataAvailable += (s, e) => PushCapture(e.Buffer, e.BytesRecorded);
                _capture.RecordingStopped += (s, e) => { if (IsRunning && e.Exception != null) FailAsync(e.Exception.Message); };
            }

            _wasapiOut = new WasapiOut(outDev, AudioClientShareMode.Shared, true, Math.Max(2, LatencyMs));
            _wasapiOut.Init(new LiveWaveProvider(this, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, _outChannels)));
            _wasapiOut.PlaybackStopped += (s, e) => { if (IsRunning && e.Exception != null) FailAsync(e.Exception.Message); };
            ReportedLatencyMs = LatencyMs;

            _capture?.StartRecording();
            _wasapiOut.Play();
        }

        void StartAsio()
        {
            if (string.IsNullOrEmpty(AsioDriver)) throw new InvalidOperationException(Localization.Loc.T("LiveNoAsioDriver"));
            _asio = new AsioOut(AsioDriver);

            // Le pilote impose sa fréquence : on essaie les usuelles dans l'ordre et on garde la première
            // acceptée, plutôt que d'imposer 48 kHz à une carte qui tourne à 44,1.
            int rate = 0;
            foreach (int candidate in new[] { 48000, 44100, 96000, 88200 })
            {
                try { if (_asio.IsSampleRateSupported(candidate)) { rate = candidate; break; } } catch { }
            }
            SampleRate = rate > 0 ? rate : 44100;
            _outChannels = Math.Max(1, Math.Min(2, _asio.DriverOutputChannelCount));
            var provider = new LiveWaveProvider(this, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, _outChannels));

            if (NeedsInput && _asio.DriverInputChannelCount > 0)
            {
                int inCh = Math.Min(2, _asio.DriverInputChannelCount - Math.Max(0, AsioInputChannel));
                if (inCh < 1) inCh = 1;
                _capChannels = inCh; _capBytesPerSample = 4; _capIsFloat = true; _capRate = SampleRate;
                PrepareRing(0);
                _asio.InputChannelOffset = Math.Max(0, AsioInputChannel);
                _asio.AudioAvailable += OnAsioAudio;
                _asio.InitRecordAndPlayback(provider, inCh, SampleRate);
            }
            else
            {
                _asio.Init(provider);
            }

            _asio.DriverResetRequest += (s, e) => FailAsync(Localization.Loc.T("LiveAsioReset"));
            _asio.Play();
            // FramesPerBuffer est connu après Init : c'est la vraie latence du pilote, celle qui compte.
            try { ReportedLatencyMs = (int)Math.Round(1000.0 * _asio.FramesPerBuffer / SampleRate); } catch { }
        }

        /// <summary>Callback d'entrée ASIO : on convertit en float entrelacé et on dépose dans le tampon.
        /// La sortie est tirée juste après, dans le même callback pilote, par
        /// <see cref="LiveWaveProvider.Read"/> — d'où une boucle micro→sortie d'un seul buffer.</summary>
        void OnAsioAudio(object sender, AsioAudioAvailableEventArgs e)
        {
            try
            {
                int need = e.SamplesPerBuffer * _capChannels;
                if (_capScratch == null || _capScratch.Length < need) _capScratch = new float[need];
                e.GetAsInterleavedSamples(_capScratch);
                WriteToRing(_capScratch, e.SamplesPerBuffer, _capChannels);
                // On NE marque PAS WrittenToOutputBuffers : NAudio enchaîne alors sur la lecture du provider,
                // qui est notre rendu — c'est exactement l'ordre voulu.
            }
            catch { }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            StopInternal();
        }

        void StopInternal()
        {
            try { _capture?.StopRecording(); } catch { }
            try { _capture?.Dispose(); } catch { }
            _capture = null;
            try { _wasapiOut?.Stop(); } catch { }
            try { _wasapiOut?.Dispose(); } catch { }
            _wasapiOut = null;
            try { _asio?.Stop(); } catch { }
            try { _asio?.Dispose(); } catch { }
            _asio = null;
            try { _instrument?.AllNotesOff(); } catch { }
            foreach (var s in _fx) { try { s.Fx.Reset(); } catch { } }
            _ring = null;
            _resPos = 0; _prevL = _prevR = 0;
            _inPeak = _outPeak = 0;
        }

        void FailAsync(string message)
        {
            IsRunning = false;
            // Le backend nous parle depuis son propre thread : on ne touche à rien d'autre ici, la fenêtre
            // fera l'arrêt propre sur le thread UI.
            try { Failed?.Invoke(message); } catch { }
        }

        // ---- chaîne d'effets -----------------------------------------------------------------------------

        /// <summary>(Re)construit la chaîne d'inserts depuis <see cref="Inserts"/>. À appeler après tout
        /// ajout / retrait / réordonnancement. Les effets Koton passent par <see cref="KotonEffectCache"/>,
        /// donc l'éditeur d'un plugin agit sur l'INSTANCE QUI JOUE : bouger un curseur s'entend au bloc
        /// suivant, sans arrêter le moteur.</summary>
        public void RebuildEffects()
        {
            var slots = new List<FxSlot>();
            foreach (var d in Inserts)
            {
                if (d == null) continue;
                IAudioEffect fx = null;
                try { fx = EffectFactory.Create(d, SampleRate); } catch { }
                if (fx == null) continue;
                // Un VST charge sa DLL native au premier Process : on force l'ouverture ICI, sur le thread
                // appelant (l'UI), pour qu'un LoadLibrary de 300 ms ne tombe pas au milieu d'un bloc audio.
                try { (fx as IVstEditorHost)?.EnsureOpenedSync(MaxBlockSize); } catch { }
                slots.Add(new FxSlot { Fx = fx, Src = d });
            }
            _fx = slots.ToArray();   // échange de référence : le thread audio ne voit jamais un état partiel
        }

        /// <summary>Vide les queues (délais, réverbérations) sans arrêter le moteur.</summary>
        public void ResetEffects() { foreach (var s in _fx) { try { s.Fx.Reset(); } catch { } } }

        // ---- entrée ---------------------------------------------------------------------------------------

        /// <summary>Prépare le tampon d'entrée. <paramref name="prefillFrames"/> = coussin à constituer avant
        /// que le rendu ne commence à consommer : en WASAPI capture et sortie sont deux horloges libres, et
        /// sans coussin le tampon se vide à la moindre irrégularité (test mesuré : décrochage sur presque
        /// chaque bloc). En ASIO le coussin est NUL — l'entrée du bloc courant est déposée juste avant que le
        /// même callback ne tire la sortie, donc attendre ajouterait de la latence pour rien.</summary>
        void PrepareRing(int prefillFrames)
        {
            // Une seconde de marge : largement au-dessus de tout buffer réaliste, et le coût mémoire est
            // dérisoire (≈ 400 ko en stéréo 48 kHz).
            _ring = new InputRing(SampleRate, prefillFrames);
            _resPos = 0; _prevL = _prevR = 0;
        }

        /// <summary>Décodage d'un buffer de capture WASAPI (float 32 ou PCM 16/24/32 selon le pilote) vers
        /// des frames stéréo à la fréquence du moteur.</summary>
        void PushCapture(byte[] data, int bytes)
        {
            var ring = _ring;
            if (ring == null || bytes <= 0) return;
            int frameBytes = _capChannels * _capBytesPerSample;
            if (frameBytes <= 0) return;
            int frames = bytes / frameBytes;
            if (frames <= 0) return;

            int need = frames * _capChannels;
            if (_capScratch == null || _capScratch.Length < need) _capScratch = new float[need];
            int k = 0;
            for (int f = 0; f < frames; f++)
            {
                int b = f * frameBytes;
                for (int c = 0; c < _capChannels; c++, b += _capBytesPerSample)
                {
                    float v;
                    if (_capIsFloat) v = BitConverter.ToSingle(data, b);
                    else if (_capBytesPerSample == 2) v = (short)(data[b] | (data[b + 1] << 8)) / 32768f;
                    else if (_capBytesPerSample == 3) v = ((data[b] << 8) | (data[b + 1] << 16) | (data[b + 2] << 24)) / 2147483648f;
                    else v = (data[b] | (data[b + 1] << 8) | (data[b + 2] << 16) | (data[b + 3] << 24)) / 2147483648f;
                    _capScratch[k++] = v;
                }
            }
            WriteToRing(_capScratch, frames, _capChannels);
        }

        /// <summary>Dépose <paramref name="frames"/> frames entrelacées dans le tampon circulaire, en
        /// réduisant à la stéréo et en rééchantillonnant (interpolation linéaire) si la capture ne tourne
        /// pas à la fréquence du moteur — cas courant quand micro et sortie sont deux cartes différentes.</summary>
        void WriteToRing(float[] src, int frames, int channels)
        {
            var ring = _ring;
            if (ring == null || frames <= 0) return;

            float peak = _inPeak;
            double step = (double)_capRate / SampleRate;

            if (Math.Abs(step - 1.0) < 1e-9)
            {
                for (int f = 0; f < frames; f++)
                {
                    float l = src[f * channels];
                    float r = channels > 1 ? src[f * channels + 1] : l;
                    float a = Math.Abs(l) > Math.Abs(r) ? Math.Abs(l) : Math.Abs(r);
                    if (a > peak) peak = a;
                    ring.Write(l, r);
                }
                _prevL = src[(frames - 1) * channels];
                _prevR = channels > 1 ? src[(frames - 1) * channels + 1] : _prevL;
            }
            else
            {
                double pos = _resPos;   // dans [-1, 0) au début d'un bloc (sauf tout premier)
                while (pos <= frames - 1)
                {
                    int i0 = (int)Math.Floor(pos);
                    double fr = pos - i0;
                    float l0 = i0 < 0 ? _prevL : src[i0 * channels];
                    float r0 = i0 < 0 ? _prevR : (channels > 1 ? src[i0 * channels + 1] : l0);
                    int i1 = i0 + 1;
                    float l1 = src[i1 * channels];
                    float r1 = channels > 1 ? src[i1 * channels + 1] : l1;
                    float l = (float)(l0 + (l1 - l0) * fr);
                    float r = (float)(r0 + (r1 - r0) * fr);
                    float a = Math.Abs(l) > Math.Abs(r) ? Math.Abs(l) : Math.Abs(r);
                    if (a > peak) peak = a;
                    ring.Write(l, r);
                    pos += step;
                }
                _resPos = pos - frames;
                _prevL = src[(frames - 1) * channels];
                _prevR = channels > 1 ? src[(frames - 1) * channels + 1] : _prevL;
            }
            _inPeak = peak;
        }

        // ---- rendu -----------------------------------------------------------------------------------------

        /// <summary>Remplit un buffer de sortie entrelacé float32. Appelé par le backend (thread audio) :
        /// pas d'allocation, pas de verrou global, pas d'exception qui remonte.</summary>
        internal int RenderInto(byte[] buffer, int offset, int count)
        {
            int frameBytes = 4 * _outChannels;
            int frames = count / frameBytes;
            if (frames <= 0) return 0;

            int done = 0;
            while (done < frames)
            {
                int chunk = Math.Min(ChunkFrames, frames - done);
                RenderChunk(chunk);
                // Entrelacement vers la sortie : L/R sur les deux premiers canaux, silence au-delà (une
                // sortie 5.1 reçoit donc un vrai stéréo sur les frontales plutôt qu'un upmix arbitraire).
                int b = offset + (done * frameBytes);
                for (int i = 0; i < chunk; i++)
                {
                    for (int c = 0; c < _outChannels; c++)
                    {
                        float v = c == 0 ? _l[i] : (c == 1 ? _r[i] : 0f);
                        WriteFloat(buffer, b, v);
                        b += 4;
                    }
                }
                done += chunk;
            }
            return frames * frameBytes;
        }

        void RenderChunk(int frames)
        {
            EnsureScratch(frames);
            var mode = Mode;
            bool wantInput = mode == LiveMode.Insert || MonitorInput;

            // 1. entrée
            if (wantInput)
            {
                var ring = _ring;
                bool starved = false;
                int got = ring != null ? ring.Read(_l, _r, frames, out starved) : 0;
                if (got < frames)
                {
                    // Un tampon qui n'a pas encore fini de se remplir n'est PAS un décrochage : on ne compte
                    // que les vraies famines (le coussin était constitué et s'est vidé quand même).
                    if (starved) Underruns++;
                    for (int i = got; i < frames; i++) { _l[i] = 0f; _r[i] = 0f; }
                }
                double g = InputGain;
                if (Math.Abs(g - 1.0) > 1e-6)
                    for (int i = 0; i < frames; i++) { _l[i] = (float)(_l[i] * g); _r[i] = (float)(_r[i] * g); }
            }
            else
            {
                Array.Clear(_l, 0, frames); Array.Clear(_r, 0, frames);
            }

            // 2. instrument
            if (mode == LiveMode.Instrument)
            {
                var inst = _instrument;
                if (inst != null)
                {
                    inst.Render(_instL.AsSpan(0, frames), _instR.AsSpan(0, frames));
                    if (wantInput)
                        for (int i = 0; i < frames; i++) { _l[i] += _instL[i]; _r[i] += _instR[i]; }
                    else
                        for (int i = 0; i < frames; i++) { _l[i] = _instL[i]; _r[i] = _instR[i]; }
                }
            }

            // 3. inserts (ordre = trajet du signal, exactement comme sur une piste de timeline)
            var chain = _fx;
            for (int s = 0; s < chain.Length; s++)
            {
                var slot = chain[s];
                if (slot.Src != null && !slot.Src.Enabled) continue;
                try { slot.Fx.Process(_l, _r, frames); } catch { }
            }

            // 4. gain de sortie + limiteur doux (le même que le mixeur du séquenceur, pour que ça « sonne »
            //    pareil) + crête pour le vumètre.
            double og = OutputGain;
            float peak = _outPeak;
            for (int i = 0; i < frames; i++)
            {
                float l = (float)AudioFormat.SoftClip(_l[i] * og);
                float r = (float)AudioFormat.SoftClip(_r[i] * og);
                _l[i] = l; _r[i] = r;
                float a = Math.Abs(l) > Math.Abs(r) ? Math.Abs(l) : Math.Abs(r);
                if (a > peak) peak = a;
            }
            _outPeak = peak;
        }

        void EnsureScratch(int frames)
        {
            if (_l != null && _l.Length >= frames) return;
            int n = Math.Max(frames, ChunkFrames);
            _l = new float[n]; _r = new float[n];
            _instL = new float[n]; _instR = new float[n];
        }

        static void WriteFloat(byte[] buffer, int offset, float value)
        {
            // BitConverter.GetBytes allouerait à chaque échantillon : on écrit les 4 octets à la main.
            int bits = BitConverter.SingleToInt32Bits(value);
            buffer[offset] = (byte)bits;
            buffer[offset + 1] = (byte)(bits >> 8);
            buffer[offset + 2] = (byte)(bits >> 16);
            buffer[offset + 3] = (byte)(bits >> 24);
        }

        public void Dispose()
        {
            Stop();
            var inst = _instrument; _instrument = null;
            try { inst?.Dispose(); } catch { }
        }

        /// <summary>Provider tiré par le backend. Le format est fixé au démarrage et ne change plus.</summary>
        sealed class LiveWaveProvider : NAudio.Wave.IWaveProvider
        {
            readonly LiveEngine _engine;
            public WaveFormat WaveFormat { get; }
            public LiveWaveProvider(LiveEngine engine, WaveFormat format) { _engine = engine; WaveFormat = format; }
            public int Read(byte[] buffer, int offset, int count)
            {
                try { return _engine.RenderInto(buffer, offset, count); }
                catch { Array.Clear(buffer, offset, count); return count; }
            }
        }

        /// <summary>
        /// Tampon circulaire stéréo entre le thread de capture et le thread de rendu. Verrou simple plutôt
        /// que sans-verrou : les deux sections critiques se comptent en dizaines de nanosecondes et la
        /// lisibilité prime. En cas de débordement (le rendu ne consomme pas assez vite) on JETTE LE PLUS
        /// ANCIEN : mieux vaut un micro-clic que voir la latence grandir indéfiniment.
        /// </summary>
        sealed class InputRing
        {
            readonly object _lock = new object();
            readonly float[] _l, _r;
            readonly int _cap, _prefill, _maxFill;
            int _read, _write, _count;
            bool _primed;

            public InputRing(int capacityFrames, int prefillFrames)
            {
                _cap = Math.Max(1024, capacityFrames);
                _prefill = Math.Max(0, Math.Min(prefillFrames, _cap / 4));
                // Plafond de remplissage : si la capture produit durablement plus vite que le rendu ne
                // consomme, on jette l'excédent au lieu de laisser la latence enfler jusqu'à la seconde.
                _maxFill = Math.Max(_prefill * 4, _cap / 2);
                _primed = _prefill == 0;
                _l = new float[_cap]; _r = new float[_cap];
            }

            public void Write(float l, float r)
            {
                lock (_lock)
                {
                    _l[_write] = l; _r[_write] = r;
                    _write = (_write + 1) % _cap;
                    if (_count == _cap) _read = (_read + 1) % _cap; else _count++;
                    while (_count > _maxFill) { _read = (_read + 1) % _cap; _count--; }
                }
            }

            /// <summary><paramref name="starved"/> distingue « le coussin n'est pas encore constitué »
            /// (démarrage normal, silence attendu) d'un vrai décrochage en cours de route.</summary>
            public int Read(float[] dstL, float[] dstR, int frames, out bool starved)
            {
                lock (_lock)
                {
                    starved = false;
                    if (!_primed)
                    {
                        if (_count < _prefill) return 0;
                        _primed = true;
                    }
                    int n = Math.Min(frames, _count);
                    for (int i = 0; i < n; i++)
                    {
                        dstL[i] = _l[_read]; dstR[i] = _r[_read];
                        _read = (_read + 1) % _cap;
                    }
                    _count -= n;
                    if (n < frames) { _primed = _prefill == 0; starved = true; }
                    return n;
                }
            }
        }
    }
}
