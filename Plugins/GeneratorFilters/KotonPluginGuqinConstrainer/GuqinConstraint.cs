using System;
using System.Collections.Generic;

namespace KotonPluginGuqinConstrainer
{
    /// <summary>
    /// État « ce qui est tenu maintenant » sur le guqin virtuel. Copié depuis KotonPluginGuqinVirtuel.
    /// </summary>
    public sealed class GuqinConstraint
    {
        public enum Decision { Allow, StealOldest, RejectStringBusy }

        public sealed class Held
        {
            public int Midi;
            public int StringIdx;
            public double Position;
            public long StruckOrder;
            public bool IsOpen => Position <= 1e-6;
        }

        readonly List<Held> _held = new List<Held>();
        long _counter;

        public double DiapasonCm { get; set; } = 110.0;
        public double MaxSpanCm { get; set; } = 15.0;
        public int MaxStoppedFingers { get; set; } = 4;

        public IReadOnlyList<Held> Active => _held;

        public double PositionCm(double p) => p * DiapasonCm;

        public Decision Consider(int stringIdx, double position, out Held toRelease)
        {
            toRelease = null;
            for (int i = 0; i < _held.Count; i++)
            {
                if (_held[i].StringIdx == stringIdx)
                {
                    toRelease = _held[i];
                    return Decision.StealOldest;
                }
            }
            bool newIsOpen = position <= 1e-6;
            if (newIsOpen) return Decision.Allow;
            int stopped = 0;
            double minCm = PositionCm(position);
            double maxCm = minCm;
            foreach (var h in _held)
            {
                if (h.IsOpen) continue;
                stopped++;
                double c = PositionCm(h.Position);
                if (c < minCm) minCm = c;
                if (c > maxCm) maxCm = c;
            }
            bool spanOk = (maxCm - minCm) <= MaxSpanCm;
            if (stopped + 1 > MaxStoppedFingers || !spanOk)
            {
                Held oldest = null;
                foreach (var h in _held)
                    if (!h.IsOpen && (oldest == null || h.StruckOrder < oldest.StruckOrder))
                        oldest = h;
                if (oldest == null) return Decision.Allow;
                toRelease = oldest;
                return Decision.StealOldest;
            }
            return Decision.Allow;
        }

        public Held Register(int midi, int stringIdx, double position)
        {
            var h = new Held { Midi = midi, StringIdx = stringIdx, Position = position, StruckOrder = ++_counter };
            _held.Add(h);
            return h;
        }

        public void Release(int midi)
        {
            for (int i = _held.Count - 1; i >= 0; i--)
                if (_held[i].Midi == midi) { _held.RemoveAt(i); return; }
        }

        public void Release(Held h) => _held.Remove(h);
        public void Clear() => _held.Clear();
    }
}
