using System;
using System.IO;
using System.Runtime.InteropServices;
using MusicTracker.Engine.Timeline.Vst3.Interop;

namespace MusicTracker.Engine.Timeline.Vst3
{
    /// <summary>
    /// Charge un module VST3 sous Windows (fichier <c>.vst3</c> OU dossier bundle
    /// <c>Foo.vst3/Contents/x86_64-win/Foo.vst3</c>) et donne accès à son <see cref="IPluginFactory"/>.
    /// Reprend la même logique que <c>module_win32.cpp</c> du SDK Steinberg :
    /// <list type="bullet">
    ///   <item><c>LoadLibraryW</c> sur le path résolu (fichier direct ou fichier interne au bundle) ;</item>
    ///   <item>optionnel : appel <c>InitDll()</c> exporté si présent (échec = plugin invalide) ;</item>
    ///   <item><c>GetPluginFactory()</c> pour récupérer le <see cref="IPluginFactory"/> ;</item>
    ///   <item>à <see cref="Dispose"/> : optionnel <c>ExitDll()</c>, puis <c>FreeLibrary</c>.</item>
    /// </list>
    ///
    /// **Attention** : sur Windows on est x64 (voir csproj), un plugin x86 échouera au chargement — c'est
    /// le comportement attendu (32-bit VST3 sont ultra-rares). Un plugin architectural incompatible ou
    /// corrompu jette une exception → le caller la capture et passe la piste en bypass.
    ///
    /// Le loader **NE FAIT PAS** de reference-count sur la factory : <see cref="Factory"/> est une IntPtr
    /// brute qui pointe sur le FUnknown COM ; les instances créées via <c>createInstance</c> ont leur
    /// propre lifetime (AddRef sur returned pointer, Release à la disposition).
    /// </summary>
    public sealed class Vst3ModuleLoader : IDisposable
    {
        // Windows P/Invoke ------------------------------------------------------------------------------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        // Delegate signatures ---------------------------------------------------------------------------
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr GetPluginFactoryProc();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        delegate bool InitDllProc();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        delegate bool ExitDllProc();

        // ---------------------------------------------------------------------------------------------

        IntPtr _hModule;
        IntPtr _factoryPtr;
        IPluginFactory _factory;
        IPluginFactory2 _factory2;   // may be null if plugin only implements the v1 factory

        /// <summary>Pointeur brut FUnknown* sur la factory — pour cas de bas niveau. Préférer <see cref="Factory"/>.</summary>
        public IntPtr FactoryPtr => _factoryPtr;
        /// <summary>Factory v1 (toujours dispo si <see cref="Load"/> a réussi).</summary>
        public IPluginFactory Factory => _factory;
        /// <summary>Factory v2 (metadata étendue via <c>getClassInfo2</c>) ou <c>null</c>.</summary>
        public IPluginFactory2 Factory2 => _factory2;
        public string Path { get; private set; }
        public bool IsLoaded => _hModule != IntPtr.Zero && _factory != null;

        /// <summary>Résout le chemin réel de la DLL à charger. Sur Windows, un <c>.vst3</c> peut être :
        /// <list type="bullet">
        ///   <item>un FICHIER <c>.vst3</c> plat (rare, mais toléré depuis SDK 3.6) ;</item>
        ///   <item>un DOSSIER bundle : <c>Foo.vst3/Contents/x86_64-win/Foo.vst3</c>.</item>
        /// </list>
        /// Renvoie <c>null</c> si aucun binaire n'est trouvé.</summary>
        public static string ResolveBinaryPath(string vst3Path)
        {
            if (string.IsNullOrEmpty(vst3Path)) return null;
            if (File.Exists(vst3Path)) return vst3Path;   // plain .vst3 file
            if (!Directory.Exists(vst3Path)) return null;

            var name = System.IO.Path.GetFileName(vst3Path);   // "Foo.vst3"

            // 1. Layout Steinberg strict : <bundle>/Contents/x86_64-win/<bundle>.vst3
            var std = System.IO.Path.Combine(vst3Path, "Contents", "x86_64-win", name);
            if (File.Exists(std)) return std;

            // 2. Layout à plat : <bundle>/<bundle>.vst3 (fichier directement dans le dossier)
            var flat = System.IO.Path.Combine(vst3Path, name);
            if (File.Exists(flat)) return flat;

            // 3. Fallback : n'importe quel .vst3 dans <bundle>/ (top-level uniquement)
            try
            {
                foreach (var f in Directory.EnumerateFiles(vst3Path, "*.vst3", SearchOption.TopDirectoryOnly))
                    return f;
            }
            catch { }

            // 4. Fallback profond : n'importe quel .vst3 récursivement dans <bundle>/ (bundle exotique).
            //    On skippe x86-win (32-bit) pour rester cohérent avec l'app x64-only.
            try
            {
                foreach (var f in Directory.EnumerateFiles(vst3Path, "*.vst3", SearchOption.AllDirectories))
                {
                    var lower = f.Replace('\\', '/').ToLowerInvariant();
                    if (lower.Contains("/x86-win/")) continue;   // 32-bit — ignorer
                    return f;
                }
            }
            catch { }

            return null;
        }

