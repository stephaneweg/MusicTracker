using System;
using System.Windows;
using System.Windows.Controls;
using KotonStudio.Library;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// Fenêtre WPF non-modale qui héberge l'éditeur (<see cref="UserControl"/>) d'un plugin Koton natif.
    /// Chrome standard Windows (dark via CommonBackground/CommonForeground), contenu = juste le contrôle
    /// que le plugin fournit via <see cref="IKotonPlugin.CreateEditor"/>. Contrairement à
    /// <c>VstPluginWindow</c>, aucune bidouille HwndHost / classe Win32 n'est nécessaire — le plugin est
    /// du WPF pur donc WPF layoute et gère les events natifs sans problème d'airspace.
    ///
    /// **Cycle** : le plugin sous-jacent est OWNED par l'appelant (typiquement une instance d'éditeur UI
    /// dédiée, distincte de celle qu'utilise le renderer audio) — cette fenêtre ne dispose PAS le plugin
    /// à sa fermeture. L'appelant capture <see cref="IKotonPlugin.SaveState"/> après le Closed et libère.
    /// </summary>
    public partial class KotonPluginEditorDialog : Window
    {
        readonly IKotonPlugin _plugin;

        public KotonPluginEditorDialog(IKotonPlugin plugin, Window owner)
        {
            InitializeComponent();
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            Owner = owner;
            Title = plugin.DisplayName ?? "Plugin";

            Loaded += OnLoaded;
        }
        void btnClose_Click(object sender, RoutedEventArgs e) => this.Close();

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_plugin.HasEditor)
            {
                // Pas d'éditeur custom → un simple message. Un futur v2 pourra générer un éditeur générique
                // depuis IKotonPlugin.Parameters (une ligne = un slider), mais v1 = plugin doit fournir son UI.
                hostBorder.Child = new TextBlock
                {
                    Text = "Ce plugin ne fournit pas d'éditeur.",
                    Margin = new Thickness(24),
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                return;
            }
            try
            {
                var ctl = _plugin.CreateEditor();
                if (ctl == null)
                {
                    hostBorder.Child = new TextBlock
                    {
                        Text = "L'éditeur du plugin est vide.",
                        Margin = new Thickness(24),
                    };
                    return;
                }
                // Barre de presets posée par l'hôte AU-DESSUS de l'éditeur : le plugin n'a rien à faire
                // pour l'avoir, elle ne parle qu'aux Parameters/SaveState que le contrat impose déjà.
                var dock = new DockPanel();
                var bar = new Controls.KotonPresetBar(_plugin);
                DockPanel.SetDock(bar, Dock.Top);
                dock.Children.Add(bar);
                dock.Children.Add(ctl);
                hostBorder.Child = dock;
            }
            catch (Exception ex)
            {
                hostBorder.Child = new TextBlock
                {
                    Text = "Erreur lors de l'ouverture de l'éditeur : " + ex.Message,
                    Margin = new Thickness(24),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 400,
                };
            }
        }

        void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // WPF détruit automatiquement les controls enfants — pas de nettoyage explicite requis. Le plugin
            // reste vivant (l'appelant Dispose après Closed).
            try { hostBorder.Child = null; } catch { }
        }
    }
}
