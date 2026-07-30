using System;
using System.Runtime.InteropServices;
using MusicTracker.Engine.Timeline.Vst3.Interop;

namespace MusicTracker.Engine.Timeline.Vst3
{
    /// <summary>
    /// Helpers partagés entre <see cref="MusicTracker.Engine.Timeline.Effects.Vst3Effect"/> et
    /// <see cref="MusicTracker.Engine.Timeline.Vst3Instrument"/> pour le cycle de vie de l'éditeur —
    /// évite de dupliquer QueryInterface et appels COM opaques dans les deux hosters.
    /// </summary>
    public static class Vst3EditorHelpers
    {
        [DllImport("user32.dll")] static extern int GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int GetDpiForSystem();

        /// <summary>Négocie le scale HiDPI avec le plugin via <see cref="IPlugViewContentScaleSupport"/>.
        /// Beaucoup de plugins VST3 modernes (Surge XT, u-he, iZotope récents…) REFUSENT de peindre leur
        /// GUI tant qu'on ne leur a pas dit à quelle échelle rendre. À appeler AVANT <c>attached()</c>.
        /// No-op si le plugin n'implémente pas l'interface (plugin ancien) — parfaitement acceptable.</summary>
        public static void TrySetContentScaleFactor(IPlugView view, IntPtr parentHwnd)
        {
            if (view == null) return;

            // Récupère le vrai DPI de la fenêtre (ou du système en fallback). 96 = 100%.
            int dpi;
            try { dpi = parentHwnd != IntPtr.Zero ? GetDpiForWindow(parentHwnd) : GetDpiForSystem(); }
            catch { dpi = 96; }
            if (dpi <= 0) dpi = 96;
            float scale = dpi / 96f;

            // QI l'interface via le RCW puis appelle setContentScaleFactor. Un plugin qui ne l'implémente
            // pas rend GetComInterfaceForObject/marshaller.QueryInterface en HRESULT E_NOINTERFACE →
            // exception managée qu'on gobe.
            IPlugViewContentScaleSupport scaleIface = null;
            try { scaleIface = (IPlugViewContentScaleSupport)view; }
            catch { }
            if (scaleIface == null) return;

            try { scaleIface.setContentScaleFactor(scale); }
            catch { /* certains plugins renvoient kNotImplemented — on tolère */ }
        }
    }
}
