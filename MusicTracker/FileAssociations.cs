using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MusicTracker
{
    /// <summary>
    /// Registers MusicTracker's file types in the per-user registry (HKCU — no admin needed) so a
    /// supported file opens the app on double-click. The app's own format (.sq, the timeline) becomes the
    /// default handler; shared import formats (.mid/.mscz/.mscx) are only added to the "Open with"
    /// list so we don't steal their system default. Best-effort: failures never block startup.
    /// </summary>
    public static class FileAssociations
    {
        const string ProgId = "MusicTracker.Music";
        static readonly string[] OwnedExtensions = { ".sq" }; // .sq = Timeline (the app's native format)
        static readonly string[] SharedExtensions = { ".mid", ".midi", ".mscz", ".mscx" };

        const int SHCNE_ASSOCCHANGED = 0x08000000;

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

        public static void EnsureRegistered()
        {
            try
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                bool changed = false;

                using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
                {
                    // The ProgId: display name, icon and the launch command.
                    using (var prog = classes.CreateSubKey(ProgId))
                    {
                        prog.SetValue(null, "MusicTracker — Musique");
                        using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue(null, "\"" + exe + "\",0");
                        using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, "\"" + exe + "\" \"%1\"");
                    }

                    // Own formats -> become the default handler.
                    foreach (var ext in OwnedExtensions)
                    {
                        using (var ek = classes.CreateSubKey(ext))
                        {
                            if ((ek.GetValue(null) as string) != ProgId) { ek.SetValue(null, ProgId); changed = true; }
                            using (var ow = ek.CreateSubKey("OpenWithProgids")) ow.SetValue(ProgId, new byte[0], RegistryValueKind.None);
                        }
                    }

                    // Shared formats -> only offered in "Open with" (keep their existing default).
                    foreach (var ext in SharedExtensions)
                    {
                        using (var ek = classes.CreateSubKey(ext))
                        using (var ow = ek.CreateSubKey("OpenWithProgids"))
                        {
                            if (ow.GetValue(ProgId) == null) { ow.SetValue(ProgId, new byte[0], RegistryValueKind.None); changed = true; }
                        }
                    }
                }

                if (changed) SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch { /* association is best-effort; never block startup */ }
        }
    }
}
