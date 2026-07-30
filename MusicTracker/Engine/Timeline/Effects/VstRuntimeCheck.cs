using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Vérifie la présence du runtime Visual C++ 2012 (msvcr110.dll / msvcp110.dll), requis par la DLL
    /// C++/CLI <c>Jacobi.Vst.Interop.dll</c>. On teste via <see cref="LoadLibrary"/> sur <c>msvcr110.dll</c>
    /// (Windows la cherche dans le PATH système, dossier de l'exe, System32). Si absent, l'UI propose un
    /// lien de téléchargement — sans bundler le redist dans l'installeur (trop lourd, souvent déjà présent).
    /// </summary>
    public static class VstRuntimeCheck
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FreeLibrary(IntPtr hModule);

        /// <summary>URL officielle Microsoft pour télécharger le redist VC++ 2012 (x86+x64).</summary>
        public const string VcRedistDownloadUrl = "https://www.microsoft.com/en-us/download/details.aspx?id=30679";

        static bool? _cached;

        /// <summary>Retourne <c>true</c> si <c>msvcr110.dll</c> peut être chargé. Résultat mis en cache — le runtime ne s'installe pas en cours d'exécution.</summary>
        public static bool IsVcRedistInstalled()
        {
            if (_cached.HasValue) return _cached.Value;
            var h = LoadLibrary("msvcr110.dll");
            if (h != IntPtr.Zero)
            {
                FreeLibrary(h);
                _cached = true;
                return true;
            }
            // Filet supplémentaire : certaines installs mettent le runtime dans System32 mais pas dans SysWOW64 (ou l'inverse).
            // On teste un chemin explicite en 64-bit et 32-bit.
            var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var sysWow = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
            if ((!string.IsNullOrEmpty(sys32) && File.Exists(Path.Combine(sys32, "msvcr110.dll")))
             || (!string.IsNullOrEmpty(sysWow) && File.Exists(Path.Combine(sysWow, "msvcr110.dll"))))
            {
                _cached = true;
                return true;
            }
            _cached = false;
            return false;
        }
    }
}
