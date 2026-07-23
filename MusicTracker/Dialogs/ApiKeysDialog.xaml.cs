using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MusicTracker.Engine.AI;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// Manages the named API keys (Paramètres → Clés API): a ListView of [Fournisseur][Nom][Clé] rows bound
    /// two-way to an <see cref="ObservableCollection{ApiKeyEntry}"/> working copy. On "Enregistrer" it replaces
    /// <see cref="AppSettings.ApiKeys"/> and persists; Annuler discards.
    /// </summary>
    public partial class ApiKeysDialog : Window
    {
        /// <summary>Provider dropdown options (id + display label) for the per-row ComboBox.</summary>
        public class ProviderOption { public string Id { get; set; } public string Label { get; set; } }

        public List<ProviderOption> Providers { get; }
        public ObservableCollection<ApiKeyEntry> Keys { get; }

        public ApiKeysDialog()
        {
            InitializeComponent();
            AiProviders.EnsureMigrated();
            Providers = AiProviders.Ids.Select(id => new ProviderOption { Id = id, Label = AiProviders.Label(id) }).ToList();
            // Deep copy so Cancel discards edits.
            Keys = new ObservableCollection<ApiKeyEntry>(
                AppSettings.Instance.ApiKeys.Select(k => new ApiKeyEntry { Provider = k.Provider, Name = k.Name, Key = k.Key }));
            Keys.CollectionChanged += (s, e) => UpdateEmpty();
            DataContext = this;
            UpdateEmpty();
        }

        void UpdateEmpty() => txtEmpty.Visibility = Keys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        void btnAdd_Click(object sender, RoutedEventArgs e)
        {
            string prov = AiProviders.Norm(AppSettings.Instance.AiProvider);
            int n = Keys.Count(k => AiProviders.Norm(k.Provider) == prov) + 1;
            Keys.Add(new ApiKeyEntry { Provider = prov, Name = "Clé " + n, Key = "" });
        }

        void btnDeleteRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ApiKeyEntry entry) Keys.Remove(entry);
        }

        void btnOk_Click(object sender, RoutedEventArgs e)
        {
            // Keep only rows with a key; normalise the provider and give unnamed rows a fallback name.
            var cleaned = Keys
                .Where(k => !string.IsNullOrWhiteSpace(k.Key))
                .Select(k => new ApiKeyEntry
                {
                    Provider = AiProviders.Norm(k.Provider),
                    Name = string.IsNullOrWhiteSpace(k.Name) ? "Sans nom" : k.Name.Trim(),
                    Key = k.Key.Trim(),
                })
                .ToList();

            var s = AppSettings.Instance;
            s.ApiKeys = cleaned;
            // Drop selected-key names that no longer exist.
            foreach (var p in AiProviders.Ids)
                if (s.SelectedKeyName.TryGetValue(p, out var sel) && !cleaned.Any(k => k.Provider == p && k.Name == sel))
                    s.SelectedKeyName.Remove(p);
            s.Save();
            DialogResult = true;
        }

        void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
