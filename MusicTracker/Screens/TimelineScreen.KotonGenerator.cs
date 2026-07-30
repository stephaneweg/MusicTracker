using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KotonStudio.Library;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Timeline;
using MusicTracker.Engine.Timeline.Effects;
using MusicTracker.Localization;

namespace MusicTracker.Screens
{
    /// <summary>
    /// Partie de <see cref="TimelineScreen"/> dédiée aux GÉNÉRATEURS Koton natifs (interface
    /// <see cref="IKotonGenerator"/>) — pendant de <see cref="TimelineScreen.Vsti.cs"/> pour les
    /// INSTRUMENTS. Isolé dans un fichier séparé pour ne pas alourdir le monstre principal.
    ///
    /// **Ce que ça fait** :
    /// - Peuple le sous-menu « Insérer ▸ Générateur Koton » à l'ouverture, avec les générateurs
    ///   filtrés par type et groupés par catégorie (Melody, Drum, Chord, Bass, Percussion, Other).
    /// - Insère un <see cref="KotonGeneratorModule"/> sur la piste cible (par type — batterie
    ///   ne propose que Drum/Percussion, mélodique ne propose que Melody/Bass, piste d'accords ne
    ///   propose que Chord).
    /// - Construit l'éditeur bottom-panel pour un module sélectionné (le UserControl fourni par
    ///   le plugin + une barre Preview/Stop qui invoque <see cref="KotonHost.PreviewNotes"/>).
    /// - Câble <see cref="KotonHost"/> à l'ouverture du projet (GetChordAt / PreviewNotes / StopPreview)
    ///   et décâble à la fermeture ; assure qu'une preview stoppe si la lecture réelle démarre.
    /// </summary>
    public partial class TimelineScreen
    {
        // ------------------------------- Câblage KotonHost --------------------------------
        //
        // Toutes les callbacks sont enregistrées quand ce TimelineScreen devient actif (ouverture d'onglet)
        // et retirées quand il ferme. Un seul TimelineScreen à la fois se câble (le plus récemment actif) —
        // KotonHost est statique, donc écraser le callback précédent est cohérent avec le comportement
        // "le curseur clavier est sur l'onglet actif".
        //
        // Preview : une petite CTS pilote un thread de dispatch qui envoie note-on / note-off dans le temps.
        // Un nouveau Preview annule l'ancien ; StopPreview idem. Le renderer audio (LookaheadBuffer +
        // TimelinePlayer) n'est pas touché — la preview passe DIRECTEMENT par le synthé de la piste actuelle,
        // via la couche IVstInstrumentHost si c'est un VSTi/Koton natif, sinon via une petite fanfare de
        // note-on/off émise sur le prochain buffer du player s'il tourne, ou (idle) via un synthé jetable
        // instancié à la volée sur MeltySynth.
        //
        // v1 : simple et robuste. On instancie une preview stack IDLE-only (le player réel n'est pas
        // exigé pour Preview) — un synth MeltySynth jetable (car allouer/désallouer est cheap et le
        // pipeline standard est protégé) OU le Koton/VSTi de la piste sélectionnée.

        // La piste dont l'éditeur générateur est OUVERT — celle qui reçoit la Preview.
        TimelineTrack kotonPreviewTrack;
        CancellationTokenSource kotonPreviewCts;
        readonly object kotonPreviewLock = new object();

        // Wrappers statiques → instance : KotonHost est statique, on garde une référence sur
        // l'instance active pour router les callbacks. Un onglet fermé ne doit pas répondre.
        static TimelineScreen s_activeKotonHost;

        /// <summary>Câble <see cref="KotonHost"/> sur CE TimelineScreen — remplace le câblage précédent.
        /// Appelé quand l'onglet devient actif (constructeur / navigation shell).</summary>
        internal void HookKotonHost()
        {
            s_activeKotonHost = this;
            KotonHost.GetChordAt = beat => (s_activeKotonHost == this)
                ? KotonChordResolver.GetChordAt(this.project, beat)
                : (KotonChord?)null;
            KotonHost.PreviewNotes = notes => { if (s_activeKotonHost == this) this.KotonHost_PreviewNotes(notes); };
            KotonHost.StopPreview = () => { if (s_activeKotonHost == this) this.KotonHost_StopPreview(); };
        }

