using System;
using System.Runtime.InteropServices;
using MusicTracker.Engine.Timeline.Vst3.Interop;

namespace MusicTracker.Engine.Timeline.Vst3
{
    /// <summary>
    /// CCW managé d'<see cref="IPlugFrame"/> : le callback host que le plugin VST3 utilise pour demander un
    /// redimensionnement de sa GUI. Doit être fourni via <c>IPlugView.setFrame()</c> AVANT
    /// <c>IPlugView.attached()</c> — plein de plugins VST3 refusent de rendre leur GUI (fenêtre noire /
    /// blanche / vide) si setFrame reçoit <c>null</c>, même s'ils n'appellent jamais resizeView eux-mêmes.
    ///
    /// L'appel <see cref="resizeView"/> est optionnel côté plugin : on propage juste via <see cref="Resized"/>
    /// vers la fenêtre WPF hôte qui adapte sa taille en conséquence. Retour <see cref="Vst3Enums.kResultOk"/>
    /// même si personne ne s'abonne — refuser (kNotImplemented) ferait croire au plugin qu'on ne supporte pas
    /// le resize et il pourrait bloquer certaines de ses fonctionnalités.
    /// </summary>
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class Vst3PlugFrame : IPlugFrame
    {
        /// <summary>Nouvelle taille demandée par le plugin (largeur, hauteur en pixels). Le handler doit
        /// redimensionner la fenêtre hôte + rappeler <c>IPlugView.onSize(newRect)</c> pour confirmer.</summary>
        public event Action<int, int> Resized;

        public int resizeView(IntPtr view, ref ViewRect newSize)
        {
            try { Resized?.Invoke(newSize.Width, newSize.Height); }
            catch { /* best-effort ; ne pas propager d'exception managée dans le plugin natif */ }
            return Vst3Enums.kResultOk;
        }
    }
}
