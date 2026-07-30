using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MusicTracker.Dialogs;
using MusicTracker.Engine;
using MusicTracker.Engine.Timeline;
using MusicTracker.Engine.Timeline.Effects;
using MusicTracker.Localization;

namespace MusicTracker.Screens
{
    /// <summary>
    /// Partie de <see cref="TimelineScreen"/> dédiée à la sélection d'un instrument VSTi (VST2 en tant que
    /// SOURCE sonore, en substitution du synthé GM MeltySynth) sur une piste. Un fichier séparé pour ne pas
    /// alourdir le monstre <c>TimelineScreen.xaml.cs</c>.
    ///
    /// **UX** : un petit bouton dans l'en-tête de piste. Étiquette = "VSTi…" quand rien n'est chargé, sinon
    /// le nom du plugin (avec ⚠ si le fichier a bougé). Clic → menu contextuel : liste des plugins scannés
    /// (filtrés = ceux dont VstPluginFlags.IsSynth), + "Éditer le VSTi…" (ouvre la GUI native via
    /// <see cref="VstPluginWindow"/>), + "Retirer le VSTi" (revient au son GM MeltySynth), + "Re-scanner".
    ///
    /// **Prise d'effet** : la sélection d'un VSTi mute la partie MeltySynth de la piste dès le prochain
    /// <c>Start</c> de lecture (le player snapshotte les synths/vsti au démarrage — cohérent avec les
    /// inserts). Un snapshot d'annulation est capturé à chaque changement.
    ///
    /// **Éditeur** : instance <c>VstInstrument</c> dédiée à l'UI (indépendante de celle du renderer). L'état
    /// est capturé à la fermeture de la fenêtre et poussé dans <c>track.VstiStateBlob</c> — au prochain Start,
    /// le renderer relit ce blob. Comme pour les inserts VST (voir <c>MixerDialog.OpenEffectEditor</c>).
    /// </summary>
    public partial class TimelineScreen
    {
        Button BuildVstiButton(TimelineTrack track)
        {
            var btn = new Button
            {
                Margin = new Thickness(0, 3, 0, 0),
                FontSize = 11,
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                ToolTip = Loc.T("TrackInstrumentVsti"),
            };
            UpdateVstiButtonLabel(btn, track);
            btn.Click += (s, e) => ShowVstiMenu(btn, track);
            return btn;
        }

        static void UpdateVstiButtonLabel(Button btn, TimelineTrack track)
        {
            // Priorité d'affichage : Koton natif d'abord (le routage TimelinePlayer donne priorité au VSTi
            // si les deux sont posés, mais l'UI de sélection empêche cette situation — un choix nouveau
            // clear l'autre. L'ordre ici juste pour la robustesse en cas d'un .sq bidon).
            if (!string.IsNullOrEmpty(track.KotonInstrumentId))
            {
                var info = System.Linq.Enumerable.FirstOrDefault(
                    Engine.Timeline.Effects.KotonPluginRegistry.GetInstruments(),
                    p => string.Equals(p.Id, track.KotonInstrumentId, StringComparison.Ordinal));
                btn.FontStyle = FontStyles.Normal;
                btn.Opacity = 1.0;
                if (info == null)
                {
                    // Plugin id référencé mais absent du dossier plugins/ (fichier supprimé / non installé).
                    btn.Content = "⚠ " + track.KotonInstrumentId;
                    btn.ToolTip = string.Format(Loc.T("KotonPluginMissing"), track.KotonInstrumentId);
                }
                else
                {
                    btn.Content = "\U0001F3AB " + info.DisplayName;   // 🎫 (icône distincte du VSTi 🎹)
                    btn.ToolTip = info.DisplayName + " (" + info.Id + ")";
                }
                return;
            }
            if (string.IsNullOrEmpty(track.VstiPath))
            {
                btn.Content = Loc.T("TrackInstrumentVsti");
                btn.FontStyle = FontStyles.Italic;
                btn.Opacity = 0.75;
                return;
            }
            btn.FontStyle = FontStyles.Normal;
            btn.Opacity = 1.0;
            string name = System.IO.Path.GetFileNameWithoutExtension(track.VstiPath);
            // File OU Directory : un vieux .sq d'avant le fix scanner peut avoir un chemin de bundle-dossier.
            if (!File.Exists(track.VstiPath) && !Directory.Exists(track.VstiPath))
            {
                btn.Content = "⚠ " + name;
                btn.ToolTip = string.Format(Loc.T("VstiPluginMissing"), track.VstiPath);
            }
            else
            {
                btn.Content = "\U0001F3B9 " + name;   // 🎹
                btn.ToolTip = track.VstiPath;
            }
        }

