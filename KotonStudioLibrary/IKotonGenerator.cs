using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace KotonStudio.Library
{
    /// <summary>Catégorie fonctionnelle d'un générateur — sert au filtrage du menu « Insérer » de
    /// l'hôte selon le type de piste (une piste batterie ne propose que Drum/Percussion, une piste
    /// mélodique ne propose que Melody/Bass/Chord, etc.).</summary>
    public enum KotonGeneratorType
    {
        Melody,        // ligne mélodique (arpège, séquence, riff généré)
        Drum,          // pattern de batterie (grille GM percussion)
        Chord,         // accords / accompagnement (peut publier son harmonie via IKotonChordSource)
        Bass,          // ligne de basse (motorik, walking, pédale) — piste mélodique
        Percussion,    // percussions non-drum-kit (ethniques, hand-drums) — piste batterie
        Other,         // fourre-tout (générateur expérimental) — accepté sur toute piste
    }

    /// <summary>
    /// Marque une classe comme générateur Koton découvrable. Mêmes règles que
    /// <see cref="KotonInstrumentAttribute"/> : constructeur public sans paramètre, classe qui
    /// implémente <see cref="IKotonGenerator"/>, assembly <c>.ksl</c> dans le dossier <c>plugins/</c>.
    ///
    /// <see cref="DisplayName"/> apparaît dans le sous-menu de sélection ; <see cref="Type"/> sert au
    /// filtrage par type de piste (l'hôte n'affiche pas un générateur de batterie sur une piste
    /// mélodique). <see cref="Category"/> reste réservé (v1 = aplati par Type).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class KotonGeneratorAttribute : Attribute
    {
        public string DisplayName { get; }
        public KotonGeneratorType Type { get; set; } = KotonGeneratorType.Melody;
        public string Category { get; set; } = "";
        public string Version { get; set; } = "1.0";
        public string Vendor { get; set; } = "";

        public KotonGeneratorAttribute(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                throw new ArgumentException("displayName required", nameof(displayName));
            DisplayName = displayName;
        }
    }

    /// <summary>
    /// Générateur natif Koton : ne rend pas d'audio directement (contrairement à
    /// <see cref="IKotonInstrument"/>) mais PRODUIT DES NOTES MIDI que l'hôte injecte dans le synthé
    /// de la piste où le générateur est posé. Un module timeline (<c>KotonGeneratorModule</c>) porte
    /// une instance vivante du générateur ; le player appelle <see cref="RenderNotes"/> au moment du
    /// flatten pour obtenir la liste d'événements MIDI relative au début du bloc.
    ///
    /// **Cycle** : construit par <c>KotonPluginRegistry.InstantiateGenerator</c> (constructeur sans
    /// paramètre requis), paramètres exposés via <see cref="IKotonPlugin.Parameters"/>, éditeur
    /// (<c>UserControl</c> WPF) affiché dans le panneau du bas de la timeline. <see cref="RenderNotes"/>
    /// peut être appelé plusieurs fois par bloc (chaque re-flatten) et depuis le thread de rendu
    /// audio — le plugin doit être thread-safe (lecture des paramètres cohérente, pas de mutation
    /// d'un état qui pourrait être lu depuis le thread UI en parallèle).
    ///
    /// **Live edit** : bouger un slider pendant la lecture appelle <see cref="RenderNotes"/> au
    /// prochain re-flatten du LookaheadBuffer (~200 ms typique) — comme les générateurs internes
    /// (PatternGenerator, DrumPattern).
    ///
    /// **Harmonie** : un générateur mélodique peut lire l'accord actif au beat courant via
    /// <see cref="KotonHost.GetChordAt"/> pour rester harmonique-cohérent. Un générateur de type
    /// <see cref="KotonGeneratorType.Chord"/> qui publie SON harmonie (pour que d'autres pistes la
    /// suivent) doit implémenter <see cref="IKotonChordSource"/>.
    ///
    /// **Preview** : l'éditeur du générateur peut appeler <see cref="KotonHost.PreviewNotes"/> pour
    /// entendre le rendu SANS lancer la timeline. L'hôte route vers le synthé de la piste où
    /// l'éditeur est ouvert.
    /// </summary>
    public interface IKotonGenerator : IKotonPlugin
    {
        /// <summary>Catégorie fonctionnelle — doit correspondre à l'attribut
        /// <see cref="KotonGeneratorAttribute.Type"/> apposé sur la classe. Redondance volontaire : le
        /// registre lit l'attribut sans instancier, l'hôte lit la property une fois l'instance créée
        /// (le plugin peut vouloir varier le type dynamiquement, mais v1 = statique = valeur d'attribut).</summary>
        KotonGeneratorType GeneratorType { get; }

        /// <summary>Durée du bloc généré en beats (quarter notes). Peut être modifiée par
        /// l'utilisateur : soit en redimensionnant la vignette dans la timeline, soit via un paramètre
        /// dans l'éditeur du plugin. Le générateur doit rester cohérent quelle que soit la durée
        /// posée — un arpège de 4 beats devient un arpège de 8 beats sans re-programmation.</summary>
        double DurationBeats { get; set; }

        /// <summary>Rendu visuel de la vignette timeline : couleur de fond + texte affiché. Appelé par
        /// l'hôte à chaque re-render de la timeline — doit être rapide (pas de génération complète des
        /// notes). Un plugin qui n'a rien de spécifique peut retourner une valeur neutre ; l'hôte
        /// applique le style de vignette standard (bord, sélection, opacité) par-dessus.</summary>
        KotonGeneratorDisplay GetTimelineDisplay();

        /// <summary>Retourne les notes actives dans l'intervalle [<paramref name="startBeat"/>,
        /// <paramref name="endBeat"/>[ en beats, relatif au début du bloc (0-based). L'hôte convertit
        /// chaque <see cref="KotonGeneratedNote"/> en événements note-on / note-off à la bonne slice.
        ///
        /// **Contrat** : les notes retournées peuvent commencer AVANT startBeat (si l'engagement est
        /// long) mais leur début effectif est à <c>StartBeat</c> — l'hôte filtre implicitement.
        /// L'itération est paresseuse : le plugin peut yield return au fur et à mesure.
        ///
        /// **Contexte** : <paramref name="ctx"/> porte la tonalité et le tempo du projet, pour les
        /// générateurs qui en ont besoin (ex. un arpégiateur peut choisir sa direction selon le mode).
        /// Un générateur qui n'en a rien à faire peut ignorer <paramref name="ctx"/>.</summary>
        IEnumerable<KotonGeneratedNote> RenderNotes(double startBeat, double endBeat, KotonRenderContext ctx);
    }

    /// <summary>Rendu visuel d'un bloc générateur dans la timeline (retourné par
    /// <see cref="IKotonGenerator.GetTimelineDisplay"/>). L'hôte peint le bloc avec
    /// <see cref="Background"/> et affiche <see cref="Text"/> dessus, en gardant le chrome standard
    /// (bord de sélection, opacité de ghost, thumbnail par-dessus si posé plus tard).</summary>
    public struct KotonGeneratorDisplay
    {
        /// <summary>Couleur de fond de la vignette — l'hôte l'utilise telle quelle (pas d'écrasement
        /// par la palette d'instrument). Reste raisonnable en saturation pour cohabiter avec les
        /// autres vignettes.</summary>
        public Color Background;

        /// <summary>Étiquette affichée dans la vignette (nom court + un ou deux paramètres clés).
        /// L'hôte n'ajoute pas d'auto-suffixe (durée / temps), le plugin est libre de mettre exactement
        /// ce qu'il veut voir.</summary>
        public string Text;
    }

    /// <summary>Une note MIDI produite par un générateur, exprimée en beats et en position RELATIVE
    /// au début du bloc générateur (0 = début du bloc). L'hôte convertit en événement note-on /
    /// note-off sur la piste où le bloc est posé, avec la vélocité fournie.</summary>
    public struct KotonGeneratedNote
    {
        /// <summary>Position de départ en beats, RELATIVE au début du bloc générateur.</summary>
        public double StartBeat;

        /// <summary>Durée en beats (note-on jusqu'à note-off). Une valeur ≤ 0 est traitée comme une
        /// note ultra-courte (staccato) — pas de silence.</summary>
        public double DurationBeats;

        /// <summary>Note MIDI 0..127 (60 = C4). Hors plage = ignorée silencieusement par l'hôte.</summary>
        public int MidiNote;

        /// <summary>Vélocité MIDI 1..127 (0 est un note-off en MIDI standard — remplacé par 1 côté
        /// hôte s'il arrive). Le player peut appliquer par-dessus une correction d'accent métrique.</summary>
        public int Velocity;
    }

    /// <summary>Contexte de rendu passé à <see cref="IKotonGenerator.RenderNotes"/> : tonalité,
    /// tempo, signature temporelle du projet à l'instant du bloc. Suffit à la plupart des générateurs
    /// harmoniquement-conscients ; ceux qui ont besoin de l'accord courant utilisent
    /// <see cref="KotonHost.GetChordAt"/>.</summary>
    public class KotonRenderContext
    {
        /// <summary>Tonique du projet, pitch class 0..11 (C=0). Utile aux générateurs modaux ou de
        /// basse pédale (qui peuvent choisir de rester sur la tonique quand aucun accord n'est actif).</summary>
        public int Tonic;

        /// <summary>Mode du projet — true = majeur, false = mineur. Un générateur peut décider de son
        /// palette de degrés selon le mode.</summary>
        public bool IsMajor;

        /// <summary>Tempo en BPM à l'instant du bloc. Un générateur qui prend en compte la vitesse
        /// (ex. un arpégiateur qui limite son rythme au-dessus d'un certain BPM) le lit ici.</summary>
        public double Tempo;

        /// <summary>Numérateur de la signature temporelle du projet (4 pour 4/4, 6 pour 6/8...).</summary>
        public int TimeSigNum;

        /// <summary>Dénominateur de la signature temporelle (4 ou 8 dans la pratique).</summary>
        public int TimeSigDen;
    }
}
