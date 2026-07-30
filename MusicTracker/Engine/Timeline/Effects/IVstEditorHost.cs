using System;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Contrat minimal qu'un objet doit exposer pour être hébergé par <c>VstPluginWindow</c> (la fenêtre WPF
    /// qui embarque la GUI native d'un plugin VST). Permet à la même fenêtre de piloter un <see cref="VstEffect"/>
    /// (insert audio) et un <see cref="MusicTracker.Engine.Timeline.VstInstrument"/> (VSTi) sans dupliquer la
    /// mécanique de HwndHost / idle-timer / EditorOpen-EditorClose.
    ///
    /// Petite interface volontairement — la fenêtre n'a besoin que d'ouvrir / fermer / rafraîchir l'éditeur
    /// natif, connaître sa taille recommandée, et un nom d'affichage. Le chargement paresseux (<c>EnsureOpenedSync</c>)
    /// est là parce que l'UI ouvre la GUI AVANT le premier buffer audio — le plugin doit être vraiment chargé.
    /// </summary>
    public interface IVstEditorHost
    {
        string DisplayName { get; }
        bool IsFailed { get; }
        bool EnsureOpenedSync(int blockSize);
        System.Drawing.Size GetEditorSize();
        bool OpenEditor(IntPtr parentHwnd);
        void CloseEditor();
        void EditorIdle();
    }
}