        void ShowVstiMenu(FrameworkElement anchor, TimelineTrack track)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };

            // Runtime VC++ requis pour héberger un plugin — même vérif que pour les inserts VST du mixeur.
            // Sans le runtime, le premier VstPluginContext.Create jettera. On propose le téléchargement, mais
            // on laisse quand même le menu ouvert : l'utilisateur peut voir la liste des plugins.
            if (!VstRuntimeCheck.IsVcRedistInstalled())
            {
                var warn = new MenuItem { Header = "⚠ " + Loc.T("VstVcRedistRequired"), IsEnabled = false };
                menu.Items.Add(warn);
                menu.Items.Add(new Separator());
            }

            // Sous-menu de la liste des VSTi (peut être long → sous-menu, pas des items plats).
            var browse = new MenuItem { Header = Loc.T("TrackInstrumentVsti") };
            // Classification synchrone : la 1re fois qu'on ouvre ce menu, chaque plugin est chargé (~50-500 ms)
            // pour lire son flag IsSynth. Les fois suivantes = cache session, instantané. En attendant la
            // classification, l'UI est bloquée quelques secondes (acceptable en bêta ; un vrai spinner et un
            // scan en arrière-plan pourront être ajoutés).
            var vstis = VstPluginScanner.GetInstruments();
            if (vstis.Count == 0)
            {
                var empty = new MenuItem { Header = Loc.T("VstiNoInstrumentsFound"), IsEnabled = false };
                browse.Items.Add(empty);
            }
            else
            {
                foreach (var p in vstis)
                {
                    var it = new MenuItem { Header = p.DisplayName, IsCheckable = false };
                    if (!string.IsNullOrEmpty(track.VstiPath) &&
                        string.Equals(track.VstiPath, p.Path, StringComparison.OrdinalIgnoreCase))
                        it.Icon = new TextBlock { Text = "✓", FontWeight = FontWeights.Bold };
                    string path = p.Path;
                    it.Click += (s, e) => SelectVsti(track, path);
                    browse.Items.Add(it);
                }
            }
            browse.Items.Add(new Separator());
            var rescan = new MenuItem { Header = Loc.T("VstRescan") };
            rescan.Click += (s, e) => { VstPluginScanner.ForceRescan(); };
            browse.Items.Add(rescan);
            menu.Items.Add(browse);

            // Sous-menu des instruments KOTON NATIFS (framework KotonStudio.Library, scannés depuis
            // plugins/*.ksl). Alternative sûre au hosting VST natif : plugins C# purs, chargés par
            // reflexion, sans HWND ni runtime C++ à installer. Séparé du sous-menu VSTi (pas la même
            // techno derrière) mais dans le même menu de choix pour rester découvrable.
            menu.Items.Add(new Separator());
            var kotonBrowse = new MenuItem { Header = Loc.T("TrackInstrumentKoton") };
            var kotons = Engine.Timeline.Effects.KotonPluginRegistry.GetInstruments();
            if (kotons.Count == 0)
            {
                var empty = new MenuItem { Header = Loc.T("KotonNoInstrumentsFound"), IsEnabled = false };
                kotonBrowse.Items.Add(empty);
            }
            else
            {
                foreach (var p in kotons)
                {
                    var it = new MenuItem { Header = p.DisplayName, IsCheckable = false };
                    if (!string.IsNullOrEmpty(track.KotonInstrumentId) &&
                        string.Equals(track.KotonInstrumentId, p.Id, StringComparison.Ordinal))
                        it.Icon = new TextBlock { Text = "✓", FontWeight = FontWeights.Bold };
                    string id = p.Id;
                    it.Click += (s, e) => SelectKoton(track, id);
                    kotonBrowse.Items.Add(it);
                }
            }
            kotonBrowse.Items.Add(new Separator());
            var kotonRescan = new MenuItem { Header = Loc.T("KotonRescan") };
            kotonRescan.Click += (s, e) => { Engine.Timeline.Effects.KotonPluginRegistry.Rescan(); };
            kotonBrowse.Items.Add(kotonRescan);
            menu.Items.Add(kotonBrowse);

