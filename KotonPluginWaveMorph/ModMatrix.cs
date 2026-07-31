using System;
using System.Collections.Generic;

namespace KotonPluginWaveMorph
{
    /// <summary>
    /// Source d'une modulation dans la mod matrix. Les 4 premières sont des ENVELOPPES/LFO produits
    /// par le voice (donc par-note) — leur valeur est calculée sample-par-sample dans le voice puis
    /// distribuée aux cibles via <see cref="ModMatrix"/>. Les 4 dernières sont des signaux MIDI/note :
    /// Velocity et Note sont figés au NoteOn ; Aftertouch et ModWheel sont mis à jour par MidiCC().
    /// </summary>
    public enum ModSource
    {
        Env2 = 0,
        Env3 = 1,
        Lfo1 = 2,
        Lfo2 = 3,
        Velocity = 4,
        Note = 5,
        Aftertouch = 6,
        ModWheel = 7,
    }

    /// <summary>
    /// Cible d'une modulation. Les cibles sont appliquées à des paramètres qui ont chacun leur unité
    /// naturelle (dB, cents, Hz, semitones). La classe <see cref="ModMatrix"/> retourne une SOMME
    /// pondérée normalisée dans [-1, +1] pour chaque cible ; le voice décide comment mapper à l'unité
    /// via une échelle spécifique (ex. XFade = +/-1 direct, Detune = +/-100 cents, Cutoff = +/-4 oct).
    ///
    /// Ordre stable pour la persistance JSON (l'index est le nom de l'enum, sérialisé en string).
    /// </summary>
    public enum ModTarget
    {
        XFade = 0,
        W1Amp = 1,
        W2Amp = 2,
        W1Detune = 3,
        W2Detune = 4,
        F1Freq = 5,
        F1Res = 6,
        F2Freq = 7,
        F2Res = 8,
        Amp = 9,
        Pan = 10,
    }

    /// <summary>
    /// Un lien de modulation : "Source X affecte Target Y avec un amount Z". Amount est dans [-1, +1]
    /// — le voice le multiplie ensuite par l'échelle propre à la cible.
    ///
    /// Struct par valeur pour éviter les allocations dans la liste. L'égalité est logique (mêmes
    /// source+target+amount = même slot) pour faciliter les tests, mais le plugin n'en dépend pas.
    /// </summary>
    public struct ModSlot : IEquatable<ModSlot>
    {
        public ModSource Src;
        public ModTarget Tgt;
        public float Amount;

        public ModSlot(ModSource src, ModTarget tgt, float amount)
        {
            Src = src; Tgt = tgt; Amount = amount;
        }

        public bool Equals(ModSlot other) =>
            Src == other.Src && Tgt == other.Tgt && Math.Abs(Amount - other.Amount) < 1e-6f;

        public override bool Equals(object obj) => obj is ModSlot ms && Equals(ms);
        public override int GetHashCode() => HashCode.Combine(Src, Tgt, Amount);
    }

    /// <summary>
    /// Container léger de <see cref="ModSlot"/> — pas de logique complexe, juste un accès rapide
    /// "quelles sources influencent cette cible". Le voice appelle <see cref="Evaluate"/> une fois
    /// par cible par sample avec les valeurs courantes de chaque source ; retourne la somme
    /// pondérée dans [-N, +N] (le voice clampe ensuite selon l'unité de la cible).
    ///
    /// **Thread-safety** : les slots peuvent être modifiés depuis l'UI pendant que le voice les lit
    /// depuis le thread audio. En pratique, la structure d'une List&lt;ModSlot&gt; peut être
    /// corrompue si un Add/Remove tombe entre 2 samples. Pour v1, l'éditeur reconstruit la liste
    /// entière à chaque édition via Assign() sous lock — c'est suffisant pour la fréquence
    /// d'édition (rare, sur clic utilisateur).
    /// </summary>
    public sealed class ModMatrix
    {
        readonly object _lock = new object();
        List<ModSlot> _slots = new List<ModSlot>();

