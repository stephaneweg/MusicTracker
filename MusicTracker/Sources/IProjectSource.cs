using System.Windows;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Sources
{
    /// <summary>
    /// A project SOURCE: produces a self-contained <see cref="TimelineDocument"/>, showing its OWN dialog for input when
    /// it needs one. The shell then loads whatever a source returns via <c>TimelineScreen.LoadDocument</c>, so the editor
    /// stays agnostic to where the document came from (opened file, import, generative composer, structure…).
    /// Returns null when the user cancels.
    /// </summary>
    public interface IProjectSource
    {
        /// <summary>A short label for the produced tab (e.g. the file name, or a generated title); may be null.</summary>
        string Title { get; }

        /// <summary>Produce the document (may show a modal dialog owned by <paramref name="owner"/>). Null = cancelled.</summary>
        TimelineDocument Produce(Window owner);
    }
}
