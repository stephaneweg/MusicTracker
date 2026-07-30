using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace KotonStudio.Library
{
    /// <summary>
    /// Contrat commun à tout plugin Koton natif — instrument (voir <see cref="IKotonInstrument"/>) ou effet
    /// d'insert (voir <see cref="IKotonEffect"/>). Un plugin Koton = une classe C# dans une DLL renommée
    /// <c>.ksl</c> (Koton Studio Library), déposée dans le dossier <c>plugins/</c> à côté de l'exécutable
    /// hôte. La découverte se fait par réflexion sur les attributs <see cref="KotonInstrumentAttribute"/> /
    /// <see cref="KotonEffectAttribute"/> apposés sur la classe.
    ///
    /// **Cycle de vie** : instanciation par l'hôte (constructeur sans paramètre requis), utilisation via
    /// l'interface spécialisée, puis <see cref="IDisposable.Dispose"/>. Les paramètres exposés via
    /// <see cref="Parameters"/> sont partagés par référence — l'hôte peut les lire et l'éditeur du plugin
    /// les modifie via <see cref="KotonParameter.Value"/>.
    ///
    /// **Persistance** : <see cref="SaveState"/> retourne un blob opaque (JSON UTF-8 recommandé), rangé
    /// dans le projet Koton et rendu au chargement via <see cref="LoadState"/>. Le format est libre côté
    /// plugin — l'hôte ne le lit jamais.
    ///
    /// **Éditeur** : si <see cref="HasEditor"/> est vrai, <see cref="CreateEditor"/> retourne un
    /// <c>UserControl</c> WPF embarquable dans une fenêtre hôte fournie par Koton (chrome standard).
    /// Un plugin peut retourner <c>false</c> et laisser l'hôte afficher un éditeur générique basé sur
    /// <see cref="Parameters"/> (non implémenté v1 — l'hôte affiche juste "pas d'éditeur").
    /// </summary>
    public interface IKotonPlugin : IDisposable
    {
        /// <summary>Identifiant stable inter-versions (ex. "koton.fm"). Utilisé pour retrouver le plugin
        /// au chargement d'un projet Koton. Deux plugins de même Id dans <c>plugins/</c> = comportement
        /// indéfini (l'hôte garde le premier chargé). Convention: éditeur.nom en snake case.</summary>
        string Id { get; }

        /// <summary>Nom affichable dans les menus et la barre de titre de l'éditeur. Peut différer par
        /// version (l'hôte s'appuie sur <see cref="Id"/> pour la persistance).</summary>
        string DisplayName { get; }

        /// <summary>Paramètres exposés du plugin, dans l'ordre d'affichage recommandé. La liste est
        /// STATIQUE pour la durée de vie du plugin : ajouter/retirer un paramètre à la volée n'est pas
        /// supporté. L'hôte peut lire/écrire <see cref="KotonParameter.Value"/> ; l'événement
        /// <see cref="KotonParameter.Changed"/> permet à l'éditeur de suivre les changements posés par
        /// une automation externe.</summary>
        IReadOnlyList<KotonParameter> Parameters { get; }

        /// <summary>Sérialise l'état actuel du plugin (valeurs de paramètres + tout état interne : preset
        /// sélectionné, LFO phase à préserver, etc.) en tableau d'octets. Format libre — l'hôte ne le
        /// désérialise jamais. Doit être robuste à un plugin détruit (retourner un blob vide si rien à
        /// sauver plutôt que jeter).</summary>
        byte[] SaveState();

        /// <summary>Restaure l'état sérialisé par <see cref="SaveState"/> — potentiellement d'une VERSION
        /// PRÉCÉDENTE du plugin. Doit dégrader gracieusement (ignorer les champs inconnus, garder les
        /// défauts sur les champs manquants) et ne PAS jeter — un blob corrompu doit laisser le plugin
        /// dans son état par défaut, pas casser le chargement du projet.</summary>
        void LoadState(byte[] state);

        /// <summary>Vrai si le plugin fournit une GUI custom via <see cref="CreateEditor"/>. Faux =
        /// l'hôte n'ouvre pas de fenêtre d'édition (v1 — un éditeur générique de sliders pourra être
        /// ajouté plus tard).</summary>
        bool HasEditor { get; }

        /// <summary>Crée un <c>UserControl</c> WPF représentant la GUI du plugin. L'hôte l'embarque dans
        /// une fenêtre Koton standard (dark chrome + boutons Koton). Le contrôle doit se comporter comme
        /// un contrôle WPF normal (Loaded/Unloaded, Dispatcher, DataContext libre — le plugin peut binder
        /// sur ses propres KotonParameter en 2-way).
        ///
        /// Peut être appelé plusieurs fois (l'utilisateur ferme puis rouvre la fenêtre) : à chaque appel
        /// le plugin retourne une INSTANCE FRAÎCHE — l'hôte ne mémorise pas.</summary>
        UserControl CreateEditor();
    }
}
