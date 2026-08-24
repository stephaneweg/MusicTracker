using System;
using System.Threading;
using KotonStudio.Library;
using MusicTracker.Engine.Timeline.Effects;
using NAudio.Wave;

namespace MusicTracker.Screens
{
    /// <summary>
    /// Écoute d'un instrument Koton au SURVOL d'un item de menu : une note tenue démarre quand le pointeur
    /// s'arrête sur le nom du plugin, et s'arrête dès qu'il en sort ou qu'on clique. De quoi parcourir
    /// quarante instruments à l'oreille sans en poser un seul sur une piste.
    ///
    /// **Une seule écoute à la fois** : survoler un autre item coupe la précédente. Un compteur de
    /// génération protège l'ordre — instancier un plugin peut prendre quelques dizaines de millisecondes
    /// (tables d'ondes, résonateurs), assez pour qu'un balayage rapide de la liste rende les réponses dans
    /// le désordre ; une session qui n'est plus la dernière demandée se jette au lieu de se brancher.
    ///
    /// **Instance jetable** : l'écoute crée SA propre instance du plugin, jamais celle du cache d'une
    /// piste — survoler un menu ne doit rien changer à ce qui est posé dans le morceau.
    ///
    /// **Sortie audio séparée** : un <see cref="WaveOutEvent"/> à part, donc l'écoute fonctionne aussi
    /// pendant la lecture de la timeline (on entend les deux, ce qui est le comportement attendu quand on
    /// cherche un son qui va avec le morceau).
    /// </summary>
    internal static class KotonInstrumentAudition
    {
        /// <summary>La3 = 220 Hz. Assez grave pour qu'un pad se déploie, assez aigu pour qu'un
        /// scintillement s'entende — et dans la tessiture de tous les modèles physiques.</summary>
        public const int DefaultNote = 57;

        const int SampleRate = 44100;
        const int Block = 512;
        const double FadeSeconds = 0.12;     // fondu de sortie : sans lui, couper net claque
        const double MaxSeconds = 20.0;      // garde-fou si un MouseLeave se perd (menu fermé brutalement)

        static readonly object _lock = new object();
        static Session _current;
        static int _generation;

        /// <summary>Démarre (ou remplace) l'écoute de <paramref name="pluginId"/>. Retourne
        /// immédiatement : l'instanciation et l'ouverture du périphérique se font en tâche de fond, pour
        /// qu'un menu ne se fige jamais sous le pointeur.</summary>
        public static void Start(string pluginId, int midiNote = DefaultNote)
        {
            if (string.IsNullOrEmpty(pluginId)) return;

            int gen;
            lock (_lock)
            {
                gen = ++_generation;
                StopLocked();
            }

            var th = new Thread(() =>
            {
                IKotonInstrument plugin = null;
                try
                {
                    plugin = KotonPluginRegistry.InstantiateInstrument(pluginId);
                    if (plugin == null) return;
                    plugin.Prepare(SampleRate, Block);

                    var session = new Session(plugin);
                    lock (_lock)
                    {
                        // Un autre survol a eu lieu pendant qu'on chargeait : cette écoute-ci est déjà périmée.
                        if (gen != _generation) { session.Dispose(); return; }
                        _current = session;
                    }
                    session.Play(midiNote);
                    plugin = null;   // possédé par la session désormais
                }
                catch (Exception ex)
                {
                    try { KotonHost.ReportException?.Invoke(ex, "KotonInstrumentAudition"); } catch { }
                }
                finally
                {
                    if (plugin != null) { try { plugin.Dispose(); } catch { } }
                }
            })
            { IsBackground = true, Name = "KotonAudition" };
            th.Start();
        }

        /// <summary>Coupe l'écoute en cours (fondu court puis libération). Sans effet si rien ne joue.</summary>
        public static void Stop()
        {
            lock (_lock) { _generation++; StopLocked(); }
        }