            // Actions sur le VSTi actif.
            if (!string.IsNullOrEmpty(track.VstiPath))
            {
                menu.Items.Add(new Separator());
                var edit = new MenuItem { Header = Loc.T("VstiEditor") };
                edit.Click += (s, e) => OpenVstiEditor(track);
                menu.Items.Add(edit);
                var remove = new MenuItem { Header = Loc.T("VstiRemove") };
                remove.Click += (s, e) => RemoveVsti(track);
                menu.Items.Add(remove);
            }
            // Actions sur le plugin Koton actif (indépendantes du VSTi — on ne peut pas avoir les deux
            // à la fois, la sélection efface l'autre, mais on affiche selon ce qui est posé).
            if (!string.IsNullOrEmpty(track.KotonInstrumentId))
            {
                menu.Items.Add(new Separator());
                var edit = new MenuItem { Header = Loc.T("KotonEditor") };
                edit.Click += (s, e) => OpenKotonEditor(track);
                menu.Items.Add(edit);
                var remove = new MenuItem { Header = Loc.T("KotonRemove") };
                remove.Click += (s, e) => RemoveKoton(track);
                menu.Items.Add(remove);
            }

            menu.IsOpen = true;
        }

        void SelectVsti(TimelineTrack track, string path)
        {
            // Snapshot d'annulation AVANT la mutation — comme add/remove d'un insert dans le mixeur.
            PushUndo("track:vsti:" + Id(track));
            track.VstiPath = path;
            track.VstiStateBlob = null;   // nouveau plugin = état frais (pas de patch chargé)
            // Le player en cours de lecture continue de rendre avec le synth MeltySynth de cette piste
            // (snapshot au Start). Le nouveau routage prendra effet au prochain Start — c'est cohérent avec
            // add/remove d'un insert. Un rafraîchissement du header suffit pour l'UI (label + combo grisé).
            Render();
        }

        void RemoveVsti(TimelineTrack track)
        {
            if (string.IsNullOrEmpty(track.VstiPath)) return;
            PushUndo("track:vsti:remove:" + Id(track));
            track.VstiPath = null;
            track.VstiStateBlob = null;
            Render();
        }

        void OpenVstiEditor(TimelineTrack track)
        {
            if (string.IsNullOrEmpty(track.VstiPath))
            {
                MessageBox.Show(Window.GetWindow(this), Loc.T("VstiNoInstrumentsFound"), Loc.T("TrackInstrumentVsti"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!VstRuntimeCheck.IsVcRedistInstalled())
            {
                var res = MessageBox.Show(Window.GetWindow(this), Loc.T("VstVcRedistRequired"), Loc.T("TrackInstrumentVsti"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res == MessageBoxResult.Yes)
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(VstRuntimeCheck.VcRedistDownloadUrl) { UseShellExecute = true }); }
                    catch { }
                }
                return;
            }
            // File OU Directory : un vieux .sq d'avant le fix scanner peut avoir un chemin de bundle-dossier.
            if (!File.Exists(track.VstiPath) && !Directory.Exists(track.VstiPath))
            {
                MessageBox.Show(Window.GetWindow(this), string.Format(Loc.T("VstiPluginMissing"), track.VstiPath), Loc.T("TrackInstrumentVsti"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Instance VSTi dédiée à l'UI, indépendante du renderer (qui vit dans le thread audio).
            // Le state actuel du projet est injecté ; à la fermeture on récupère le nouveau state et on l'écrit
            // dans track.VstiStateBlob — au prochain Start, le renderer relit ce blob. Même pattern que les
            // inserts VST du mixeur (voir MixerDialog.OpenEffectEditor). Format-agnostique via IVstInstrumentHost :
            // routage par extension .vst3 → Vst3Instrument, sinon VstInstrument (.dll).
            var ext = System.IO.Path.GetExtension(track.VstiPath);
            IVstInstrumentHost vsti = string.Equals(ext, ".vst3", StringComparison.OrdinalIgnoreCase)
                ? new Vst3Instrument(track.VstiPath, 44100)
                : (IVstInstrumentHost)new VstInstrument(track.VstiPath, 44100);
            if (!string.IsNullOrEmpty(track.VstiStateBlob)) vsti.LoadState(track.VstiStateBlob);
            if (!vsti.EnsureOpenedSync(512))
            {
                MessageBox.Show(Window.GetWindow(this), Loc.T("VstPluginFailedToLoad"), vsti.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
                try { vsti.Dispose(); } catch { }
                return;
            }
            var w = new VstPluginWindow(vsti, Window.GetWindow(this));
            w.Closed += (s, e) =>
            {
                try { track.VstiStateBlob = vsti.SaveState(); } catch { }
                try { vsti.Dispose(); } catch { }
            };
            w.Show();
        }

        // ---- Plugins Koton natifs -------------------------------------------------------------------
        //
        // Même parcours que VSTi mais sans HWND ni runtime C++ à installer : le plugin est du C# pur, son
        // éditeur est un UserControl WPF. On garde la même règle d'exclusion : sélectionner un Koton
        // efface le VSTi actif (une piste = un instrument tiers) et inversement.

        void SelectKoton(TimelineTrack track, string id)
        {
            PushUndo("track:koton:" + Id(track));
            track.KotonInstrumentId = id;
            track.KotonInstrumentStateBlob = null;   // nouveau plugin = état par défaut
            // Exclusion mutuelle : sélectionner un plugin Koton natif clear un éventuel VSTi précédent.
            // Le renderer donnerait la précédence au VSTi si les deux étaient posés — l'UI empêche ce cas.
            track.VstiPath = null;
            track.VstiStateBlob = null;
            Render();
        }

        void RemoveKoton(TimelineTrack track)
        {
            if (string.IsNullOrEmpty(track.KotonInstrumentId)) return;
            PushUndo("track:koton:remove:" + Id(track));
            track.KotonInstrumentId = null;
            track.KotonInstrumentStateBlob = null;
            Render();
        }

        void OpenKotonEditor(TimelineTrack track)
        {
            if (string.IsNullOrEmpty(track.KotonInstrumentId))
            {
                MessageBox.Show(Window.GetWindow(this), Loc.T("KotonNoInstrumentsFound"),
                    Loc.T("TrackInstrumentKoton"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // Instance dédiée à l'UI (indépendante du renderer). État actuel injecté ; à la fermeture on
            // capture SaveState et on l'écrit dans track.KotonInstrumentStateBlob. Le plugin est disposé
            // à la fermeture — ses UserControl sont libérés en cascade par WPF.
            var plugin = Engine.Timeline.Effects.KotonPluginRegistry.InstantiateInstrument(track.KotonInstrumentId);
            if (plugin == null)
            {
                MessageBox.Show(Window.GetWindow(this),
                    string.Format(Loc.T("KotonPluginMissing"), track.KotonInstrumentId),
                    Loc.T("TrackInstrumentKoton"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Prepare est nécessaire pour les plugins qui allouent des buffers audio à Prepare (le FM
            // synth de référence par ex.). Sample rate de la carte son ; block size raisonnable.
            try { plugin.Prepare(AudioFormat.SampleRate, 8192); } catch { /* un plugin qui jette au Prepare aura un éditeur inutilisable, mais on ouvre quand même pour que l'utilisateur puisse voir le nom */ }
            if (!string.IsNullOrEmpty(track.KotonInstrumentStateBlob))
            {
                try
                {
                    var bytes = Convert.FromBase64String(track.KotonInstrumentStateBlob);
                    plugin.LoadState(bytes);
                }
                catch { }
            }
            var w = new MusicTracker.Dialogs.KotonPluginEditorDialog(plugin, Window.GetWindow(this));
            w.Closed += (s, e) =>
            {
                try
                {
                    var bytes = plugin.SaveState();
                    track.KotonInstrumentStateBlob = (bytes == null || bytes.Length == 0) ? null : Convert.ToBase64String(bytes);
                }
                catch { }
                try { plugin.Dispose(); } catch { }
            };
            w.Show();
        }
    }
}
