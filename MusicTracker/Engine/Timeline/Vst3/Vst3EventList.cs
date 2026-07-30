using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MusicTracker.Engine.Timeline.Vst3.Interop;

namespace MusicTracker.Engine.Timeline.Vst3
{
    /// <summary>
    /// Implémentation managée d'<see cref="IEventList"/> : buffer FIFO d'<see cref="Vst3Event"/>. Le
    /// pipeline audio accumule ses note-on/off dans cette liste avant chaque appel <c>process()</c>, et
    /// la passe au plugin via <c>ProcessData.InputEvents</c>. Le CCW COM est récupéré à la demande via
    /// <see cref="GetComPtr"/> (une seule allocation, réutilisée buffer après buffer).
    ///
    /// **Non thread-safe intra-buffer** : un seul thread (audio) écrit puis remet à zéro. Les autres
    /// threads peuvent enqueuer via <see cref="AddSafe"/> qui prend un lock — l'audio flush ensuite
    /// sous ce même lock.
    /// </summary>
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class Vst3EventList : IEventList
    {
        readonly List<Vst3Event> _events = new List<Vst3Event>();
        readonly object _lock = new object();

        public int Count { get { lock (_lock) return _events.Count; } }

        /// <summary>Ajoute un event (typiquement depuis le thread audio, sans contention).</summary>
        public void AddSafe(Vst3Event e)
        {
            lock (_lock) _events.Add(e);
        }

        /// <summary>Vide la liste (à appeler après le <c>process()</c> pour ne pas rejouer les events).</summary>
        public void Clear()
        {
            lock (_lock) _events.Clear();
        }

        // ---- IEventList (appelé par le plugin) -------------------------------------------------------

        public int getEventCount()
        {
            lock (_lock) return _events.Count;
        }

        public int getEvent(int index, out Vst3Event e)
        {
            lock (_lock)
            {
                if (index < 0 || index >= _events.Count) { e = default; return Vst3Enums.kInvalidArgument; }
                e = _events[index];
                return Vst3Enums.kResultOk;
            }
        }

        public int addEvent(ref Vst3Event e)
        {
            // Un plugin qui produit des events (rare pour un VSTi) les ajouterait ici. Pour l'input list,
            // on tolère qu'il appelle addEvent (peu de plugins le font sur l'input) — l'events buffer est le nôtre.
            lock (_lock) _events.Add(e);
            return Vst3Enums.kResultOk;
        }
    }
}