        public int Count { get { lock (_lock) return _slots.Count; } }

        /// <summary>Copie défensive pour l'UI (renvoyer la List interne exposerait à une modif
        /// externe qui court-circuiterait le lock).</summary>
        public List<ModSlot> Snapshot()
        {
            lock (_lock) return new List<ModSlot>(_slots);
        }

        /// <summary>Remplace atomiquement la liste des slots. Utilisé par l'éditeur : reconstruit
        /// une nouvelle liste depuis son UI et la pousse en un coup — évite les états intermédiaires
        /// visibles du thread audio.</summary>
        public void Assign(IEnumerable<ModSlot> slots)
        {
            var list = new List<ModSlot>();
            if (slots != null)
                foreach (var s in slots) list.Add(s);
            lock (_lock) _slots = list;
        }

        /// <summary>Ajoute (ou met à jour l'amount d') un slot pour la paire (src, tgt).</summary>
        public void SetSlot(ModSource src, ModTarget tgt, float amount)
        {
            lock (_lock)
            {
                for (int i = 0; i < _slots.Count; i++)
                {
                    if (_slots[i].Src == src && _slots[i].Tgt == tgt)
                    {
                        if (Math.Abs(amount) < 1e-6f)
                        {
                            _slots.RemoveAt(i);
                            return;
                        }
                        _slots[i] = new ModSlot(src, tgt, amount);
                        return;
                    }
                }
                if (Math.Abs(amount) >= 1e-6f)
                    _slots.Add(new ModSlot(src, tgt, amount));
            }
        }

        public float GetAmount(ModSource src, ModTarget tgt)
        {
            lock (_lock)
            {
                for (int i = 0; i < _slots.Count; i++)
                    if (_slots[i].Src == src && _slots[i].Tgt == tgt) return _slots[i].Amount;
            }
            return 0f;
        }

        /// <summary>Somme pondérée des sources qui affectent <paramref name="tgt"/>. Les valeurs de
        /// source sont fournies par un tableau indexé par (int)ModSource. Le résultat est dans
        /// [-N, +N] où N = nombre de sources actives (typiquement 1-3 par cible) — le voice le
        /// mappe à l'unité de la cible via une échelle appropriée.</summary>
        public float Evaluate(ModTarget tgt, float[] sourceValues)
        {
            if (sourceValues == null) return 0f;
            float sum = 0f;
            // Lecture sans lock : dans la boucle audio, le pire cas est de lire la liste alors qu'un
            // Assign vient de la remplacer — on lit soit l'ancienne soit la nouvelle, jamais une
            // version corrompue (référence atomique en .NET). Une race sur les slots reste bénigne
            // (le prochain sample lira la bonne version).
            var slots = _slots;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Tgt != tgt) continue;
                int srcIdx = (int)slots[i].Src;
                if (srcIdx < 0 || srcIdx >= sourceValues.Length) continue;
                sum += sourceValues[srcIdx] * slots[i].Amount;
            }
            return sum;
        }

        /// <summary>Nombre d'entrées enum ModSource — dimensionne les tableaux "source values"
        /// du voice.</summary>
        public const int SourceCount = 8;

        /// <summary>Nombre d'entrées enum ModTarget — utile pour dimensionner un état pré-alloué
        /// dans le voice si besoin.</summary>
        public const int TargetCount = 11;

        public static readonly string[] SourceNames =
        {
            "Env 2", "Env 3", "LFO 1", "LFO 2", "Vel", "Note", "AT", "Mod",
        };

        public static readonly string[] TargetNames =
        {
            "X-Fade", "W1 Amp", "W2 Amp", "W1 Det", "W2 Det",
            "F1 Freq", "F1 Res", "F2 Freq", "F2 Res", "Amp", "Pan",
        };
    }
}