        /// <summary>Décâble <see cref="KotonHost"/> — appelé à la fermeture de l'onglet.</summary>
        internal void UnhookKotonHost()
        {
            KotonHost_StopPreview();
            if (s_activeKotonHost == this)
            {
                s_activeKotonHost = null;
                KotonHost.GetChordAt = null;
                KotonHost.PreviewNotes = null;
                KotonHost.StopPreview = null;
            }
        }

        // ------------------------------- Menu Insérer ▸ Générateur Koton --------------------------------

        void miKotonGenerator_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            if (mi == null) return;
            // Rebuild dynamique : le sous-menu se remplit à l'ouverture, filtré selon la piste
            // sélectionnée. Ré-ouvrir = re-scan = capte un nouveau plugin déposé pendant la session.
            mi.Items.Clear();
            var gens = KotonPluginRegistry.GetGenerators();
            var allowed = AllowedGeneratorTypes(selectedTrack);
            bool anyAdded = false;

            // Un sous-menu par KotonGeneratorType (Melody / Drum / Chord / Bass / Percussion / Other),
            // dans l'ordre d'utilisation courante. Un type sans plugin dans la catégorie n'apparaît pas.
            foreach (var type in new[] {
                KotonGeneratorType.Melody, KotonGeneratorType.Bass, KotonGeneratorType.Chord,
                KotonGeneratorType.Drum, KotonGeneratorType.Percussion, KotonGeneratorType.Other })
            {
                if (!allowed.Contains(type)) continue;
                var inGroup = gens.Where(g => g.Type == type).ToList();
                if (inGroup.Count == 0) continue;
                var typeItem = new MenuItem { Header = LocForGenType(type) };
                foreach (var g in inGroup)
                {
                    var it = new MenuItem { Header = g.DisplayName, ToolTip = g.Vendor + (string.IsNullOrEmpty(g.Version) ? "" : " · " + g.Version) };
                    string id = g.Id;
                    it.Click += (s2, e2) => InsertKotonGenerator(id);
                    typeItem.Items.Add(it);
                }
                mi.Items.Add(typeItem);
                anyAdded = true;
            }

            if (!anyAdded)
            {
                var empty = new MenuItem { Header = Loc.T("KotonNoGeneratorsFound"), IsEnabled = false };
                mi.Items.Add(empty);
            }
            mi.Items.Add(new Separator());
            var rescan = new MenuItem { Header = Loc.T("KotonRescan") };
            rescan.Click += (s2, e2) => { KotonPluginRegistry.Rescan(); };
            mi.Items.Add(rescan);
        }

        static string LocForGenType(KotonGeneratorType t)
        {
            switch (t)
            {
                case KotonGeneratorType.Melody: return Loc.T("KotonGenTypeMelody");
                case KotonGeneratorType.Drum: return Loc.T("KotonGenTypeDrum");
                case KotonGeneratorType.Chord: return Loc.T("KotonGenTypeChord");
                case KotonGeneratorType.Bass: return Loc.T("KotonGenTypeBass");
                case KotonGeneratorType.Percussion: return Loc.T("KotonGenTypePercussion");
                default: return Loc.T("KotonGenTypeOther");
            }
        }

        // Filtrage par type de piste, comme spécifié :
        //  - Instrument mélodique  → Melody, Bass, Chord, Other
        //  - Batterie              → Drum, Percussion, Other
        //  - Piste d'accords       → Chord, Other
        static HashSet<KotonGeneratorType> AllowedGeneratorTypes(TimelineTrack track)
        {
            var result = new HashSet<KotonGeneratorType>();
            if (track == null)
            {
                // Rien de sélectionné : on montre tout — l'utilisateur choisit, l'insertion enverra
                // vers la bonne piste ou l'obligera à sélectionner (cas Bass/Melody sans piste).
                foreach (var v in Enum.GetValues(typeof(KotonGeneratorType))) result.Add((KotonGeneratorType)v);
                return result;
            }
            switch (track.Type)
            {
                case TimelineTrackType.Instrument:
                    result.Add(KotonGeneratorType.Melody);
                    result.Add(KotonGeneratorType.Bass);
                    result.Add(KotonGeneratorType.Chord);
                    result.Add(KotonGeneratorType.Other);
                    break;
                case TimelineTrackType.Drum:
                    result.Add(KotonGeneratorType.Drum);
                    result.Add(KotonGeneratorType.Percussion);
                    result.Add(KotonGeneratorType.Other);
                    break;
                case TimelineTrackType.Chord:
                    result.Add(KotonGeneratorType.Chord);
                    result.Add(KotonGeneratorType.Other);
                    break;
            }
            return result;
        }

