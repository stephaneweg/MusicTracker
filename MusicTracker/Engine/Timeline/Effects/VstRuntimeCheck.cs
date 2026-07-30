using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Vérifie la présence du runtime Visual C++ 2015+ (<c>vcruntime140.dll</c>) requis par la DLL
    /// C++/CLI <c>Jacobi.Vst.Host.Interop.dll</c> livrée par VST.NET 2.x. On teste via
    /// <see cref="LoadLibrary"/> (Windows la cherche dans le PATH système, dossier de l'exe, System32).
    /// Si absent, l'UI propose un lien de téléchargement — sans bundler le redist dans l'installeur
    /// (trop lourd, presque toujours déjà présent car exigé par .NET 10 lui-même).
    ///
    /// **Différence vs main (VST.NET 1.x)** : l'ancien fork TruDan compilait son Interop.dll contre le
    /// VC++ 2012 runtime (msvcr110.dll). La version 2.x moderne (obiwanjacobi) est compilée contre
    /// VC++ 2015+ (vcruntime140.dll) — on met à jour la sonde et l'URL de téléchargement en conséquence.
    /// </summary>
    public static class VstRuntimeCheck
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FreeLibrary(IntPtr hModule);

        /// <summary>URL officielle Microsoft pour télécharger le redist VC++ 2015-2022 (x64 pour notre build 64-bit).</summary>
        public const string VcRedistDownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

        static bool? _cached;

        /// <summary>Retourne <c>true</c> si <c>vcruntime140.dll</c> peut être chargé. Résultat mis en cache — le runtime ne s'installe pas en cours d'exécution.</summary>
        public static bool IsVcRedistInstalled()
        {
            if (_cached.HasValue) return _cached.Value;
            var h = LoadLibrary("vcruntime140.dll");
            if (h != IntPtr.Zero)
            {
                FreeLibrary(h);
                _cached = true;
                return true;
            }
            // Filet supplémentaire : présence explicite dans System32/SysWOW64.
            var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var sysWow = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
            if ((!string.IsNullOrEmpty(sys32) && File.Exists(Path.Combine(sys32, "vcruntime140.dll")))
             || (!string.IsNullOrEmpty(sysWow) && File.Exists(Path.Combine(sysWow, "vcruntime140.dll"))))
            {
                _cached = true;
                return true;
            }
            _cached = false;
            return false;
        }
    }
}
