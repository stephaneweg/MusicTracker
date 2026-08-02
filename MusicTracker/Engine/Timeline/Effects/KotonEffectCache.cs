using System;
using System.Collections.Generic;
using System.Linq;
using KotonStudio.Library;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Cache statique d'instances <see cref="KotonEffectAdapter"/> partagées entre l'éditeur UI et
    /// le renderer audio, indexées par <see cref="TrackEffectData"/> (identité par référence).
    /// Symétrique de <see cref="KotonInstrumentCache"/> côté instruments — même raison d'être :
    /// garantir que bouger un slider dans l'éditeur d'effet s'entend IMMÉDIATEMENT pendant la
    /// lecture, sans avoir à fermer le dialog + Stop + Play.
    ///
    /// **Problème que ce cache résout** : sans lui, chaque appel à
    /// <see cref="EffectFactory.Create(TrackEffectData, int)"/> côté renderer instancie un
    /// <see cref="KotonEffectAdapter"/> neuf, et l'éditeur ouvert dans <c>MixerDialog</c> crée un
    /// autre adapter neuf de son côté. Les deux instances ont des KotonParameter différents en
    /// mémoire → bouger le slider dans l'éditeur modifie l'instance UI, jamais celle qui rend
    /// l'audio. Bug rapporté par l'utilisateur sur Ocean Reverb (2026-08-02).
    ///
    /// **Cycle de vie** : le TrackEffectData est owned par le projet (survit Play/Stop). L'adapter
    /// caché survit tant que le TrackEffectData existe. <see cref="Release"/> est appelé quand
    /// l'insert est retiré. <see cref="ClearAll"/> à la fermeture d'un tab timeline.
    /// </summary>
    public static class KotonEffectCache
    {
        static readonly Dictionary<TrackEffectData, KotonEffectAdapter> _cache =
            new Dictionary<TrackEffectData, KotonEffectAdapter>();
        static readonly object _lock = new object();

        /// <summary>Retourne l'adapter caché pour ce TrackEffectData, ou en crée un nouveau via
        /// <see cref="KotonEffectAdapter(string, int)"/> et le met en cache. Retourne <c>null</c>
        /// si l'Id Koton est inconnu (plugin absent du dossier <c>plugins/</c>).</summary>
        public static KotonEffectAdapter GetOrCreate(TrackEffectData data, int sampleRate)
        {
            if (data == null || string.IsNullOrEmpty(data.PluginPath)) return null;
            lock (_lock)
            {
                if (_cache.TryGetValue(data, out var existing)) return existing;
                KotonEffectAdapter adapter;
                try { adapter = new KotonEffectAdapter(data.PluginPath, sampleRate); }
                catch { return null; }
                // Charger le state ET les params dès la création — l'adapter démarre synchro avec
                // ce qui est stocké dans TrackEffectData (fraîchement chargé depuis le .sq ou modifié
                // par une session précédente).
                if (!string.IsNullOrEmpty(data.StateBlob)) adapter.LoadState(data.StateBlob);
                adapter.Load(data.Params);
                _cache[data] = adapter;
                return adapter;
            }
        }

        /// <summary>Vrai si un adapter est déjà en cache pour ce TrackEffectData — utile au renderer
        /// pour décider s'il faut re-Load le state (première instance) ou pas (instance réutilisée,
        /// KotonParameter courants préservés).</summary>
        public static bool Contains(TrackEffectData data)
        {
            if (data == null) return false;
            lock (_lock) return _cache.ContainsKey(data);
        }

        /// <summary>Libère l'instance associée à ce TrackEffectData. Appelé quand l'insert est
        /// retiré ou remplacé.</summary>
        public static void Release(TrackEffectData data)
        {
            if (data == null) return;
            lock (_lock)
            {
                if (_cache.TryGetValue(data, out var adapter))
                {
                    try { adapter.Dispose(); } catch { }
                    _cache.Remove(data);
                }
            }
        }

        /// <summary>Libère toutes les instances cachées — appelé à la fermeture d'un tab timeline
        /// ou de l'application.</summary>
        public static void ClearAll()
        {
            lock (_lock)
            {
                foreach (var a in _cache.Values) { try { a.Dispose(); } catch { } }
                _cache.Clear();
            }
        }

        /// <summary>Nombre d'instances cachées — debug / monitoring.</summary>
        public static int Count { get { lock (_lock) return _cache.Count; } }
    }
}
