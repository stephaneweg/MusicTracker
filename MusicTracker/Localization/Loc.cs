using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Data;
using System.Windows.Markup;

namespace MusicTracker.Localization
{
    /// <summary>One selectable UI language: its two-letter <see cref="Code"/> ("fr", "en", …) and the
    /// endonym <see cref="Name"/> shown in the settings combo ("Français", "English", …).</summary>
    public sealed class LanguageOption
    {
        public string Code { get; }
        public string Name { get; }
        public LanguageOption(string code, string name) { Code = code; Name = name; }
        public override string ToString() => Name;
    }

    /// <summary>
    /// Runtime UI localization, keyed by STABLE string keys (e.g. <c>ComposeWithAI</c>) — never by translatable text.
    /// Each language is a <c>key → text</c> table in a loose file next to the exe: <c>Localization\lang.&lt;code&gt;.json</c>.
    /// French (<see cref="SourceLanguage"/>) is the fallback when a key is missing from the active language; the key
    /// itself is the last resort (so a typo shows up as the visible key, not a crash).
    ///
    /// The available languages are DISCOVERED at startup by scanning that folder — so adding a language is just
    /// dropping a <c>lang.&lt;code&gt;.json</c> file in the install's <c>Localization</c> folder; it appears in the
    /// settings combo on the next launch with NO rebuild. The display name is the culture's own endonym.
    ///
    /// XAML uses the <see cref="TrExtension"/> (<c>{loc:Tr 'ComposeWithAI'}</c>), which binds through
    /// <see cref="TrConverter"/> to <see cref="Language"/> so every string re-evaluates live when the language changes.
    /// Code paths call <see cref="Loc.T"/>.
    /// </summary>
    public class LocalizationManager : INotifyPropertyChanged
    {
        /// <summary>The source language (holds every key; used as the fallback and shipped in <c>lang.fr.json</c>).</summary>
        public const string SourceLanguage = "fr";

        /// <summary>The folder (next to the exe) holding the <c>lang.&lt;code&gt;.json</c> tables.</summary>
        const string LangFolder = "Localization";

        static LocalizationManager _instance;
        public static LocalizationManager Instance => _instance ?? (_instance = new LocalizationManager());

        // language code -> (key -> translated text)
        readonly Dictionary<string, Dictionary<string, string>> _maps =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        readonly List<LanguageOption> _available = new List<LanguageOption>();
        string _lang = SourceLanguage;

        LocalizationManager()
        {
            DiscoverLanguages();
        }

        /// <summary>The languages found on disk (source language first, then the rest by display name). Drives the
        /// settings combo. Rebuilt at startup from the <c>Localization</c> folder — no rebuild needed to add one.</summary>
        public IReadOnlyList<LanguageOption> Available => _available;

        // Scan Localization\lang.<code>.json next to the exe; load each table and list the language.
        void DiscoverLanguages()
        {
            _maps.Clear();
            _available.Clear();
            try
            {
                string dir = MusicTracker.AppPaths.Local(LangFolder);
                if (Directory.Exists(dir))
                {
                    foreach (var path in Directory.GetFiles(dir, "lang.*.json"))
                    {
                        string name = Path.GetFileNameWithoutExtension(path);       // "lang.fr"
                        if (!name.StartsWith("lang.", StringComparison.OrdinalIgnoreCase)) continue;
                        string code = name.Substring("lang.".Length).Trim().ToLowerInvariant();
                        if (string.IsNullOrEmpty(code)) continue;
                        _maps[code] = LoadMapFromFile(path);
                        if (!_available.Exists(l => l.Code == code))
                            _available.Add(new LanguageOption(code, DisplayName(code)));
                    }
                }
            }
            catch { /* discovery is best-effort — fall back to whatever loaded (possibly nothing) */ }

            // Guarantee the source language is always selectable, even if its file is missing/unreadable.
            if (!_maps.ContainsKey(SourceLanguage)) _maps[SourceLanguage] = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!_available.Exists(l => l.Code == SourceLanguage)) _available.Add(new LanguageOption(SourceLanguage, DisplayName(SourceLanguage)));

            _available.Sort((a, b) =>
                a.Code == SourceLanguage ? -1 :
                b.Code == SourceLanguage ? 1 :
                string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        }

