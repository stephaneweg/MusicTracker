using System;
using System.Collections.Generic;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Un effet audio d'insert : traite un buffer stéréo (float, entrelacé en deux tableaux L/R) sur place.
    /// Les paramètres se sérialisent en <see cref="Dictionary{TKey, TValue}"/> nom→valeur — donc lisible par
    /// <see cref="System.Text.Json"/> sans configuration polymorphique. La chaîne d'inserts d'une piste est
    /// évaluée en série (l'ordre = le trajet du signal), instanciée au démarrage de la lecture par
    /// <see cref="EffectFactory.Create(TrackEffectData, int)"/>.
    /// </summary>
    public interface IAudioEffect
    {
        /// <summary>Identifiant stable du type d'effet ("eq", "comp", "delay", "sat"). Sert à la reconstruction depuis les données sérialisées.</summary>
        string Kind { get; }

        /// <summary>Traite <paramref name="frames"/> échantillons dans <paramref name="left"/>/<paramref name="right"/> en place.</summary>
        void Process(float[] left, float[] right, int frames);

        /// <summary>Vide les buffers internes (delays, filtres, enveloppes) — à appeler à chaque Start pour ne pas rejouer une queue.</summary>
        void Reset();

        /// <summary>Sérialise les paramètres. Le dictionnaire est plat, non typé, uniquement des <see cref="double"/> — pour rester compatible avec le format .sq (System.Text.Json + IncludeFields).</summary>
        Dictionary<string, double> Save();

        /// <summary>Recharge les paramètres depuis une carte produite par <see cref="Save"/>. Les clés manquantes gardent la valeur par défaut (permet d'ajouter un paramètre sans casser les vieux .sq).</summary>
        void Load(Dictionary<string, double> data);

        /// <summary>
        /// Sérialise un état interne opaque (blob binaire → base64) qui ne rentre pas dans le format
        /// plat de <see cref="Save"/>. Utilisé par <see cref="VstEffect"/> pour transporter le chunk
        /// natif du plugin (banque de presets, courbes IR, etc.) dans <see cref="TrackEffectData.StateBlob"/>.
        /// Les 4 effets maison retournent <c>null</c> — leur état tient entièrement dans <see cref="Save"/>.
        /// </summary>
        string SaveState();

        /// <summary>
        /// Symétrique de <see cref="SaveState"/> : reçoit le blob relu depuis <see cref="TrackEffectData.StateBlob"/>
        /// (ou <c>null</c> si le .sq est antérieur au champ). Les 4 effets maison ignorent l'appel.
        /// </summary>
        void LoadState(string state);
    }
}
