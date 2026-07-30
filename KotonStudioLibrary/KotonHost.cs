using System;
using System.Collections.Generic;

namespace KotonStudio.Library
{
    /// <summary>
    /// Point d'accès statique vers les services de Koton depuis un plugin. L'hôte injecte les
    /// callbacks au démarrage de l'application et à l'ouverture d'un projet ; les plugins les
    /// invoquent librement au moment du rendu ou depuis leur éditeur.
    ///
    /// **Pourquoi statique** : un plugin est instancié par réflexion dans une DLL séparée — passer
    /// une référence à un « host » à chaque instance demanderait soit une méthode d'injection dédiée
    /// (constructeur non conforme au contrat sans-paramètre), soit un ThreadStatic laid. Le statique
    /// est le pattern qui reste transparent côté auteur de plugin.
    ///
    /// **Thread-safety** : les callbacks peuvent être invoqués depuis le thread de rendu audio
    /// (<see cref="IKotonGenerator.RenderNotes"/>) OU depuis le thread UI (bouton Preview de
    /// l'éditeur). L'hôte rend les callbacks thread-safe côté implémentation ; côté plugin, aucune
    /// précaution particulière n'est requise en v1 (les callbacks eux-mêmes ne sont ni [ThreadStatic]
    /// ni verrouillés — l'hôte réassigne uniquement à l'ouverture / fermeture de projet, entre deux
    /// exécutions de RenderNotes / PreviewNotes).
    ///
    /// **Null-safety** : chaque callback est <c>null</c> avant que l'hôte ne l'ait câblé (ou après
    /// qu'il l'ait décâblé). Un plugin doit tester <c>if (KotonHost.GetChordAt != null)</c> avant
    /// d'appeler — pas d'exception si absent, juste un fallback silencieux (le comportement du plugin
    /// dégrade sans se casser, ex. un arpégiateur qui n'a pas d'accord joue sa dernière liste de
    /// notes ou se tait).
    /// </summary>
    public static class KotonHost
    {
        /// <summary>Renvoie l'accord actif au <paramref name="beat"/> donné (contexte projet courant),
        /// en position ABSOLUE depuis le début du morceau. L'hôte agrège la piste d'accords built-in
        /// et tous les blocs générateurs qui implémentent <see cref="IKotonChordSource"/>.
        /// <c>null</c> = pas d'accord / silence à ce beat.
        ///
        /// **Contexte usage** : un générateur mélodique, un arpégiateur, une basse peuvent lire cet
        /// accord à chaque note qu'ils produisent pour rester harmoniques-cohérents. Coût côté hôte =
        /// binary search dans les blocs d'accords → très bon marché à appeler dans une boucle de
        /// rendu.</summary>
        public static Func<double, KotonChord?> GetChordAt { get; set; }

        /// <summary>Preview de notes : Koton dispatche les <paramref name="notes"/> à travers le
        /// synthé de la piste où l'éditeur du générateur est actuellement OUVERT (routing implicite
        /// « éditeur ouvert = piste cible »). Fire-and-forget : la méthode retourne immédiatement, la
        /// diffusion est asynchrone (respecte les timings des notes). Le plugin n'a pas besoin de
        /// connaître le synthé, la piste ni le temps de rendu — juste appeler ce callback.
        ///
        /// **Cas d'usage** : bouton "▶ Preview" dans l'éditeur d'un générateur pour entendre le
        /// résultat sans lancer la timeline. Réappeler pendant qu'une preview joue déjà = annule la
        /// précédente et démarre la nouvelle (comportement DAW standard).</summary>
        public static Action<IEnumerable<KotonGeneratedNote>> PreviewNotes { get; set; }

        /// <summary>Coupe tout preview en cours (bouton "⏹ Stop" dans l'éditeur d'un générateur, ou
        /// avant de démarrer une lecture réelle). No-op si aucun preview n'est actif.</summary>
        public static Action StopPreview { get; set; }
    }
}
