using System;
using System.Collections.Generic;
using System.Text;
using KotonStudio.Library;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Adaptateur qui présente un <see cref="IKotonEffect"/> (contrat de plugin Koton natif) sous la
    /// forme d'un <see cref="IAudioEffect"/> attendu par la chaîne d'inserts de <c>TimelinePlayer</c>.
    /// Symétrique de <see cref="KotonInstrumentAdapter"/> côté instruments.
    ///
    /// **Identification** : le champ <see cref="TrackEffectData.PluginPath"/> porte l'Id du plugin
    /// Koton (ex. "koton.oceanreverb"), pas un chemin de fichier — cohérent avec la sémantique du
    /// champ (identifiant opaque de l'implémentation d'effet).
    ///
    /// **Persistance** : les KotonParameter sont plats et n'ont pas besoin de <see cref="Save"/>
    /// (l'hôte snapshotte via <see cref="IKotonPlugin.SaveState"/>/<see cref="IKotonPlugin.LoadState"/>
    /// qui retourne un blob binaire). On stocke ce blob en base64 via <see cref="SaveState"/>/<see cref="LoadState"/>
    /// dans <see cref="TrackEffectData.StateBlob"/> — exactement comme un VST.
    /// </summary>
    public sealed class KotonEffectAdapter : IAudioEffect, IDisposable
    {
        readonly int _sampleRate;
        readonly IKotonEffect _plugin;
        bool _prepared;

        public string Kind => EffectFactory.KotonKind;
        public IKotonEffect Plugin => _plugin;
        public string PluginId => _plugin.Id;
        public string DisplayName => _plugin.DisplayName;

        /// <summary>Construit un adaptateur pour l'effet Koton identifié par <paramref name="kotonEffectId"/>.
        /// Retourne <c>null</c> côté factory si l'id est inconnu (plugin absent) — pas une exception
        /// pour rester compatible avec le pattern "TrackEffectData d'un projet chargé sans le plugin".</summary>
        public KotonEffectAdapter(string kotonEffectId, int sampleRate)
        {
            if (string.IsNullOrEmpty(kotonEffectId)) throw new ArgumentException("kotonEffectId required", nameof(kotonEffectId));
            _sampleRate = sampleRate;
            _plugin = KotonPluginRegistry.InstantiateEffect(kotonEffectId)
                      ?? throw new InvalidOperationException("Unknown Koton effect id: " + kotonEffectId);
        }

        /// <summary>Prépare le plugin avec le sample rate courant. Idempotent — un 2e appel est un no-op
        /// (le player peut réinsérer l'effet ou re-Load ses paramètres sans re-Prepare le plugin).</summary>
        void EnsurePrepared()
        {
            if (_prepared) return;
            _plugin.Prepare(_sampleRate, 4096);
            _prepared = true;
        }

        public void Process(float[] left, float[] right, int frames)
        {
            EnsurePrepared();
            try { _plugin.Process(left.AsSpan(0, frames), right.AsSpan(0, frames)); }
            catch { /* bypass silencieux — un plugin qui jette ne doit pas planter le thread audio */ }
        }

        public void Reset()
        {
            if (_prepared) { try { _plugin.Reset(); } catch { } }
        }

        // Les KotonParameter sont typés double et lus directement par le plugin (pas via un dictionnaire),
        // mais le format .sq stocke Save()/Load() en dictionnaire nom→valeur ; on remplit celui-ci depuis
        // Parameters pour que les KotonParameter figurent aussi dans le blob "clair" (utile pour debug
        // + compatible avec un vieux .sq relu par une future version qui aurait supprimé StateBlob).
        public Dictionary<string, double> Save()
        {
            var d = new Dictionary<string, double>();
            try { foreach (var p in _plugin.Parameters) d[p.Id] = p.Value; }
            catch { }
            return d;
        }

        public void Load(Dictionary<string, double> data)
        {
            if (data == null) return;
            try
            {
                foreach (var p in _plugin.Parameters)
                    if (data.TryGetValue(p.Id, out var v)) p.Value = v;
            }
            catch { }
        }

        /// <summary>Sérialise l'état complet du plugin (KotonParameter + état interne libre) en base64.
        /// Cohérent avec la convention VST : <see cref="TrackEffectData.StateBlob"/> = un string
        /// opaque, sérialisé/désérialisé par le plugin lui-même.</summary>
        public string SaveState()
        {
            try
            {
                var bytes = _plugin.SaveState();
                if (bytes == null || bytes.Length == 0) return null;
                return Convert.ToBase64String(bytes);
            }
            catch { return null; }
        }

        public void LoadState(string state)
        {
            if (string.IsNullOrEmpty(state)) return;
            try
            {
                var bytes = Convert.FromBase64String(state);
                _plugin.LoadState(bytes);
            }
            catch { /* blob corrompu → défauts du plugin, pas d'exception */ }
        }

        public void Dispose()
        {
            try { _plugin?.Dispose(); } catch { }
        }
    }
}
