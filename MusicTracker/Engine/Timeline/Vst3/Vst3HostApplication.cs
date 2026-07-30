using System;
using System.Runtime.InteropServices;
using MusicTracker.Engine.Timeline.Vst3.Interop;

namespace MusicTracker.Engine.Timeline.Vst3
{
    /// <summary>
    /// CCW managé d'<see cref="IHostApplication"/> — passé au plugin via <c>IComponent.initialize()</c> et
    /// <c>IEditController.initialize()</c>. Beaucoup de plugins VST3 modernes REFUSENT d'initialiser
    /// correctement leur éditeur (GUI noire, createView refuse, controller inerte) si initialize reçoit
    /// null au lieu d'une IHostApplication valide.
    ///
    /// L'implémentation est minimale : <see cref="getName"/> renvoie « Koton Studio » (utilisé parfois dans
    /// le « À propos » du plugin), <see cref="createInstance"/> renvoie systématiquement kNotImplemented
    /// (le plugin peut demander à l'host de créer certains types Steinberg comme <c>IMessage</c> — on
    /// n'en a pas besoin pour un hoster basique). Une seule instance singleton suffit pour tous les
    /// plugins ; c'est référencé via <see cref="Instance"/>.
    /// </summary>
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class Vst3HostApplication : IHostApplication
    {
        static readonly Vst3HostApplication _singleton = new Vst3HostApplication();
        static IntPtr _singletonPtr;

        /// <summary>Instance CCW singleton — jamais disposée (vit tant que le process tourne).</summary>
        public static Vst3HostApplication Instance => _singleton;

        /// <summary>COM pointer (FUnknown*) réutilisable pour <c>initialize()</c>. La première demande
        /// alloue le pointeur ; il est intentionnellement JAMAIS libéré (le CCW singleton vit pour
        /// toute la session, et le plugin fait ses propres AddRef/Release dessus).</summary>
        public static IntPtr GetPtr()
        {
            if (_singletonPtr == IntPtr.Zero)
                _singletonPtr = Marshal.GetComInterfaceForObject(_singleton, typeof(IHostApplication));
            return _singletonPtr;
        }

        public int getName(IntPtr name128)
        {
            // String128 = 128 UTF-16 chars (buffer alloué par le plugin), terminé par '\0'.
            // Le plugin l'affiche parfois dans son « À propos ».
            if (name128 == IntPtr.Zero) return Vst3Enums.kInvalidArgument;
            var s = "Koton Studio";
            const int max = 127;                                    // 1 char réservé pour le '\0'
            int n = Math.Min(s.Length, max);
            for (int i = 0; i < n; i++)
                Marshal.WriteInt16(name128, i * 2, s[i]);
            Marshal.WriteInt16(name128, n * 2, 0);                  // null terminator
            return Vst3Enums.kResultOk;
        }

        public int createInstance(IntPtr cid, IntPtr iid, out IntPtr obj)
        {
            // Le plugin peut demander à l'host d'instancier certains types Steinberg (IMessage,
            // IAttributeList). On n'en a pas besoin — le plugin accepte kNotImplemented et se débrouille.
            obj = IntPtr.Zero;
            return Vst3Enums.kNotImplemented;
        }
    }
}
