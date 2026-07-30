using System;

namespace KotonStudio.Library
{
    /// <summary>
    /// Effet d'insert natif Koton (traite un buffer stéréo en place — équivalent d'un plugin VST d'effet).
    /// Chaîné après un instrument (Koton natif ou VSTi), avant le mixeur master.
    ///
    /// **Cycle** : <see cref="Prepare"/> une fois avant la première Process avec le sample rate et un
    /// max block size (block size effectif <= max). <see cref="Reset"/> vide les buffers internes (délais,
    /// reverbs, filtres) — appelé au Stop et sur boucle.
    ///
    /// **Process** : traite les deux canaux EN PLACE (lit et réécrit dans left/right). Un effet qui ne
    /// fait rien doit être un no-op (l'hôte peut lui-même faire le bypass via un flag externe, mais
    /// un plugin bien élevé garantit l'idempotence quand ses paramètres sont neutres).
    ///
    /// **Politique d'échec** : jamais jeter — un plugin qui dégénère doit passer en bypass (laisser
    /// left/right inchangés) et attendre un Reset.
    ///
    /// **v1** : aucun effet natif n'est livré (les 4 effets internes Koton — biquad EQ, compresseur,
    /// délai, saturation — restent en C# pur). L'interface existe pour qu'un futur plugin puisse
    /// s'ajouter sans changement côté hôte.
    /// </summary>
    public interface IKotonEffect : IKotonPlugin
    {
        /// <summary>Configure l'effet pour la fréquence d'échantillonnage cible et une taille de bloc
        /// maximale.</summary>
        void Prepare(int sampleRate, int maxBlockSize);

        /// <summary>Vide les buffers internes (delays, filtres, reverb tails). Appelé au Stop et sur
        /// retour de boucle.</summary>
        void Reset();

        /// <summary>Traite les deux canaux stéréo EN PLACE. Longueurs identiques. Ne pas jeter — bypass
        /// silent en cas de problème (laisser tel quel).</summary>
        void Process(Span<float> left, Span<float> right);
    }
}
