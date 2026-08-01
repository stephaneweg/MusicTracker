using System;
using System.Collections.Generic;
using System.Linq;
using KotonStudio.Library;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Cache statique d'instances Koton natives partagees a travers tous les cycles Play/Stop d'une
    /// session. Pendant : le meme adaptateur (donc le meme IKotonInstrument sous-jacent) est reutilise
    /// entre chaque Play — les KotonParameter modifies depuis l'editeur restent bien lus au prochain
    /// Render sans reset. Sans ce cache, `TimelinePlayer.TrySetupMeltySynth` creait une nouvelle
    /// instance a chaque Start : les slidersdu dialog etaient bindes sur la 1re instance, le renderer
    /// utilisait la 2e (ou la 3e apres Stop+Play), l'audio ignorait donc les changements UI.
    ///
    /// Meme pattern que <see cref="VstInstrumentCache"/> — cle (TimelineTrack, id) par reference,
    /// nettoyage explicite via <see cref="ReleaseTrack"/> / <see cref="ClearAll"/>, thread-safe par lock.
    ///
    /// **Sur le poids d'un plugin Koton natif** : contrairement aux VSTi (LoadLibrary + COM +
    /// activation DSP), les Koton sont juste des objets .NET managed — instancier une nouvelle voix
    /// polyphonique est cheap. Mais le VRAI probleme n'est pas le cout d'instanciation, c'est le
    /// SHARING d'instance entre l'editeur UI et le renderer : le cache garantit qu'ils voient tous
    /// les deux le meme objet.
    /// </summary>
    public static class KotonInstrumentCache
    {
        static readonly Dictionary<(TimelineTrack, string), KotonInstrumentAdapter> _cache =
            new Dictionary<(TimelineTrack, string), KotonInstrumentAdapter>();
        static readonly object _lock = new object();

        /// <summary>Retourne l'adaptateur Koton cache pour cette (piste, id), ou en cree un nouveau
        /// via <see cref="KotonPluginRegistry.InstantiateInstrument"/> et le met en cache. Retourne
        /// <c>null</c> si l'id est inconnu (plugin absent du dossier <c>plugins/</c>) ou si le
        /// constructeur du plugin jette.</summary>
        public static KotonInstrumentAdapter GetOrCreate(TimelineTrack track, string kotonInstrumentId, int sampleRate)
        {
            if (track == null || string.IsNullOrEmpty(kotonInstrumentId)) return null;
            lock (_lock)
            {
                var key = (track, kotonInstrumentId);
                if (_cache.TryGetValue(key, out var existing)) return existing;
                var koton = KotonPluginRegistry.InstantiateInstrument(kotonInstrumentId);
                if (koton == null) return null;
                var adapter = new KotonInstrumentAdapter(koton, sampleRate, koton.DisplayName);
                _cache[key] = adapter;
                return adapter;
            }
        }

        /// <summary>Vrai si (piste, id) est deja en cache. Utilise par
        /// <c>TimelinePlayer.TrySetupMeltySynth</c> pour decider s'il faut appliquer <c>LoadState</c>
        /// (nouvelle instance seulement) ou pas (instance reutilisee, state courant preserve).</summary>
        public static bool Contains(TimelineTrack track, string kotonInstrumentId)
        {
            if (track == null || string.IsNullOrEmpty(kotonInstrumentId)) return false;
            lock (_lock) return _cache.ContainsKey((track, kotonInstrumentId));
        }

        /// <summary>Libere toutes les instances Koton associees a cette piste (quel que soit l'id).
        /// A appeler quand : (1) l'utilisateur retire le plugin d'une piste, (2) l'utilisateur change
        /// de plugin sur une piste, (3) une piste est supprimee du projet.</summary>
        public static void ReleaseTrack(TimelineTrack track)
        {
            if (track == null) return;
            lock (_lock)
            {
                var keys = _cache.Keys.Where(k => ReferenceEquals(k.Item1, track)).ToList();
                foreach (var k in keys)
                {
                    try { _cache[k].Dispose(); } catch { }
                    _cache.Remove(k);
                }
            }
        }

        /// <summary>Libere TOUTES les instances Koton cachees. Appele a la fermeture d'un onglet
        /// timeline / de l'application.</summary>
        public static void ClearAll()
        {
            lock (_lock)
            {
                foreach (var a in _cache.Values) { try { a.Dispose(); } catch { } }
                _cache.Clear();
            }
        }

        /// <summary>Nombre d'instances actuellement en cache — debug / monitoring.</summary>
        public static int Count { get { lock (_lock) return _cache.Count; } }
    }
}