        /// <summary>Charge le module. Jette <see cref="InvalidOperationException"/> en cas d'échec (chemin
        /// invalide, LoadLibrary raté, pas de GetPluginFactory exporté, InitDll returns false).</summary>
        public void Load(string vst3Path)
        {
            if (IsLoaded) throw new InvalidOperationException("Vst3ModuleLoader already loaded");
            var binaryPath = ResolveBinaryPath(vst3Path);
            if (binaryPath == null) throw new FileNotFoundException("VST3 binary not found (neither file nor bundle)", vst3Path);
            _hModule = LoadLibraryW(binaryPath);
            if (_hModule == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"LoadLibraryW failed for '{binaryPath}' (Win32 error {err}). Likely 32-bit VST3 or missing dependency.");
            }
            try
            {
                // InitDll est optionnel. Certains SDK legacy l'utilisent pour l'init global du plugin.
                var initPtr = GetProcAddress(_hModule, "InitDll");
                if (initPtr != IntPtr.Zero)
                {
                    var initDll = Marshal.GetDelegateForFunctionPointer<InitDllProc>(initPtr);
                    if (!initDll()) throw new InvalidOperationException("InitDll returned false");
                }
                var factoryProcPtr = GetProcAddress(_hModule, "GetPluginFactory");
                if (factoryProcPtr == IntPtr.Zero)
                    throw new InvalidOperationException("Missing 'GetPluginFactory' export — not a valid VST3 module.");
                var factoryProc = Marshal.GetDelegateForFunctionPointer<GetPluginFactoryProc>(factoryProcPtr);
                _factoryPtr = factoryProc();
                if (_factoryPtr == IntPtr.Zero)
                    throw new InvalidOperationException("GetPluginFactory returned null");
                // GetObjectForIUnknown fait AddRef côté RCW ; on relâche l'AddRef initial du factory
                // pour rester sur un refcount total = 1 (RCW en propriétaire).
                _factory = (IPluginFactory)Marshal.GetObjectForIUnknown(_factoryPtr);
                Marshal.Release(_factoryPtr);
                // v2 en best-effort : plugins récents l'ont, plugins vraiment anciens non.
                try { _factory2 = _factory as IPluginFactory2; } catch { _factory2 = null; }
                Path = vst3Path;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            // Release RCWs before FreeLibrary : sinon les finalizers plus tard essaieront de libérer un
            // vtable dont le code a disparu → AV.
            if (_factory != null) { try { Marshal.ReleaseComObject(_factory); } catch { } _factory = null; _factory2 = null; }
            _factoryPtr = IntPtr.Zero;
            if (_hModule != IntPtr.Zero)
            {
                try
                {
                    var exitPtr = GetProcAddress(_hModule, "ExitDll");
                    if (exitPtr != IntPtr.Zero)
                    {
                        var exitDll = Marshal.GetDelegateForFunctionPointer<ExitDllProc>(exitPtr);
                        try { exitDll(); } catch { }
                    }
                    FreeLibrary(_hModule);
                }
                catch { }
                _hModule = IntPtr.Zero;
            }
        }
    }
}
