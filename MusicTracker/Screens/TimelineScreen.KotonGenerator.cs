using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
            KotonHost.NotifyDurationChanged = dur => { if (s_activeKotonHost == this) this.KotonHost_DurationChanged(dur); };
            KotonHost.CurrentContext = () => (s_activeKotonHost == this)
                ? KotonGeneratorRuntime.ContextFor(this.project, 0)
                : null;
            // Tête de lecture AUDIBLE (échantillons consommés par le device) — référence exacte
            // des plugins de visualisation temps réel : encaisse la latence du device, la pause,
            // le départ au curseur et la boucle sans qu'ils aient à re-scheduler quoi que ce soit.
            KotonHost.PlayheadBeat = () => (s_activeKotonHost == this) ? this.KotonHost_PlayheadBeat() : (double?)null;
        }

        /// <summary>Position audible en beats absolus, ou null si le device audio ne tourne pas
        /// (arrêt / pause / buffer encore en prime → l'animation se fige au lieu de dériver).</summary>
        double? KotonHost_PlayheadBeat()
            => (playWaveOut != null && player != null && playBuffer != null) ? PlayedBeat() : (double?)null;

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
                KotonHost.NotifyDurationChanged = null;
                KotonHost.CurrentContext = null;
                KotonHost.PlayheadBeat = null;
            }
        }

        /// <summary>Handler du callback <see cref="KotonHost.NotifyDurationChanged"/> : propage la
        /// nouvelle durée du plugin vers le module timeline (source de vérité pour la longueur du
        /// bloc — sinon EnsureInstance re-écrase la durée à chaque instanciation). Le Render()
        /// re-calcule la vignette immédiatement.</summary>
        void KotonHost_DurationChanged(double newDuration)
        {
            if (selectedItem?.Module is KotonGeneratorModule kg)
            {
                double clamped = newDuration < 0.25 ? 0.25 : newDuration;
                if (Math.Abs(kg.DurationBeats - clamped) > 1e-6)
                {
                    kg.DurationBeats = clamped;
                    Render();
                }
            }
        }

        /// <summary>Notifie l'éditeur Koton actuellement ouvert (s'il implémente <see cref="IKotonEditor"/>)
        /// qu'un aspect du contexte projet a changé (métrique, tonalité, tempo). L'éditeur peut alors
        /// adapter son UI — typiquement un arpégiateur qui repeuple son combo "notes par temps" quand
        /// on bascule binaire ↔ ternaire. Sans-effet si aucun éditeur Koton n'est ouvert.</summary>
        internal void NotifyKotonEditorContextChanged()
        {
            try
            {
                if (editorHost?.Content is FrameworkElement fe)
                {
                    // L'éditeur d'un plugin Koton peut être imbriqué (BuildKotonGeneratorEditor met un
                    // DockPanel qui contient le UserControl du plugin) — on cherche récursivement.
                    var koton = FindKotonEditor(fe);
                    if (koton != null)
                    {
                        var ctx = KotonGeneratorRuntime.ContextFor(project, 0);
                        koton.OnContextUpdated(ctx);
                    }
                }
            }
            catch { /* best-effort */ }
        }

        static IKotonEditor FindKotonEditor(DependencyObject root)
        {
            if (root is IKotonEditor e) return e;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                var found = FindKotonEditor(child);
                if (found != null) return found;
            }
            return null;
        }

        // ------------------------------- Menu Insérer ▸ Générateur Koton --------------------------------
        //
        // Le sous-menu est peuplé via ItemsSource. WPF affiche la flèche de sous-menu tant que la
        // source contient au moins un item (l'entrée Rescan garantit ça, donc le submenu s'ouvre
        // toujours et SubmenuOpened se déclenche). On rafraîchit à 2 moments :
        //  - Au Loaded initial (piste par défaut).
        //  - À chaque OUVERTURE du sous-menu (SubmenuOpened) — attrape tous les changements de piste
        //    sélectionnée, y compris ceux passés par des chemins qui n'appellent pas SelectTrack
        //    (ajout de piste, chargement de projet, etc.). Coût = négligeable, à l'ouverture user.

        void miKotonGenerator_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            RefreshKotonGeneratorMenu();
        }

        /// <summary>Reconstruit la liste des items du sous-menu « Générateur Koton » selon la piste
        /// sélectionnée (filtrage par type). Appelée au chargement de la TimelineScreen et à chaque
        /// changement de <c>selectedTrack</c>. Sans-effet si le contrôle XAML n'est pas encore chargé.
        ///
        /// **UX** : un seul niveau de sous-menu (pas de sous-groupes par type), avec les générateurs
        /// À PLAT triés par nom. Le filtrage garantit la consistance : sur une piste instrument, on
        /// n'affiche QUE les générateurs mélodiques (Melody + Bass) et d'accords ; sur une piste
        /// batterie, QUE les générateurs de drum (Drum + Percussion) et d'accords.</summary>
        internal void RefreshKotonGeneratorMenu()
        {
            if (miKotonGenerator == null) return;

            var items = new List<object>();
            var gens = KotonPluginRegistry.Generators;
            var allowed = AllowedGeneratorTypes(selectedTrack);

            // Un seul niveau : les générateurs autorisés directement, triés par nom.
            var visible = gens.Where(g => allowed.Contains(g.Type))
                              .OrderBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                              .ToList();

            foreach (var g in visible)
            {
                // Tag = Id + handler nommé plutôt que closure — évite l'exception HotReload
                // "Attempted to invoke a deleted lambda" quand VS patche le code pendant que le
                // MenuItem vit encore dans ItemsSource. Un handler d'instance est ré-résolu à chaque
                // clic, pas gelé au moment de la construction.
                var it = new MenuItem
                {
                    Header = g.DisplayName,
                    ToolTip = g.Vendor + (string.IsNullOrEmpty(g.Version) ? "" : " · " + g.Version),
                    Tag = g.Id,
                };
                it.Click += KotonGenMenuItem_Click;
                items.Add(it);
            }

            if (visible.Count == 0)
            {
                items.Add(new MenuItem { Header = Loc.T("KotonNoGeneratorsFound"), IsEnabled = false });
            }
            items.Add(new Separator());
            var rescan = new MenuItem { Header = Loc.T("KotonRescan") };
            rescan.Click += KotonGenRescan_Click;
            items.Add(rescan);

            // ItemsSource plutôt que Items.Add — garantit que WPF traite le MenuItem comme un submenu
            // dès qu'il y a au moins un item.
            miKotonGenerator.ItemsSource = items;
        }

        void KotonGenMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is string id)
                InsertKotonGenerator(id);
        }

        void KotonGenRescan_Click(object sender, RoutedEventArgs e)
        {
            KotonPluginRegistry.Rescan();
            RefreshKotonGeneratorMenu();
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

        // Filtrage par type de piste — cohérence stricte :
        //  - Instrument mélodique  → Melody (+ Bass, mélodique par nature) + Chord
        //  - Batterie              → Drum (+ Percussion, drum par nature) + Chord
        //  - Piste d'accords       → Chord uniquement
        //  - Sélection vide        → aucun (menu vide → invite à sélectionner une piste)
        //
        // Note : les générateurs de type Chord apparaissent dans TOUS les cas (sauf sélection vide) —
        // l'insertion route automatiquement vers la piste Accords permanente du projet, quelle que soit
        // la piste sélectionnée. Cf. <see cref="InsertKotonGenerator"/>.
        //
        // La catégorie "Other" est volontairement écartée pour garder la cohérence.
        static HashSet<KotonGeneratorType> AllowedGeneratorTypes(TimelineTrack track)
        {
            var result = new HashSet<KotonGeneratorType>();
            if (track == null) return result;
            switch (track.Type)
            {
                case TimelineTrackType.Instrument:
                    result.Add(KotonGeneratorType.Melody);
                    result.Add(KotonGeneratorType.Bass);
                    result.Add(KotonGeneratorType.Chord);
                    break;
                case TimelineTrackType.Drum:
                    result.Add(KotonGeneratorType.Drum);
                    result.Add(KotonGeneratorType.Percussion);
                    result.Add(KotonGeneratorType.Chord);
                    break;
                case TimelineTrackType.Chord:
                    result.Add(KotonGeneratorType.Chord);
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
            var genType = probe.GeneratorType;
            try { probe.Dispose(); } catch { }

            // Routing par type : un générateur de type Chord va systématiquement sur la piste Accords
            // permanente (peu importe la piste sélectionnée) — l'utilisateur peut ainsi lancer une
            // génération d'accords sans avoir à cliquer d'abord sur la piste Accords. Les autres
            // types (Melody/Bass/Drum/Percussion) restent sur la piste sélectionnée.
            var targetTrack = selectedTrack;
            if (genType == KotonGeneratorType.Chord)
            {
                var chordTrack = project?.Tracks?.FirstOrDefault(t => t != null && t.Type == TimelineTrackType.Chord);
                if (chordTrack != null) targetTrack = chordTrack;
            }

            string pre = BeginUndo();
            var module = new KotonGeneratorModule
            {
                GeneratorId = generatorId,
                DurationBeats = defaultDuration,
                GeneratorState = initialState,
            };
            var item = new TimelineItem { Module = module };
            TimelineHelper.PlaceAtCursor(targetTrack, item, defaultDuration, startBeat, project.RiffById);
            SelectItem(targetTrack, item);
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
                    // Même barre de presets que dans la fenêtre d'éditeur des instruments/effets — un
                    // générateur se règle autant qu'un synthé, il mérite le même magasin de réglages.
                    var dock = new DockPanel();
                    var bar = new Controls.KotonPresetBar(inst);
                    DockPanel.SetDock(bar, Dock.Top);
                    dock.Children.Add(bar);
                    dock.Children.Add(uc);
                    host.Child = dock;
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
