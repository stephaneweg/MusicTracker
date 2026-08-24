using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MusicTracker.Dialogs;
using MusicTracker.Engine.Timeline.Effects;
using MusicTracker.Localization;

namespace MusicTracker.Controls
{
    /// <summary>
    /// Éditeur d'une chaîne d'inserts (la liste d'effets d'une piste, du master, ou du moteur live) :
    /// une ligne par effet avec bouton d'édition, interrupteur, flèches de réordonnancement et suppression,
    /// plus un bouton « Ajouter » qui propose les quatre effets maison, les plugins Koton natifs et les VST
    /// installés.
    ///
    /// Le panneau ne connaît QUE la liste de <see cref="TrackEffectData"/> qu'on lui confie : c'est le même
    /// format que dans un projet .sq, donc la même UI sert au mixeur du séquenceur et au rack live.
    /// <see cref="Changed"/> est levé après toute modification — l'hôte y branche sa persistance et, en
    /// live, la reconstruction de la chaîne audio.
    /// </summary>
    public sealed class InsertChainPanel : UserControl
    {
        readonly List<TrackEffectData> _inserts;
        readonly Window _owner;
        readonly Style _tinyButton;   // style des petits boutons, fourni par l'hôte (dépend de sa fenêtre)
        readonly StackPanel _rows;
        readonly int _sampleRate;

        /// <summary>Levé après ajout / suppression / déplacement / bascule / édition d'un insert.</summary>
        public event Action Changed;

        /// <param name="inserts">Liste éditée EN PLACE (l'hôte en garde la référence).</param>
        /// <param name="owner">Fenêtre parente des dialogues d'édition.</param>
        /// <param name="sampleRate">Fréquence utilisée pour les instances d'édition des plugins.</param>
        /// <param name="tinyButtonStyle">Style appliqué aux petits boutons ; <c>null</c> = boutons par défaut.</param>
        public InsertChainPanel(List<TrackEffectData> inserts, Window owner, int sampleRate, Style tinyButtonStyle = null)
        {
            _inserts = inserts ?? throw new ArgumentNullException(nameof(inserts));
            _owner = owner;
            _sampleRate = sampleRate;
            _tinyButton = tinyButtonStyle;

            _rows = new StackPanel();
            var add = new Button
            {
                Content = Loc.T("AddFxDots"),
                Style = _tinyButton,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0),
            };
            add.Click += (s, e) => ShowAddMenu(add);

            var root = new StackPanel();
            root.Children.Add(_rows);
            root.Children.Add(add);
            Content = root;
            Rebuild();
        }

