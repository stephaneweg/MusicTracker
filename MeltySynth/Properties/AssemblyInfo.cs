using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: AssemblyTitle("MeltySynth")]
[assembly: AssemblyDescription("Vendored SoundFont (SF2) parser.")]
[assembly: AssemblyProduct("MeltySynth")]

// MusicTracker reads internal members of the SoundFont model (regions, samples) for its instrument catalogue.
[assembly: InternalsVisibleTo("MusicTracker")]
