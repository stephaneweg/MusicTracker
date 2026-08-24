using System;
using System.Collections.Generic;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Reconstitue un <see cref="IAudioEffect"/> à partir de son descripteur sérialisé (<see cref="TrackEffectData"/>)
    /// et fournit la liste des types installés (pour peupler le menu « Ajouter un effet »).
    /// </summary>
    public static class EffectFactory
    {
        /// <summary>Ordre affiché dans le menu « Ajouter un effet » — n'inclut PAS le VST (il a son propre sous-menu peuplé dynamiquement par <see cref="VstPluginScanner"/>).</summary>
        public static readonly string[] Kinds = new[] { "eq", "comp", "delay", "sat", "reverb" };

        /// <summary>Identifiant stable d'un insert VST2 — extériorisé en constante pour éviter les "vst" littéraux disséminés.</summary>
        public const string VstKind = "vst";

        /// <summary>Identifiant stable d'un insert VST3 — hébergé par <see cref="Vst3Effect"/>.</summary>
        public const string Vst3Kind = "vst3";

        /// <summary>Identifiant stable d'un insert Koton natif — hébergé par <see cref="KotonEffectAdapter"/>.
        /// Le champ <see cref="TrackEffectData.PluginPath"/> porte l'Id du plugin Koton (ex. "koton.oceanreverb"),
        /// pas un chemin — cohérent avec la sémantique de "path opaque de l'implémentation d'effet".</summary>
        public const string KotonKind = "koton";

        /// <summary>Clé de localisation associée à un type (le nom d'affichage).</summary>
        public static string LocKey(string kind)
        {
            switch (kind)
            {
                case "eq":    return "FxEq";
                case "comp":  return "FxCompressor";
                case "delay": return "FxDelay";
                case "sat":   return "FxSaturation";
                case "reverb":return "FxReverb";
                case VstKind: return "FxVst";
                case Vst3Kind:return "FxVst";   // même intitulé côté UI — l'utilisateur voit "VST" sans distinction de version
                case KotonKind:return "FxKoton";
                default:      return kind ?? "";
            }
        }

        public static IAudioEffect Create(string kind, int sampleRate)
        {
            switch (kind)
            {
                case "eq":    return new EqEffect(sampleRate);
                case "comp":  return new CompressorEffect(sampleRate);
                case "delay": return new DelayEffect(sampleRate);
                case "sat":   return new SaturationEffect(sampleRate);
                case "reverb":return new ReverbEffect(sampleRate);
                case VstKind: return new VstEffect(sampleRate);  // PluginPath posé par le caller
                case Vst3Kind:return new Vst3Effect(sampleRate);
                case KotonKind:return null;                       // Koton natifs : instanciation via CreateKoton (Id requis)
                default:      return null;
            }
        }

        /// <summary>Instancie un adaptateur d'effet Koton natif. Retourne <c>null</c> si l'Id est inconnu
        /// (plugin absent du dossier <c>plugins/</c>) — le TrackEffectData survit en no-op, le projet
        /// reste chargeable même si l'utilisateur a désinstallé le plugin.</summary>
        public static IAudioEffect CreateKoton(string kotonEffectId, int sampleRate)
        {
            if (string.IsNullOrEmpty(kotonEffectId)) return null;
            try { return new KotonEffectAdapter(kotonEffectId, sampleRate); }
            catch { return null; }
        }

        /// <summary>Résout le kind à partir du chemin d'un plugin externe : .vst3 → Vst3Kind, sinon VstKind.</summary>
        public static string KindForPluginPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return VstKind;
            // Un bundle .vst3 est un DOSSIER (extension .vst3) — GetExtension marche pareil.
            var ext = System.IO.Path.GetExtension(path);
            return string.Equals(ext, ".vst3", StringComparison.OrdinalIgnoreCase) ? Vst3Kind : VstKind;
        }

        /// <summary>Instancie l'effet et lui applique les paramètres — renvoie null pour un type inconnu (ancien .sq d'une version ultérieure inconnue par ex.).</summary>
        public static IAudioEffect Create(TrackEffectData data, int sampleRate)
        {
            if (data == null) return null;
            IAudioEffect fx;
            // Koton natif : PluginPath porte l'Id du plugin (pas un chemin de fichier). L'instance
            // vient du CACHE (KotonEffectCache) — même objet pour le renderer et pour l'éditeur UI,
            // ce qui permet le live-edit : bouger un slider dans le dialog s'entend au prochain
            // buffer sans avoir a Stop/Play.
            if (data.Kind == KotonKind)
            {
                var adapter = KotonEffectCache.GetOrCreate(data, sampleRate);
                return adapter;   // null si Id inconnu — insert survit en no-op
            }
            fx = Create(data.Kind, sampleRate);
            if (fx == null) return null;
            // VST2/VST3 : le PluginPath et le blob d'état viennent des champs dédiés de TrackEffectData
            // (Params reste vide côté VST). Load(Params) est appelé quand même par symétrie,
            // mais côté VST* c'est un no-op délibéré.
            if (fx is VstEffect vst)
            {
                vst.PluginPath = data.PluginPath;
                vst.LoadState(data.StateBlob);
            }
            else if (fx is Vst3Effect vst3)
            {
                vst3.PluginPath = data.PluginPath;
                vst3.LoadState(data.StateBlob);
            }
            fx.Load(data.Params);
            return fx;
        }
    }
}
