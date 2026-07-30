using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using KotonStudio.Library;

namespace MusicTracker.Engine.Flow
{
    /// <summary>
    /// Un bloc timeline dont les notes sont produites par un <see cref="IKotonGenerator"/> vivant
    /// (plugin natif Koton chargé depuis <c>plugins/*.ksl</c>). Le module porte l'id du plugin +
    /// son blob d'état sérialisé (pour restaurer les paramètres au chargement du projet) + la durée
    /// posée en beats. À la 1re utilisation, l'hôte instancie le générateur via
    /// <see cref="Engine.Timeline.Effects.KotonPluginRegistry.InstantiateGenerator"/>, applique le
    /// blob et garde l'instance vivante dans <see cref="RuntimeInstance"/> — ré-utilisée par le
    /// player (RenderNotes), le score (RiffForModule), l'export MIDI (FlattenLeaf) et l'éditeur
    /// (panneau du bas).
    ///
    /// **Persistance** : <see cref="GeneratorId"/> et <see cref="GeneratorState"/> sont sérialisés
    /// dans le .sq ; <see cref="RuntimeInstance"/> est marqué [JsonIgnore] — recréé au premier accès.
    /// Un vieux .sq sans ce type se charge sans crash (System.Text.Json ignore les JsonDerivedType
    /// absents à la sérialisation, et à la désérialisation un type inconnu jette explicitement
    /// — c'est POURQUOI on doit déclarer le JsonDerivedType sur FlowModule).
    ///
    /// **Backward-compat** : un fichier antérieur à cette fonctionnalité n'a AUCUN module de ce
    /// type — la liste de JsonDerivedType côté FlowModule s'étend, mais les fichiers historiques
    /// ne référencent aucun discriminateur "KotonGenerator", donc rien à faire. À l'inverse, un
    /// .sq écrit avec cette version qui serait ouvert dans une version antérieure verrait le
    /// désérialiseur jeter — comportement acceptable en bêta (l'utilisateur ne downgrade pas).
    /// </summary>
    public class KotonGeneratorModule : FlowModule
    {
        string generatorId;
        byte[] generatorState;
        double durationBeats = 4;

        /// <summary>Id stable du plugin (<see cref="IKotonPlugin.Id"/>). Sert à retrouver le plugin
        /// au chargement du projet (via le registre <see cref="Engine.Timeline.Effects.KotonPluginRegistry"/>).
        /// Un id inconnu (plugin supprimé du dossier <c>plugins/</c>) = le module ne produit rien
        /// (piste silencieuse sur cette portion), l'UI affiche un badge d'avertissement.</summary>
        public string GeneratorId
        {
            get { return generatorId; }
            set { if (generatorId != value) { generatorId = value; OnChanged(nameof(GeneratorId)); } }
        }

        /// <summary>Blob d'état sérialisé retourné par <see cref="IKotonPlugin.SaveState"/> — format
        /// opaque, l'hôte ne le lit jamais. Restauré au chargement du projet via
        /// <see cref="IKotonPlugin.LoadState"/>. Peut être <c>null</c> pour un module fraîchement
        /// créé (le plugin utilise ses valeurs par défaut).</summary>
        public byte[] GeneratorState
        {
            get { return generatorState; }
            set { generatorState = value; OnChanged(nameof(GeneratorState)); }
        }

        /// <summary>Durée du bloc en beats (temps). Modifiable par l'utilisateur : soit en
        /// redimensionnant la vignette dans la timeline, soit via un paramètre dans l'éditeur du
        /// plugin. Le générateur est censé rester musical à toute durée (un arpège de 4 beats devient
        /// un arpège de 16 beats sans reprogrammation).</summary>
        public double DurationBeats
        {
            get { return durationBeats; }
            set { double v = Math.Max(0.25, value); if (durationBeats != v) { durationBeats = v; OnChanged(nameof(DurationBeats)); } }
        }

        /// <summary>Instance VIVANTE du générateur (créée à la 1re utilisation par le player /
        /// éditeur). Non-sérialisée — au chargement du .sq elle est nulle, la 1re demande la crée
        /// via <c>KotonPluginRegistry.InstantiateGenerator(GeneratorId)</c> puis applique
        /// <see cref="GeneratorState"/>. Partagée entre le player (RenderNotes) et l'éditeur pour
        /// que bouger un slider dans l'éditeur affecte immédiatement le prochain flatten audio.</summary>
        [JsonIgnore] public IKotonGenerator RuntimeInstance { get; set; }

        [JsonIgnore] public override string Title => "Générateur Koton";
    }
}
