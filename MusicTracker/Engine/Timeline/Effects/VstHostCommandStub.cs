using System;
using System.Reflection;
using Jacobi.Vst.Core;
using Jacobi.Vst.Core.Host;
// NB : on n'importe PAS Jacobi.Vst.Core.Plugin — ce namespace contient un homonyme IVstHostCommandStub
// (l'interface côté plugin) et l'ambiguïté fait exploser la résolution.

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Implémentation minimale d'un stub hôte VST : c'est ce que Koton PRÉSENTE à un plugin quand celui-ci
    /// demande des services (« quelle est ma sample rate ? », « quel est le nom de l'hôte ? », etc.). VST.NET
    /// nous impose d'implémenter la totalité de <see cref="IVstHostCommands20"/> (qui étend
    /// <see cref="IVstHostCommands10"/>), même si 90 % des callbacks n'ont pas de sens pour un simple hôte
    /// d'inserts stéréo — on renvoie des valeurs neutres (0, false, null) qui sont interprétées par les
    /// plugins « pas supporté ».
    ///
    /// Ce stub est PAR INSTANCE de plugin (chaque <see cref="VstEffect"/> en crée un) : c'est <see cref="PluginContext"/>
    /// qui référence le plugin en question, et <see cref="IVstHostCommandStub.PluginContext"/> est renseigné par
    /// VST.NET quand le contexte est créé.
    /// </summary>
    internal sealed class VstHostCommandStub : IVstHostCommandStub
    {
        readonly int _sampleRate;
        readonly int _blockSize;

        public VstHostCommandStub(int sampleRate, int blockSize)
        {
            _sampleRate = sampleRate;
            _blockSize = blockSize;
        }

        public IVstPluginContext PluginContext { get; set; }

        // ============ IVstHostCommands20 ============
        public bool BeginEdit(int index) { return true; }
        public bool EndEdit(int index) { return true; }

        public VstCanDoResult CanDo(string cando)
        {
            // Réponse conservative : les plugins d'effets audio (le seul cas qui nous concerne en v1) n'utilisent
            // quasiment jamais l'API MIDI-vers-hôte, on peut donc dire « no » à presque tout. On accepte les deux
            // envois VST events les plus courants au cas où (les strings VST sont en camelCase — VstCanDoHelper
            // convertit une valeur d'enum vers la chaîne officielle).
            var kind = VstCanDoHelper.ParseHostCanDo(cando);
            if (kind == VstHostCanDo.SendVstEvents || kind == VstHostCanDo.SendVstMidiEvent) return VstCanDoResult.Yes;
            return VstCanDoResult.No;
        }

        public bool CloseFileSelector(VstFileSelect fileSelect) { return false; }
        public string GetDirectory() { return null; }
        public int GetInputLatency() { return 0; }
        public VstHostLanguage GetLanguage() { return VstHostLanguage.English; }
        public int GetOutputLatency() { return 0; }
        public string GetProductString() { return "Koton Studio"; }
        public VstProcessLevels GetProcessLevel() { return VstProcessLevels.Realtime; }
        public VstTimeInfo GetTimeInfo(VstTimeInfoFlags filterFlags)
        {
            // Le timeline Koton n'expose pas encore la position musicale au plugin (v1 = pas d'automation, pas de
            // tempo-aware). On renvoie une time-info minimale : sample-count = 0, sample-rate correct — assez pour
            // que les plugins qui font juste du DSP audio soient contents.
            return new VstTimeInfo
            {
                SamplePosition = 0.0,
                SampleRate = _sampleRate,
                Tempo = 120.0,
                TimeSignatureNumerator = 4,
                TimeSignatureDenominator = 4,
                Flags = VstTimeInfoFlags.TempoValid,
            };
        }
        public string GetVendorString() { return "Koton"; }
        public int GetVendorVersion() { return 100; }
        public bool IoChanged() { return false; }
        public bool OpenFileSelector(VstFileSelect fileSelect) { return false; }
        public bool ProcessEvents(VstEvent[] events) { return false; }
        public bool SizeWindow(int width, int height) { return false; }
        public bool UpdateDisplay() { return true; }
        public VstAutomationStates GetAutomationState() { return VstAutomationStates.Off; }
        public float GetSampleRate() { return _sampleRate; }
        public int GetBlockSize() { return _blockSize; }

        // ============ IVstHostCommands10 ============
        public int GetCurrentPluginID() { return 0; }
        public int GetVersion()
        {
            // Version de l'HÔTE (Koton) — libre, on renvoie le build number de l'assembly.
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v != null ? v.Major * 1000 + v.Minor * 100 + v.Build * 10 + v.Revision : 1000;
        }
        public void ProcessIdle() { }
        public void SetParameterAutomated(int index, float value) { /* pas d'automation en v1 */ }
    }
}