        void InsertKotonGenerator(string generatorId)
        {
            if (selectedTrack == null) { MessageBox.Show(Loc.T("SelectionneDAbordUnePiste")); return; }
            // Instancie une fois pour sonder l'affichage et la durée par défaut du plugin — puis pousse
            // le module. Le player ré-instanciera pour le rendu audio (chaque module a SA propre instance)
            // via KotonGeneratorRuntime.EnsureInstance. Une exception au ctor = message + abandon.
            var probe = KotonPluginRegistry.InstantiateGenerator(generatorId);
            if (probe == null)
            {
                MessageBox.Show(Window.GetWindow(this),
                    string.Format(Loc.T("KotonPluginMissing"), generatorId),
                    Loc.T("KotonGenerator"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            double defaultDuration = 4;
            try { defaultDuration = Math.Max(0.25, probe.DurationBeats); } catch { }
            byte[] initialState = null;
            try { initialState = probe.SaveState(); } catch { }
            try { probe.Dispose(); } catch { }

            string pre = BeginUndo();
            var module = new KotonGeneratorModule
            {
                GeneratorId = generatorId,
                DurationBeats = defaultDuration,
                GeneratorState = initialState,
            };
            var item = new TimelineItem { Module = module };
            TimelineHelper.PlaceAtCursor(selectedTrack, item, defaultDuration, startBeat, project.RiffById);
            SelectItem(selectedTrack, item);
            CommitUndo(pre, "insert:" + Id(item));
            Render();
        }

        // ------------------------------- Éditeur bottom panel --------------------------------

        /// <summary>Construit l'éditeur d'un <see cref="KotonGeneratorModule"/> pour le panneau du bas.
        /// Un en-tête (nom du plugin + Preview / Stop) + l'UserControl WPF fourni par le plugin, ou un
        /// message si le plugin est absent / n'a pas d'éditeur.</summary>
        internal UIElement BuildKotonGeneratorEditor(TimelineTrack track, TimelineItem item, KotonGeneratorModule module)
        {
            var stack = new DockPanel { LastChildFill = true };

            // Barre supérieure : nom du plugin + boutons Preview / Stop. Preview joue les notes du
            // bloc entier via le synthé de la piste courante (kotonPreviewTrack posé au SelectItem).
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 6, 8, 6),
            };
            DockPanel.SetDock(header, Dock.Top);

            var inst = KotonGeneratorRuntime.EnsureInstance(module);
            string pluginName = inst?.DisplayName ?? module.GeneratorId ?? "?";
            header.Children.Add(new TextBlock
            {
                Text = pluginName,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            });

            var btnPreview = new Button
            {
                Content = Loc.T("KotonPreviewButton"),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            btnPreview.Click += (s, e) =>
            {
                // Contexte piste : la piste sélectionnée = la piste où l'éditeur est ouvert (SelectItem
                // pose selectedTrack). On la mémorise pour que PreviewNotes route correctement.
                kotonPreviewTrack = track;
                var vinst = KotonGeneratorRuntime.EnsureInstance(module);
                if (vinst == null) return;
                double dur = Math.Max(0.25, module.DurationBeats);
                IEnumerable<KotonGeneratedNote> notes;
                try { notes = vinst.RenderNotes(0, dur, KotonGeneratorRuntime.ContextFor(project))?.ToList(); }
                catch { return; }
                if (notes == null) return;
                KotonHost_PreviewNotes(notes);
            };
            header.Children.Add(btnPreview);

            var btnStop = new Button
            {
                Content = Loc.T("KotonStopButton"),
                Padding = new Thickness(8, 2, 8, 2),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            btnStop.Click += (s, e) => KotonHost_StopPreview();
            header.Children.Add(btnStop);

            stack.Children.Add(header);

            // Contenu principal : l'UserControl fourni par le plugin. Absent = message diagnostique.
            var host = new Border
            {
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
            };
            if (inst == null)
            {
                host.Child = new TextBlock
                {
                    Text = string.Format(Loc.T("KotonPluginMissing"), module.GeneratorId ?? "?"),
                    Margin = new Thickness(24),
                    TextWrapping = TextWrapping.Wrap,
                };
            }
            else if (!inst.HasEditor)
            {
                host.Child = new TextBlock
                {
                    Text = "Ce plugin ne fournit pas d'éditeur.",
                    Margin = new Thickness(24),
                };
            }
            else
            {
                UserControl uc = null;
                try { uc = inst.CreateEditor(); } catch { }
                if (uc != null)
                {
                    host.Child = uc;
                }
                else
                {
                    host.Child = new TextBlock
                    {
                        Text = "L'éditeur du plugin est vide.",
                        Margin = new Thickness(24),
                    };
                }
            }
            stack.Children.Add(host);

            // À la fermeture de l'éditeur (autre item sélectionné / onglet fermé), capture l'état
            // pour qu'il persiste dans le projet. Le plugin reste vivant (RuntimeInstance sur le
            // module) — le player le réutilise, et un futur clic ré-ouvrira SA MÊME instance.
            stack.Unloaded += (s, e) =>
            {
                try
                {
                    var v2 = module.RuntimeInstance;
                    if (v2 != null) module.GeneratorState = v2.SaveState();
                }
                catch { }
                // Preview coupée quand on quitte l'éditeur (évite qu'elle continue de jouer alors
                // qu'on a switché sur un autre bloc).
                KotonHost_StopPreview();
            };

            // Poser le contexte piste courante — utile si le user clique tout de suite sur Preview
            // sans avoir touché à autre chose.
            kotonPreviewTrack = track;
            return stack;
        }

        // ------------------------------- Preview dispatch --------------------------------

        void KotonHost_PreviewNotes(IEnumerable<KotonGeneratedNote> notes)
        {
            // Toujours annuler la preview précédente avant d'en démarrer une nouvelle. Un utilisateur
            // qui clique Preview 3 fois de suite n'accumule pas 3 threads (DAW-friendly).
            KotonHost_StopPreview();

            var track = kotonPreviewTrack;
            if (track == null || notes == null) return;
            var list = notes.ToList();
            if (list.Count == 0) return;

            var cts = new CancellationTokenSource();
            lock (kotonPreviewLock) kotonPreviewCts = cts;

            double bpm = Math.Max(30, project?.MainBpm ?? 120);
            double beatSeconds = 60.0 / bpm;

            // Résout la cible d'émission au moment du Preview. Dans un premier temps on utilise le
            // pipeline synth du player : PROBLÈME — si le player n'est pas démarré (idle), il n'y a
            // pas de synth prêt. Solution : passer par une routine légère qui NE DÉPEND PAS du
            // player — un synthé jetable si la piste utilise MeltySynth, ou une émission directe
            // sur l'IVstInstrumentHost si la piste a un VSTi ou un Koton natif.
            //
            // v1 SIMPLE : on utilise MeltySynth avec un canal dédié + un stream NAudio jetable
            // pour la sortie. Pour éviter la duplication de plomberie, on utilise le SoundFontObject
            // et un Synthesizer local, et on envoie directement à WaveOutEvent — c'est le même
            // pattern qu'un utilise l'AudioPreviewProvider historique.
            var previewer = new KotonPreviewPlayer(track, list, beatSeconds, cts.Token);
            previewer.Start();
        }

        void KotonHost_StopPreview()
        {
            CancellationTokenSource cts;
            lock (kotonPreviewLock) { cts = kotonPreviewCts; kotonPreviewCts = null; }
            try { cts?.Cancel(); } catch { }
        }

        // ------------------------------- Sérialisation --------------------------------

        /// <summary>Parcourt toutes les pistes et les items, et pour chaque
        /// <see cref="KotonGeneratorModule"/> avec une <see cref="KotonGeneratorModule.RuntimeInstance"/>
        /// vivante, rafraîchit le blob <see cref="KotonGeneratorModule.GeneratorState"/> depuis
        /// <see cref="IKotonPlugin.SaveState"/>. Appelée juste avant chaque Save.
        ///
        /// Sinon un slider bougé dans l'éditeur (qui mute l'instance vivante) serait perdu au save —
        /// l'instance vit dans le module runtime-only, le blob est ce qui persiste dans le .sq.</summary>
        internal void CaptureKotonGeneratorStates()
        {
            if (project?.Tracks == null) return;
            foreach (var t in project.Tracks)
            {
                if (t?.Items == null) continue;
                foreach (var it in t.Items)
                {
                    if (it?.Module is KotonGeneratorModule kg && kg.RuntimeInstance != null)
                    {
                        try
                        {
                            var blob = kg.RuntimeInstance.SaveState();
                            kg.GeneratorState = (blob == null || blob.Length == 0) ? null : blob;
                        }
                        catch { }
                    }
                }
            }
        }
    }
}