        // The language's own endonym ("Français", "English", "Deutsch") from its culture; falls back to the code.
        static string DisplayName(string code)
        {
            try
            {
                string n = CultureInfo.GetCultureInfo(code).NativeName;
                if (!string.IsNullOrEmpty(n)) return char.ToUpper(n[0], CultureInfo.InvariantCulture) + n.Substring(1);
            }
            catch { }
            return code;
        }

        /// <summary>Active language code ("fr", "en", …). Setting it re-cultures the threads and refreshes every {loc:Tr}.</summary>
        public string Language
        {
            get => _lang;
            set
            {
                string code = Normalize(value);
                if (_lang == code) return;
                _lang = code;
                ApplyCulture();
                Raise(nameof(Language));
                Raise("Item[]");
                Raise(nameof(IsEnglish));
            }
        }

        /// <summary>True when the active language is English (kept for the changelog + AI-prompt language switches).</summary>
        public bool IsEnglish => _lang == "en";

        /// <summary>Two-letter code of the active language (for the changelog + AI prompts).</summary>
        public string TwoLetter => _lang;

        public string this[string key] => Translate(key);

        /// <summary>Translate a stable key: active language → source language → the key itself.</summary>
        public string Translate(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            if (_maps.TryGetValue(_lang, out var m) && m.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                return v;
            if (_lang != SourceLanguage && _maps.TryGetValue(SourceLanguage, out var src)
                && src.TryGetValue(key, out var fv) && !string.IsNullOrEmpty(fv))
                return fv;
            return key;
        }

        /// <summary>Set the active language from a persisted/loose code ("en", "EN", "en-US", "english", …).</summary>
        public void SetLanguageCode(string code) => Language = code;

        // Map a loose value onto one of the discovered codes; unknown → the source language.
        string Normalize(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return SourceLanguage;
            string c = code.Trim().ToLowerInvariant();
            if (_maps.ContainsKey(c)) return c;
            foreach (var k in _maps.Keys)
                if (c.StartsWith(k)) return k;
            return SourceLanguage;
        }

        /// <summary>Align the thread cultures with the UI language (number/date formatting shown to the user).</summary>
        public void ApplyCulture()
        {
            try
            {
                var ci = CultureInfo.GetCultureInfo(_lang);
                Thread.CurrentThread.CurrentCulture = ci;
                Thread.CurrentThread.CurrentUICulture = ci;
                CultureInfo.DefaultThreadCurrentCulture = ci;
                CultureInfo.DefaultThreadCurrentUICulture = ci;
            }
            catch { /* unknown culture id — leave the current culture in place */ }
        }

        static Dictionary<string, string> LoadMapFromFile(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (parsed != null)
                    foreach (var kv in parsed) map[kv.Key] = kv.Value;
            }
            catch { /* missing/corrupt table — everything falls back to the source language */ }
            return map;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>Short static entry point for code paths: <c>Loc.T("ComposeWithAI")</c>.</summary>
    public static class Loc
    {
        public static string T(string key) => LocalizationManager.Instance.Translate(key);
        public static bool IsEnglish => LocalizationManager.Instance.IsEnglish;
    }

    /// <summary>Translates through the localization table. The stable KEY is passed as the ConverterParameter,
    /// so it survives binding-path escaping unchanged.</summary>
    public class TrConverter : IValueConverter
    {
        public static readonly TrConverter Instance = new TrConverter();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => LocalizationManager.Instance.Translate(parameter as string);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// XAML markup extension: <c>{loc:Tr 'ComposeWithAI'}</c>. Returns a one-way binding to the manager's
    /// <see cref="LocalizationManager.Language"/>, translated via <see cref="TrConverter"/> using the KEY as the
    /// parameter — so the value updates live whenever the language changes.
    /// </summary>
    [MarkupExtensionReturnType(typeof(string))]
    public class TrExtension : MarkupExtension
    {
        [ConstructorArgument("key")]
        public string Key { get; set; }

        public TrExtension() { }
        public TrExtension(string key) { Key = key; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var binding = new Binding(nameof(LocalizationManager.Language))
            {
                Source = LocalizationManager.Instance,
                Mode = BindingMode.OneWay,
                Converter = TrConverter.Instance,
                ConverterParameter = Key
            };
            return binding.ProvideValue(serviceProvider);
        }
    }
}