        /// <summary>Reconstruit les lignes depuis la liste (à appeler si l'hôte la modifie lui-même).</summary>
        public void Rebuild()
        {
            _rows.Children.Clear();
            if (_inserts.Count == 0)
            {
                _rows.Children.Add(new TextBlock
                {
                    Text = Loc.T("LiveNoInsert"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x8C, 0x93)),
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 2),
                });
                return;
            }
            for (int i = 0; i < _inserts.Count; i++) _rows.Children.Add(BuildRow(_inserts[i], i));
        }

        void Notify() { Rebuild(); Changed?.Invoke(); }

        FrameworkElement BuildRow(TrackEffectData d, int index)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Le libellé d'un VST est le nom du fichier : le « nice name » du plugin n'est connu qu'une fois
            // chargé, et on ne charge pas un plugin juste pour dessiner une ligne de liste.
            bool isVst = d.Kind == EffectFactory.VstKind || d.Kind == EffectFactory.Vst3Kind;
            string label = isVst && !string.IsNullOrEmpty(d.PluginPath)
                ? System.IO.Path.GetFileNameWithoutExtension(d.PluginPath)
                : (d.Kind == EffectFactory.KotonKind ? KotonName(d.PluginPath) : Loc.T(EffectFactory.LocKey(d.Kind)));

            var edit = new Button
            {
                Content = label,
                Style = _tinyButton,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Opacity = d.Enabled ? 1.0 : 0.5,
                ToolTip = Loc.T("EditEffect"),
            };
            edit.Click += (s, e) => OpenEditor(d);
            Grid.SetColumn(edit, 0);
            row.Children.Add(edit);

            var onoff = new CheckBox
            {
                IsChecked = d.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 2, 0),
                ToolTip = Loc.T("ToggleEffect"),
            };
            onoff.Checked += (s, e) => { d.Enabled = true; edit.Opacity = 1.0; Changed?.Invoke(); };
            onoff.Unchecked += (s, e) => { d.Enabled = false; edit.Opacity = 0.5; Changed?.Invoke(); };
            Grid.SetColumn(onoff, 1);
            row.Children.Add(onoff);

            var up = new Button { Content = "▲", Style = _tinyButton, Width = 20, IsEnabled = index > 0, ToolTip = Loc.T("LiveMoveUp") };
            up.Click += (s, e) => { Move(index, index - 1); };
            Grid.SetColumn(up, 2);
            row.Children.Add(up);

            var down = new Button { Content = "▼", Style = _tinyButton, Width = 20, IsEnabled = index < _inserts.Count - 1, ToolTip = Loc.T("LiveMoveDown") };
            down.Click += (s, e) => { Move(index, index + 1); };
            Grid.SetColumn(down, 3);
            row.Children.Add(down);

            var del = new Button { Content = "✕", Style = _tinyButton, Width = 20, Margin = new Thickness(3, 0, 0, 0), ToolTip = Loc.T("RemoveEffect") };
            del.Click += (s, e) =>
            {
                // L'adaptateur Koton caché n'a plus de raison d'exister une fois l'insert supprimé.
                if (d.Kind == EffectFactory.KotonKind) KotonEffectCache.Release(d);
                _inserts.Remove(d);
                Notify();
            };
            Grid.SetColumn(del, 4);
            row.Children.Add(del);

            return row;
        }

        void Move(int from, int to)
        {
            if (from < 0 || from >= _inserts.Count || to < 0 || to >= _inserts.Count) return;
            var d = _inserts[from];
            _inserts.RemoveAt(from);
            _inserts.Insert(to, d);
            Notify();
        }

        static string KotonName(string id)
        {
            if (string.IsNullOrEmpty(id)) return Loc.T("FxKoton");
            foreach (var p in KotonPluginRegistry.Effects)
                if (string.Equals(p.Id, id, StringComparison.Ordinal)) return p.DisplayName;
            return "⚠ " + id;
        }

        // ---- menu d'ajout ---------------------------------------------------------------------------------

        void ShowAddMenu(FrameworkElement anchor)
        {
            var m = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            foreach (var kind in EffectFactory.Kinds)
            {
                string k = kind;
                var it = new MenuItem { Header = Loc.T(EffectFactory.LocKey(kind)) };
                it.Click += (s, e) => { _inserts.Add(new TrackEffectData { Kind = k, Enabled = true }); Notify(); };
                m.Items.Add(it);
            }
            m.Items.Add(new Separator());

            var kotonMenu = new MenuItem { Header = Loc.T("FxKotonMenu") };
            var kotons = KotonPluginRegistry.Effects;
            if (kotons.Count == 0) kotonMenu.Items.Add(new MenuItem { Header = Loc.T("KotonNoPluginsFound"), IsEnabled = false });
            // Regroupés par catégorie, comme le sélecteur d'instrument : il y a une vingtaine d'effets.
            // PluginPath porte l'Id du plugin Koton, pas un chemin — cf. EffectFactory.KotonKind.
            KotonPluginMenu.AddGroupedByCategory(kotonMenu, kotons,
                p => p.Category, p => p.DisplayName, null,
                p => { _inserts.Add(new TrackEffectData { Kind = EffectFactory.KotonKind, Enabled = true, PluginPath = p.Id }); Notify(); });
            kotonMenu.Items.Add(new Separator());
            var kRescan = new MenuItem { Header = Loc.T("KotonRescan") };
            kRescan.Click += (s, e) => KotonPluginRegistry.Rescan();
            kotonMenu.Items.Add(kRescan);
            m.Items.Add(kotonMenu);

            var vstMenu = new MenuItem { Header = Loc.T("FxVstMenu") };
            var vsts = VstPluginScanner.GetEffects();
            if (vsts.Count == 0) vstMenu.Items.Add(new MenuItem { Header = Loc.T("VstNoPluginsFound"), IsEnabled = false });
            foreach (var p in vsts)
            {
                string path = p.Path;
                var it = new MenuItem { Header = p.DisplayName };
                it.Click += (s, e) => TryAddVst(path);
                vstMenu.Items.Add(it);
            }
            vstMenu.Items.Add(new Separator());
            var rescan = new MenuItem { Header = Loc.T("VstRescan") };
            rescan.Click += (s, e) => VstPluginScanner.ForceRescan();
            vstMenu.Items.Add(rescan);
            m.Items.Add(vstMenu);

            m.IsOpen = true;
        }

        void TryAddVst(string path)
        {
            if (!VstRuntimeCheck.IsVcRedistInstalled())
            {
                var res = MessageBox.Show(_owner, Loc.T("VstVcRedistRequired"), Loc.T("FxVstMenu"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res == MessageBoxResult.Yes)
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(VstRuntimeCheck.VcRedistDownloadUrl) { UseShellExecute = true }); }
                    catch { }
                }
                return;
            }
            _inserts.Add(new TrackEffectData { Kind = EffectFactory.KindForPluginPath(path), Enabled = true, PluginPath = path });
            Notify();
        }

        // ---- édition ---------------------------------------------------------------------------------------

        /// <summary>Ouvre l'éditeur adapté : GUI native pour un VST, éditeur fourni par le plugin pour un
        /// effet Koton, dialogue de curseurs générique pour les quatre effets maison.</summary>
        void OpenEditor(TrackEffectData d)
        {
            if (d.Kind == EffectFactory.KotonKind) { OpenKotonEditor(d); return; }
            if (d.Kind == EffectFactory.VstKind || d.Kind == EffectFactory.Vst3Kind) { OpenVstEditor(d); return; }
            new EffectConfigDialog(d, _owner).ShowDialog();
            Changed?.Invoke();
        }

        void OpenKotonEditor(TrackEffectData d)
        {
            // Instance PARTAGÉE via le cache : le moteur live rend avec le même adaptateur, donc bouger un
            // curseur s'entend au bloc suivant sans rien arrêter.
            var adapter = KotonEffectCache.GetOrCreate(d, _sampleRate);
            if (adapter == null)
            {
                MessageBox.Show(_owner, Loc.T("KotonPluginFailedToLoad"), Loc.T("FxKotonMenu"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var w = new KotonPluginEditorDialog(adapter.Plugin, _owner);
            w.Closed += (s, e) =>
            {
                try { d.StateBlob = adapter.SaveState(); } catch { }
                try { d.Params = adapter.Save(); } catch { }
                Changed?.Invoke();
            };
            w.Show();
        }

        void OpenVstEditor(TrackEffectData d)
        {
            if (!VstRuntimeCheck.IsVcRedistInstalled())
            {
                MessageBox.Show(_owner, Loc.T("VstVcRedistRequired"), Loc.T("FxVstMenu"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Instance d'ÉDITION dédiée (comme dans le mixeur) : l'état est recopié dans le TrackEffectData à
            // la fermeture, et le moteur le reprendra à la reconstruction de la chaîne.
            IVstEditorHost fx;
            IAudioEffect audio;
            if (d.Kind == EffectFactory.Vst3Kind)
            {
                var v3 = new Vst3Effect(_sampleRate) { PluginPath = d.PluginPath };
                if (!string.IsNullOrEmpty(d.StateBlob)) v3.LoadState(d.StateBlob);
                fx = v3; audio = v3;
            }
            else
            {
                var v2 = new VstEffect(_sampleRate) { PluginPath = d.PluginPath };
                if (!string.IsNullOrEmpty(d.StateBlob)) v2.LoadState(d.StateBlob);
                fx = v2; audio = v2;
            }
            if (!fx.EnsureOpenedSync(512))
            {
                MessageBox.Show(_owner, Loc.T("VstPluginFailedToLoad"), fx.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
                try { (audio as IDisposable)?.Dispose(); } catch { }
                return;
            }
            var w = new VstPluginWindow(fx, _owner);
            w.Closed += (s, e) =>
            {
                try { d.StateBlob = audio.SaveState(); } catch { }
                try { (audio as IDisposable)?.Dispose(); } catch { }
                Changed?.Invoke();
            };
            w.Show();
        }
    }
}
