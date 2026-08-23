using System;

namespace KotonStudio.Library
{
    /// <summary>
    /// Instrument (source sonore MIDI-driven) natif Koton. Reçoit note-on / note-off / CC / pitch bend
    /// et rend un buffer stéréo. C'est l'équivalent Koton d'un VSTi — même rôle dans la chaîne de rendu
    /// mais sans passer par le protocole VST natif.
    ///
    /// **Cycle** : l'hôte appelle <see cref="Prepare"/> une fois avant la lecture avec la fréquence
    /// d'échantillonnage et une taille de bloc MAXIMALE (les Render suivants peuvent être plus petits,
    /// jamais plus grands). <see cref="Reset"/> vide l'état actif (voix en cours, LFO phase) — appelé
    /// au Stop et sur boucle. Chaque <see cref="Render"/> reçoit deux Spans stéréo à remplir en place ;
    /// le plugin doit toujours écrire dans les deux (pas de "silent = skip" — l'hôte s'attend à ce que
    /// les canaux soient posés).
    ///
    /// **MIDI** : NoteOn/NoteOff/MidiCC/SetPitchBend peuvent arriver depuis le thread audio (typique)
    /// ou depuis l'UI (mode preview). Un plugin polyphonique doit être thread-safe entre NoteOn et
    /// Render — un lock simple sur la table de voix suffit pour la plupart des cas. Le
    /// <c>sampleOffset</c> indique combien de samples APRÈS le début du prochain Render l'événement
    /// prend effet (0 = début) ; un plugin qui ne gère pas le sample-accurate peut l'ignorer sans
    /// dommage audible (précision buffer, ~10 ms typique).
    ///
    /// **Politique d'échec** : jamais jeter depuis Render (le thread audio panique) — un plugin qui
    /// dégénère doit se mettre en mode silent (buffers vides) et attendre un Reset.
    /// </summary>
    public interface IKotonInstrument : IKotonPlugin
    {
        /// <summary>Configure le plugin pour la fréquence d'échantillonnage cible et une taille de bloc
        /// maximale (les Render suivants seront <= maxBlockSize). Appelé une seule fois avant la
        /// première lecture ; un changement de sample rate = nouvelle instance côté hôte.</summary>
        void Prepare(int sampleRate, int maxBlockSize);

        /// <summary>Éteint toutes les voix en cours, remet à zéro les états de phase (LFO, envelope).
        /// Appelé au Stop de la lecture, au retour de boucle, et sur "All Notes Off" (CC 123).</summary>
        void Reset();

        /// <summary>Déclenche une note. <paramref name="note"/> = numéro MIDI 0-127. <paramref name="velocity"/>
        /// = 1-127 (0 est un note-off en MIDI standard — l'hôte s'en occupe). <paramref name="sampleOffset"/>
        /// = position sample-accurate dans le prochain Render (0 = début).</summary>
        void NoteOn(int note, int velocity, int sampleOffset = 0);

        /// <summary>Termine la note (release de l'enveloppe). Un note-off sur une note qui ne joue pas
        /// est ignoré silencieusement.</summary>
        void NoteOff(int note, int sampleOffset = 0);

        /// <summary>Continuous Controller MIDI. Les CC classiques que l'hôte envoie : CC1 modulation,
        /// CC7 volume, CC10 pan, CC11 expression, CC64 sustain, CC91 reverb, CC93 chorus, CC123
        /// all-notes-off. Un plugin peut ignorer ceux qu'il ne comprend pas.</summary>
        void MidiCC(int cc, int value, int sampleOffset = 0);

        /// <summary>Pitch bend normalisé -1..+1 (0 = centre). L'amplitude en demi-tons est laissée au
        /// plugin (2 semitons est le défaut GM). Les CC RPN pour changer la range ne sont pas propagés v1.</summary>
        void SetPitchBend(float value, int sampleOffset = 0);

        /// <summary>Rend un bloc stéréo. <paramref name="left"/> et <paramref name="right"/> ont la même
        /// longueur (le nombre de frames à rendre). Le plugin ÉCRASE le contenu (pas d'ADD — l'hôte
        /// somme dans un master à côté). Un plugin silent doit poser des zéros, pas laisser tel quel.</summary>
        void Render(Span<float> left, Span<float> right);
    }
}
