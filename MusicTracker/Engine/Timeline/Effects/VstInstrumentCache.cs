using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Cache statique d'instances VSTi (VST2 <see cref="VstInstrument"/> et VST3 <see cref="Vst3Instrument"/>)
    /// partagées à travers tous les cycles Play/Stop d'une session Koton. Résout le bug historique du
    /// « silence croissant » : chaque Play recréait un <c>TimelinePlayer</c> qui recréait un
    /// <c>VstInstrument</c>/<c>Vst3Instrument</c> neuf (LoadLibrary + COM initialize + activation DSP).
    /// La 1re instance chargeait proprement, mais les instances suivantes souffraient d'un état DLL
    /// statique du plugin (samples lazy-loaded partiellement, oscillator LUTs à moitié initialisés,
    /// filter states orphelins…) ou de ressources natives pas complètement libérées entre les
    /// <c>Dispose</c>. Résultat : ~1 s de silence audible en plus à chaque cycle Play/Stop/Play.
    ///
    /// **Approche** : une seule instance VSTi vit par tuple (<see cref="TimelineTrack"/>, pluginPath)
    /// pour toute la session. Elle est créée au 1er <c>Play</c> qui l'utilise (via
    /// <see cref="GetOrCreate"/>), REUTILISÉE tel quel à chaque <c>Play</c> suivant, et libérée
    /// explicitement seulement quand :
    /// - l'utilisateur retire le VSTi de la piste (<see cref="ReleaseTrack"/>) ;
    /// - l'utilisateur change de VSTi sur la piste (<see cref="ReleaseTrack"/> avant nouveau path) ;
    /// - la piste est supprimée du projet (<see cref="ReleaseTrack"/>) ;
    /// - l'onglet timeline / l'application se ferme (<see cref="ClearAll"/>).
    ///
    /// **Clé référence-based** (<c>TimelineTrack</c> par réf, pas par nom) : deux pistes identiques qui
    /// pointent sur le même plugin ne partagent PAS d'instance — chacune garde son propre état MIDI
    /// (notes tenues, sustain, program courant, chunk de paramètres). C'est le comportement attendu :
    /// mixer plusieurs instances du même synthé sur des pistes différentes doit produire des voix
    /// indépendantes.
    ///
    /// **Restriction du scope** : seuls les VSTi (VST2/VST3) sont cachés — les plugins Koton natifs
    /// (<see cref="KotonInstrumentAdapter"/>) sont légers à créer (pas de LoadLibrary, pas de COM,
    /// tout .NET managé) et ne souffrent pas du bug. Documenté comme extension future si un plugin
    /// natif se met à souffrir du même symptôme.
    ///
    /// **Thread-safety** : toutes les opérations sont sous un lock unique. Contention négligeable
    /// puisque <see cref="GetOrCreate"/> n'est appelé qu'au setup d'un <c>TimelinePlayer</c>
    /// (thread UI ou thread audio, une fois par piste par Play) et <see cref="ReleaseTrack"/> est
    /// UI-thread only.
    ///
    /// **Persistance d'état (VstiStateBlob)** : l'appelant NE ré-applique PAS <c>LoadState</c> quand
    /// il récupère une instance existante — le plugin garde son état accumulé (patch chargé, notes
    /// en release, envelopes en cours). Cohérent pour Play → Stop → Play : l'utilisateur attend que
    /// le patch qu'il vient d'éditer reste appliqué. Pour un nouveau projet (nouvelle instance
    /// <c>TimelineTrack</c>), la clé cache change → nouvelle instance VSTi → <c>LoadState(blob)</c>
    /// s'applique naturellement au load initial.
    /// </summary>
    public static class VstInstrumentCache
    {
        // Dict sur tuple (référence TimelineTrack, path). Attention : le tuple par défaut compare les
        // références du TimelineTrack (RuntimeHelpers.Equals au sein d'ITuple.Equals appelle Object.Equals
        // qui pour un class non-overridé est ReferenceEquals — c'est exactement ce qu'on veut, pas d'égalité
        // par contenu de la track).
        static readonly Dictionary<(TimelineTrack, string), IVstInstrumentHost> _cache =
            new Dictionary<(TimelineTrack, string), IVstInstrumentHost>();
        static readonly object _lock = new object();

        /// <summary>Retourne l'instance VSTi cachée pour cette (piste, path), ou en crée une nouvelle
        /// (VST2 ou VST3 selon l'extension du path) et la met en cache. Retourne <c>null</c> si les
        /// arguments sont invalides. Les erreurs de chargement du plugin ne surviennent pas ici — le
        /// constructeur ne charge rien, <c>EnsureLoaded</c> est appelé au 1er Render (comportement
        /// historique de <see cref="VstInstrument"/> et <see cref="Vst3Instrument"/>).</summary>
        public static IVstInstrumentHost GetOrCreate(TimelineTrack track, string pluginPath, int sampleRate)
        {
            if (track == null || string.IsNullOrEmpty(pluginPath)) return null;
            lock (_lock)
            {
                var key = (track, pluginPath);
                if (_cache.TryGetValue(key, out var existing)) return existing;
                IVstInstrumentHost v;
                if (pluginPath.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
                    v = new Vst3Instrument(pluginPath, sampleRate);
                else
                    v = new VstInstrument(pluginPath, sampleRate);
                _cache[key] = v;
                return v;
            }
        }

        /// <summary>Vrai si (piste, path) est déjà en cache. Utilisé par
        /// <c>TimelinePlayer.TrySetupMeltySynth</c> pour décider s'il faut appliquer <c>LoadState</c>
        /// (nouvelle instance seulement) ou pas (instance réutilisée, state courant préservé).</summary>
        public static bool Contains(TimelineTrack track, string pluginPath)
        {
            if (track == null || string.IsNullOrEmpty(pluginPath)) return false;
            lock (_lock) return _cache.ContainsKey((track, pluginPath));
        }

        /// <summary>Libère toutes les instances VSTi associées à cette piste (quel que soit le path).
        /// À appeler quand : (1) l'utilisateur retire le VSTi d'une piste, (2) l'utilisateur change de
        /// VSTi sur une piste (l'ancien path devient orphelin), (3) une piste est supprimée du projet.
        ///
        /// **Sécurité vis-à-vis d'un player en cours** : l'appelant DOIT s'assurer qu'aucun
        /// <c>TimelinePlayer</c> n'utilise l'instance quand cette méthode est appelée — sinon le thread
        /// audio pourrait rendre sur un plugin disposé (<c>_ctx == null</c> → silence dans nos deux
        /// hôtes, mais mieux vaut ne pas courir le risque). En pratique : appelée sur des event UI
        /// alors que le player du même TimelineScreen est stoppé, ou n'a jamais démarré.</summary>
        public static void ReleaseTrack(TimelineTrack track)
        {
            if (track == null) return;
            lock (_lock)
            {
                // ToList() : on modifie le dict pendant la boucle, sinon InvalidOperationException.
                var keys = _cache.Keys.Where(k => ReferenceEquals(k.Item1, track)).ToList();
                foreach (var k in keys)
                {
                    try { _cache[k].Dispose(); } catch { }
                    _cache.Remove(k);
                }
            }
        }

        /// <summary>Libère TOUTES les instances VSTi cachées. À appeler à la fermeture d'un onglet
        /// timeline (les pistes de ce projet ne seront plus jamais rejouées) ou à la sortie de
        /// l'application. Après cet appel, le prochain <see cref="GetOrCreate"/> recréera des
        /// instances neuves.
        ///
        /// **NB** : c'est global, pas par projet. Un usage multi-onglets qui ferme UN onglet et
        /// veut garder les VSTi des AUTRES onglets doit appeler <see cref="ReleaseTrack"/> ciblé
        /// sur les pistes du projet fermé, pas <c>ClearAll</c>.</summary>
        public static void ClearAll()
        {
            lock (_lock)
            {
                foreach (var v in _cache.Values) { try { v.Dispose(); } catch { } }
                _cache.Clear();
            }
        }

        /// <summary>Nombre d'instances actuellement en cache. Utile pour debug / monitoring mémoire.</summary>
        public static int Count { get { lock (_lock) return _cache.Count; } }
    }
}