        /// <summary>
        /// Branche l'écoute au survol sur un item de menu : le pointeur s'arrête dessus, la note part ;
        /// il en sort ou on clique, elle s'arrête.
        ///
        /// Le délai avant démarrage n'est pas cosmétique : descendre une liste de quarante instruments
        /// traverse tous les items, et sans lui chaque passage instancierait un plugin pour l'abandonner
        /// aussitôt. À 220 ms, seul l'item sur lequel on s'arrête vraiment se fait entendre.
        /// </summary>
        public static void Attach(System.Windows.Controls.MenuItem item, string pluginId, int midiNote = DefaultNote)
        {
            if (item == null || string.IsNullOrEmpty(pluginId)) return;

            System.Windows.Threading.DispatcherTimer timer = null;

            item.MouseEnter += (s, e) =>
            {
                timer?.Stop();
                timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(220),
                };
                timer.Tick += (a, b) => { timer.Stop(); Start(pluginId, midiNote); };
                timer.Start();
            };
            item.MouseLeave += (s, e) => { timer?.Stop(); Stop(); };
            // Clic = l'instrument est choisi : la note tenue n'a plus lieu d'être, et le menu se ferme
            // sans forcément produire un MouseLeave.
            item.Click += (s, e) => { timer?.Stop(); Stop(); };
            item.Unloaded += (s, e) => { timer?.Stop(); Stop(); };
        }

        static void StopLocked()
        {
            var s = _current;
            _current = null;
            s?.BeginRelease();
        }

        /// <summary>Une écoute : le plugin, sa sortie audio, et l'état du fondu de sortie. Le rendu vit sur
        /// le thread de NAudio, l'arrêt est demandé depuis l'UI — d'où les champs volatiles plutôt qu'un
        /// verrou (le thread audio ne doit jamais attendre l'UI).</summary>
        sealed class Session : IWaveProvider, IDisposable
        {
            readonly IKotonInstrument _plugin;
            readonly float[] _left = new float[Block];
            readonly float[] _right = new float[Block];
            readonly float[] _inter = new float[Block * 2];
            WaveOutEvent _wave;
            volatile bool _releasing;
            volatile bool _dead;
            double _fade = 1.0;
            long _frames;
            int _note = DefaultNote;

            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

            public Session(IKotonInstrument plugin) { _plugin = plugin; }

            public void Play(int midiNote)
            {
                try
                {
                    _note = midiNote;
                    _plugin.NoteOn(midiNote, 100);
                    // Latence courte : l'écoute doit répondre au survol, pas se faire attendre. 80 ms tient
                    // largement sur un rendu de synthèse qui ne touche ni disque ni réseau.
                    _wave = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 3 };
                    _wave.Init(this);
                    _wave.Play();
                }
                catch (Exception ex)
                {
                    try { KotonHost.ReportException?.Invoke(ex, "KotonInstrumentAudition"); } catch { }
                    Dispose();
                }
            }

            /// <summary>Demande l'arrêt : note-off, fondu, puis libération sur un thread à part (fermer un
            /// périphérique WaveOut bloque le temps que le pilote rende la main — pas sur le thread UI).</summary>
            public void BeginRelease()
            {
                if (_releasing) return;
                _releasing = true;
                try { _plugin.NoteOff(_note); } catch { }
                var th = new Thread(() =>
                {
                    // Laisse le fondu se dérouler dans Read() avant de couper le périphérique.
                    Thread.Sleep((int)(FadeSeconds * 1000) + 60);
                    Dispose();
                })
                { IsBackground = true, Name = "KotonAuditionStop" };
                th.Start();
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                int frames = count / 8;                 // 2 canaux × 4 octets
                if (frames <= 0 || _dead) return 0;
                int done = 0;
                while (done < frames)
                {
                    int n = Math.Min(Block, frames - done);
                    var l = new Span<float>(_left, 0, n);
                    var r = new Span<float>(_right, 0, n);
                    try { _plugin.Render(l, r); }
                    catch { l.Clear(); r.Clear(); _dead = true; }

                    for (int i = 0; i < n; i++)
                    {
                        if (_releasing && _fade > 0) _fade -= 1.0 / (FadeSeconds * SampleRate);
                        float g = _fade > 0 ? (float)_fade : 0f;
                        _inter[i * 2] = _left[i] * g;
                        _inter[i * 2 + 1] = _right[i] * g;
                    }
                    Buffer.BlockCopy(_inter, 0, buffer, offset + done * 8, n * 8);
                    done += n;
                    _frames += n;
                }
                // Le garde-fou de durée ne coupe pas le flux (NAudio arrêterait la lecture d'un coup) :
                // il déclenche le même fondu que le MouseLeave, donc la sortie reste propre.
                if (!_releasing && _frames > (long)(MaxSeconds * SampleRate)) BeginRelease();
                return count;
            }

            public void Dispose()
            {
                if (_dead && _wave == null) return;
                _dead = true;
                var w = _wave; _wave = null;
                try { w?.Stop(); } catch { }
                try { w?.Dispose(); } catch { }
                try { _plugin?.Dispose(); } catch { }
            }
        }
    }
}
