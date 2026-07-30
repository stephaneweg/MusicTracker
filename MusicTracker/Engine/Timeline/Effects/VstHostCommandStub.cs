using System.Reflection;
using Jacobi.Vst.Core;
using Jacobi.Vst.Core.Host;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Implémentation minimale d'un stub hôte VST : c'est ce que Koton PRÉSENTE à un plugin quand celui-ci
    /// demande des services (« quelle est ma sample rate ? », « quel est le nom de l'hôte ? », etc.).
    ///
    /// **Différence API majeure vs VST.NET 1.x (l'ancienne branche <c>main</c>)** : en v2.x le contrat
    /// <see cref="IVstHostCommandStub"/> n'expose PLUS les callbacks eux-mêmes — il n'a plus que
    /// <see cref="IVstHostCommandStub.PluginContext"/> et une propriété <see cref="IVstHostCommandStub.Commands"/>
    /// qui renvoie l'objet implémentant <see cref="IVstHostCommands20"/>. On économise du typage en faisant
    /// implémenter les deux interfaces par la MÊME instance et en renvoyant <c>this</c> depuis
    /// <see cref="Commands"/>.
    ///
    /// 90 % des callbacks n'ont pas de sens pour un simple hôte d'inserts stéréo : on renvoie des valeurs neutres
    /// (0, false, null) que les plugins interprètent comme « non supporté ». Ce stub est PAR INSTANCE de plugin
    /// (chaque <see cref="VstEffect"/> en crée un) : <see cref="PluginContext"/> est renseigné par VST.NET quand
    /// le contexte est créé.
    /// </summary>
    internal sealed class VstHostCommandStub : IVstHostCommandStub, IVstHostCommands20
    {
        readonly int _sampleRate;
        readonly int _blockSize;

        public VstHostCommandStub(int sampleRate, int blockSize)
        {
            _sampleRate = sampleRate;
            _blockSize = blockSize;
        }

        public IVstPluginContext PluginContext { get; set; }

        /// <summary>C'est nous-mêmes qui portons les 30+ méthodes de <see cref="IVstHostCommands20"/> — pas la peine de composer avec un adapter séparé.</summary>
        public IVstHostCommands20 Commands => this;

        // ============ IVstHostCommands20 ============
        public bool BeginEdit(int index) => true;
        public bool EndEdit(int index) => true;

        public VstCanDoResult CanDo(string cando)
        {
            // Réponse conservative : les plugins d'effets audio (le seul cas qui nous concerne en bêta) n'utilisent
            // quasiment jamais l'API MIDI-vers-hôte, on peut donc dire « no » à presque tout. On accepte les deux
            // envois VST events les plus courants au cas où.
            var kind = VstCanDoHelper.ParseHostCanDo(cando);
            if (kind == VstHostCanDo.SendVstEvents || kind == VstHostCanDo.SendVstMidiEvent) return VstCanDoResult.Yes;
            return VstCanDoResult.No;
        }

        public bool CloseFileSelector(VstFileSelect fileSelect) => false;
        public string GetDirectory() => null;
        public int GetInputLatency() => 0;
        public VstHostLanguage GetLanguage() => VstHostLanguage.English;
        public int GetOutputLatency() => 0;
        public string GetProductString() => "Koton Studio";
        public VstProcessLevels GetProcessLevel() => VstProcessLevels.Realtime;

        public VstTimeInfo GetTimeInfo(VstTimeInfoFlags filterFlags)
        {
            // Le timeline Koton n'expose pas encore la position musicale au plugin (bêta = pas d'automation, pas
            // de tempo-aware). On renvoie une time-info minimale : sample-count = 0, sample-rate correct — assez
            // pour que les plugins qui font juste du DSP audio soient contents.
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

        public string GetVendorString() => "Koton";
        public int GetVendorVersion() => 100;
        public bool IoChanged() => false;
        public bool OpenFileSelector(VstFileSelect fileSelect) => false;
        public bool ProcessEvents(VstEvent[] events) => false;
        public bool SizeWindow(int width, int height) => false;
        public bool UpdateDisplay() => true;
        public VstAutomationStates GetAutomationState() => VstAutomationStates.Off;
        public float GetSampleRate() => _sampleRate;
        public int GetBlockSize() => _blockSize;

        // ============ IVstHostCommands10 (héritées via IVstHostCommands20) ============
        public int GetCurrentPluginID() => 0;
        public int GetVersion()
        {
            // Version de l'HÔTE (Koton) — libre, on renvoie un entier dérivé du build.
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v != null ? v.Major * 1000 + v.Minor * 100 + v.Build * 10 + v.Revision : 1000;
        }
        public void ProcessIdle() { }
        public void SetParameterAutomated(int index, float value) { /* pas d'automation en bêta */ }
    }
}
