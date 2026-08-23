using System;
using System.Collections.Generic;

namespace KotonStudio.Library
{
    /// <summary>
    /// Marque une classe comme constrainer de notes Koton découvrable. Mêmes règles que les autres
    /// attributs Koton : constructeur public sans paramètre, classe qui implémente
    /// <see cref="IKotonGeneratorConstrainer"/>, assembly <c>.ksl</c> dans <c>plugins/</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class KotonGeneratorConstrainerAttribute : Attribute
    {
        public string DisplayName { get; }
        /// <summary>Id stable optionnel — cf. <see cref="KotonInstrumentAttribute.Id"/>.</summary>
        public string Id { get; set; }
        public string Category { get; set; } = "";
        public string Version { get; set; } = "1.0";
        public string Vendor { get; set; } = "";

        public KotonGeneratorConstrainerAttribute(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                throw new ArgumentException("displayName required", nameof(displayName));
            DisplayName = displayName;
        }
    }

    /// <summary>
    /// Constrainer natif Koton : filtre/modifie une séquence de notes MIDI avant qu'elles
    /// n'atteignent l'instrument. Cas d'usage typiques :
    ///   - Contraintes de jouabilité (guqin, harpe, guitare classique — max N doigts, empan max)
    ///   - Swing / quantification / humanisation
    ///   - Transposition d'octave, snap gamme/tonalité, harmonisation
    ///   - Glissando via pitch-bend automation (2 notes voisines en legato → slide continu)
    ///   - Filtres MIDI programmables (voice-leading, arpéger à la volée)
    ///
    /// **Placement architecture** : vit sur la <c>TimelineTrack</c> (lane), pas sur le module ni
    /// sur l'instrument. Ordre du flux : <c>Module → chaîne de constrainers → Instrument</c>.
    /// La chaîne est appliquée de manière IDENTIQUE par le player, le renderer de partition et
    /// l'exporteur MIDI — pour que l'audio, la partition et l'export restent parfaitement en phase.
    ///
    /// **Pureté** : <see cref="Filter"/> doit être une fonction pure (mêmes notes + même contexte
    /// → même sortie). Un constrainer peut porter de l'état visuel (l'éditeur du plugin peut
    /// observer via un event exposé sur la classe concrète), mais Filter ne doit pas dépendre d'un
    /// historique invisible ni de temps réel — sinon la partition et l'audio divergeraient.
    ///
    /// **Ordre et cardinalité** : une piste peut avoir 0, 1 ou N constrainers, appliqués dans
    /// l'ordre de la chaîne (chaque constrainer voit la sortie du précédent). Un constrainer qui
    /// n'a rien à faire (paramètres neutres) doit renvoyer la séquence telle quelle.
    ///
    /// **Automation** : un constrainer peut ÉGALEMENT émettre des points d'automation (typiquement
    /// pitch-bend pour un glissando, mais aussi vélocité continue, mod-wheel, etc.) via
    /// <paramref name="ctx"/> — voir <see cref="KotonRenderContext"/> pour l'API d'écriture
    /// (introduite en phase B du système constrainer, absente en v1 = un constrainer qui essaie
    /// aura simplement un no-op). Ces automations sont posées sur la LANE de la piste et consommées
    /// naturellement par l'instrument (Koton natif comme VST) au moment du rendu audio.
    /// </summary>
    public interface IKotonGeneratorConstrainer : IKotonPlugin
    {
        /// <summary>Applique le constrainer à une séquence de notes. Ordre d'entrée recommandé =
        /// tri par <c>StartBeat</c> croissant (le player le garantit), mais un constrainer bien
        /// élevé re-trie si besoin. Retourne la séquence transformée — potentiellement plus courte
        /// (notes rejetées), plus longue (notes ajoutées), ou de même taille (pitch/durée modifiés).
        ///
        /// <paramref name="ctx"/> porte tonalité, tempo, signature, position absolue du bloc (utile
        /// aux constrainers qui interrogent <see cref="KotonHost.GetChordAt"/> pour un choix
        /// harmonique-conscient), et — en phase B — l'API d'écriture d'automation.</summary>
        IEnumerable<KotonGeneratedNote> Filter(IEnumerable<KotonGeneratedNote> notes, KotonRenderContext ctx);
    }
}
