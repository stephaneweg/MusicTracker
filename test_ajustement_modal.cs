// Unit test for the AJUSTEMENT MODAL realization (GhibliComposer.ModalStep), verified by reflection.
// INVARIANT: for every chord type, every inversion, and a degree series 1..N over two octaves, a degree that is a
// CHORD TONE in root position must realize to a CHORD TONE of the chord in the inversion (shape kept). Expect 0 FAILURES.
//
// Run (from the repo root, after a Debug build, app closed):
//   $csc = "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe"
//   & $csc -nologo -target:exe -out:MusicTracker\bin\Debug\modaltest.exe -reference:MusicTracker\bin\Debug\MusicTracker.exe test_ajustement_modal.cs
//   Copy-Item MusicTracker\bin\Debug\MusicTracker.exe.config MusicTracker\bin\Debug\modaltest.exe.config -Force
//   MusicTracker\bin\Debug\modaltest.exe
using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using MusicTracker.Engine.Timeline;

class ModalTest
{
    static readonly string[] N = { "C","C#","D","D#","E","F","F#","G","G#","A","A#","B" };
    static int Pc(int x) { return ((x % 12) + 12) % 12; }
    static HashSet<int> scale = new HashSet<int> { 0, 2, 4, 5, 7, 9, 11 };   // C major
    static int ScaleStep(int midi, int dir) { int m = midi + dir, g = 0; while (!scale.Contains(Pc(m)) && g++ < 12) m += dir; return m; }
    static int ShiftScale(int midi, int steps) { int dir = steps >= 0 ? 1 : -1, n = Math.Abs(steps), m = midi; for (int i = 0; i < n; i++) m = ScaleStep(m, dir); return m; }
    static MethodInfo MS;
    static int ModalStep(int d, int root, int bass, HashSet<int> ct) { return (int)MS.Invoke(null, new object[] { d, root, bass, ct, scale }); }

    static int Main()
    {
        MS = typeof(GhibliComposer).GetMethod("ModalStep", BindingFlags.NonPublic | BindingFlags.Static);
        if (MS == null) { Console.WriteLine("ModalStep not found"); return 1; }
        var stacks = new (string name, List<int> s)[]{
            ("triade", new List<int>{0,4,7}), ("min", new List<int>{0,3,7}), ("dim", new List<int>{0,3,6}),
            ("7e", new List<int>{0,4,7,11}), ("dom7", new List<int>{0,4,7,10}),
            ("9e", new List<int>{0,4,7,11,2}), ("11e", new List<int>{0,4,7,11,2,5}), ("13e", new List<int>{0,4,7,11,2,5,9}),
            ("sus2", new List<int>{0,2,7}), ("sus4", new List<int>{0,5,7}),
        };
        int Nser = 16, totalFail = 0, totalChecks = 0;
        foreach (var (name, stack) in stacks)
        {
            var ct = new HashSet<int>(stack.Select(Pc));
            Console.WriteLine($"=== {name}  tones: {string.Join(" ", stack.Select(p => N[Pc(p)]))} ===");
            foreach (int bassPc in stack.Select(Pc))
            {
                int bassMidi = 48; while (Pc(bassMidi) != bassPc) bassMidi++;
                var line = new System.Text.StringBuilder(); int fails = 0;
                for (int d = 1; d <= Nser; d++)
                {
                    int rpPc = Pc(ShiftScale(60, d - 1));
                    bool isCtDeg = ct.Contains(rpPc);
                    int invPc = Pc(ShiftScale(bassMidi, ModalStep(d, 0, bassPc, ct)));
                    if (isCtDeg) { totalChecks++; bool ok = ct.Contains(invPc); if (!ok) { fails++; totalFail++; } line.Append(N[invPc]).Append(ok ? "* " : "!! "); }
                    else line.Append(N[invPc].ToLower()).Append(" ");
                }
                Console.WriteLine($"  bass {N[bassPc],-2}: {line}{(fails > 0 ? "  <-- " + fails + " FAIL" : "")}");
            }
        }
        Console.WriteLine($"\n{totalChecks} chord-tone-degree checks, {totalFail} FAILURES");
        Console.WriteLine(totalFail == 0 ? "ALL PASS" : "*** FAILURES ***");
        return totalFail == 0 ? 0 : 2;
    }
}
