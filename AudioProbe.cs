using System;
using NAudio.Wave;

public static class AudioProbe
{
    public static int Main(string[] args)
    {
        try { Console.WriteLine(Run(args[0])); return 0; }
        catch (Exception e) { Console.WriteLine("ERROR: " + e.GetType().Name + ": " + e.Message); return 1; }
    }

    static string Run(string path)
    {
        float[] mono; int sr;
        using (var r = new AudioFileReader(path))
        {
            sr = r.WaveFormat.SampleRate; int ch = r.WaveFormat.Channels;
            int want = sr * ch * 10;                 // first 10 s (interleaved)
            var buf = new float[want]; int got = r.Read(buf, 0, want);
            int n = got / ch; mono = new float[n];
            for (int i = 0; i < n; i++) { float s = 0; for (int c = 0; c < ch; c++) s += buf[i * ch + c]; mono[i] = s / ch; }
        }
        double[] chroma = new double[12]; double lowE = 0, highE = 0;
        for (int m = 48; m <= 83; m++)
        {
            double f = 440.0 * Math.Pow(2.0, (m - 69) / 12.0);
            double w = 2 * Math.PI * f / sr, coeff = 2 * Math.Cos(w);
            double s1 = 0, s2 = 0;
            for (int i = 0; i < mono.Length; i++) { double s0 = mono[i] + coeff * s1 - s2; s2 = s1; s1 = s0; }
            double power = s1 * s1 + s2 * s2 - coeff * s1 * s2; if (power < 0) power = 0;
            double mag = Math.Sqrt(power) / Math.Max(1, mono.Length);
            chroma[((m % 12) + 12) % 12] += mag;
            if (m <= 59) lowE += mag; else if (m >= 72) highE += mag;
        }
        double cmax = 1e-9; for (int i = 0; i < 12; i++) if (chroma[i] > cmax) cmax = chroma[i];
        for (int i = 0; i < 12; i++) chroma[i] /= cmax;
        double[] maj = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        double[] min = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
        double bestR = -2; int bestKey = 0; bool bestMin = false;
        for (int k = 0; k < 12; k++) { double rM = Corr(chroma, maj, k), rm = Corr(chroma, min, k); if (rM > bestR) { bestR = rM; bestKey = k; bestMin = false; } if (rm > bestR) { bestR = rm; bestKey = k; bestMin = true; } }
        string[] names = { "Do", "Do#", "Re", "Mib", "Mi", "Fa", "Fa#", "Sol", "Lab", "La", "Sib", "Si" };
        int t1 = 0, t2 = 0, t3 = 0; for (int i = 1; i < 12; i++) { if (chroma[i] > chroma[t1]) { t3 = t2; t2 = t1; t1 = i; } else if (chroma[i] > chroma[t2]) { t3 = t2; t2 = i; } else if (chroma[i] > chroma[t3]) { t3 = i; } }
        int win = sr; var rms = new System.Text.StringBuilder();
        for (int t = 0; t + win <= mono.Length && t < sr * 10; t += win) { double e = 0; for (int i = t; i < t + win; i++) e += mono[i] * mono[i]; rms.Append(Math.Round(Math.Sqrt(e / win), 3)).Append(' '); }
        int hop = 512; int frames = mono.Length / hop; var flux = new double[frames]; double prev = 0;
        for (int fI = 0; fI < frames; fI++) { double e = 0; for (int i = fI * hop; i < fI * hop + hop && i < mono.Length; i++) e += Math.Abs(mono[i]); double d = e - prev; flux[fI] = d > 0 ? d : 0; prev = e; }
        double fps = (double)sr / hop; int loLag = (int)(fps * 60.0 / 150.0), hiLag = (int)(fps * 60.0 / 50.0); double bestAc = -1; int bestLag = Math.Max(1, loLag);
        for (int lag = loLag; lag <= hiLag && lag < frames; lag++) { double ac = 0; for (int i = 0; i + lag < frames; i++) ac += flux[i] * flux[i + lag]; if (ac > bestAc) { bestAc = ac; bestLag = lag; } }
        double bpm = 60.0 * fps / bestLag;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("sampleRate=" + sr + "  duration~=" + Math.Round(mono.Length / (double)sr, 1) + "s");
        sb.AppendLine("KEY = " + names[bestKey] + (bestMin ? " mineur" : " majeur") + "   (corr " + Math.Round(bestR, 2) + ")");
        sb.AppendLine("top pitch-classes = " + names[t1] + ", " + names[t2] + ", " + names[t3]);
        sb.Append("chroma: "); for (int i = 0; i < 12; i++) sb.Append(names[i] + "=" + Math.Round(chroma[i], 2) + " "); sb.AppendLine();
        sb.AppendLine("register: low(<=B3)=" + Math.Round(lowE, 3) + "  high(>=C5)=" + Math.Round(highE, 3) + "  => " + (highE > lowE ? "plutot AIGU/brillant" : "plutot GRAVE/sombre"));
        sb.AppendLine("tempo ~ " + Math.Round(bpm) + " BPM (approx, onset-autocorr)");
        sb.AppendLine("RMS/s = " + rms.ToString());
        return sb.ToString();
    }

    static double Corr(double[] x, double[] prof, int rot)
    {
        double mx = 0, mp = 0; for (int i = 0; i < 12; i++) { mx += x[i]; mp += prof[i]; } mx /= 12; mp /= 12;
        double num = 0, dx = 0, dp = 0; for (int i = 0; i < 12; i++) { double a = x[i] - mx, b = prof[(i + 12 - rot) % 12] - mp; num += a * b; dx += a * a; dp += b * b; }
        return num / Math.Sqrt(dx * dp + 1e-12);
    }
}
