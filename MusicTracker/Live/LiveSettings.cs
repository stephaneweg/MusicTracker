using System;
using System.Collections.Generic;
using System.IO;
using MusicTracker.Engine;
using MusicTracker.Engine.Timeline.Effects;

namespace MusicTracker.Live
{
    /// <summary>Source des notes en mode « Instrument ».</summary>
    public enum LiveNoteSource
    {
        /// <summary>Clavier / contrôleur MIDI (avec vélocité, sustain, molettes).</summary>
        Midi,
        /// <summary>Micro : détection de hauteur monophonique, la même que l'enregistrement de l'éditeur
        /// de riff (voix, violon, flûte…).</summary>
        Microphone,
    }

    /// <summary>
    /// Réglages persistés du programme live, dans <c>%AppData%\MusicTracker\live.json</c> — fichier séparé de
    /// <see cref="AppSettings"/> parce que ce sont deux applications distinctes : lancer Koton Live ne doit
    /// pas réécrire les réglages du séquenceur, et inversement.
    ///
    /// Les périphériques WASAPI sont mémorisés par Id d'endpoint (stable), le MIDI et le WaveIn par NOM
    /// (leurs index bougent dès qu'on branche un périphérique).
    /// </summary>
    public class LiveSettings
    {
        const string FileName = "live.json";

        public LiveBackend Backend { get; set; } = LiveBackend.Wasapi;
        public string InputDeviceId { get; set; } = "";
        public string OutputDeviceId { get; set; } = "";
        public string AsioDriver { get; set; } = "";
        public int AsioInputChannel { get; set; }
        public int LatencyMs { get; set; } = 25;

        public LiveMode Mode { get; set; } = LiveMode.Insert;
        public double InputGain { get; set; } = 1.0;
        public double OutputGain { get; set; } = AudioFormat.OutputGain;
        public bool MonitorInput { get; set; }

        public LiveNoteSource NoteSource { get; set; } = LiveNoteSource.Midi;
        public string MidiDevice { get; set; } = "";
        public string MicDevice { get; set; } = "";
        /// <summary>Tonique de la gamme de calage (0 = Do) pour la détection de hauteur.</summary>
        public int ScaleRoot { get; set; }
        /// <summary>Index dans <see cref="AudioPitch.ScaleNames"/>, ou -1 pour chromatique (aucun calage).</summary>
        public int ScaleMode { get; set; } = -1;
        /// <summary>Temps de confirmation d'un changement de hauteur, en secondes (voir WaveNoteSourceProvider).</summary>
        public double PitchHold { get; set; } = 0.03;
        /// <summary>Sensibilité au ré-attaque sur une même note (détaché), 0..1.</summary>
        public double OnsetSensitivity { get; set; } = 0.5;
        /// <summary>Vélocité fixe des notes venues du micro (le micro ne donne pas de vélocité fiable).</summary>
        public int MicVelocity { get; set; } = 100;

        /// <summary>Transposition en octaves appliquée aux notes reçues (micro comme MIDI), -3..+3.</summary>
        public int OctaveShift { get; set; }

        /// <summary>Seuil de voisement de la détection (RMS) : plus bas = plus sensible. Voir
        /// <see cref="MusicTracker.Controls.WaveNoteSourceProvider.SilenceThreshold"/>.</summary>
        public double SilenceThreshold { get; set; } = Controls.WaveNoteSourceProvider.DefaultSilenceThreshold;

        /// <summary>Durée minimale d'une note détectée, en secondes. En dessous, le changement est ignoré et la
        /// note en cours continue. 1/18 s par défaut : assez pour gommer les couacs d'archet et de voix sans
        /// que la latence d'attaque devienne gênante.</summary>
        public double MinNoteSeconds { get; set; } = 1.0 / 18;

        /// <summary>Écart maximal accepté entre deux notes consécutives, en demi-tons (12 à 36). Au-delà, la
        /// trame est ignorée : c'est presque toujours une erreur d'octave de l'analyse.</summary>
        public int MaxLeapSemitones { get; set; } = 24;

        /// <summary>Nombre de fenêtres du filtre médian de hauteur (1 = aucun lissage).</summary>
        public int MedianFrames { get; set; } = 3;

        /// <summary>Taille de la fenêtre d'analyse en échantillons (1024 / 2048 / 4096). Propre au rack live :
        /// ne touche pas au réglage de l'éditeur de riff.</summary>
        public int AnalysisFrameSize { get; set; } = 2048;

        /// <summary>Marge d'accroche autour de la note tenue, en cents (0 = aucune).</summary>
        public double SnapHysteresisCents { get; set; } = 20;

        /// <summary>Note MIDI la plus grave que l'instrument produit : borne la recherche de hauteur et rend
        /// donc impossibles les erreurs d'octave sous cette note. 36 = Do2, proche du défaut historique
        /// (70 Hz) ; un violon se règle sur 55 (Sol3, la corde de sol à vide).</summary>
        public int LowestNoteMidi { get; set; } = 36;

        /// <summary>Biais anti-octave-basse 0..1 (0 = seuil MPM par défaut).</summary>
        public double OctaveBias { get; set; }

        public LiveInstrumentKind InstrumentKind { get; set; } = LiveInstrumentKind.SoundFont;
        /// <summary>Chemin du VSTi ou Id du plugin Koton selon <see cref="InstrumentKind"/>.</summary>
        public string InstrumentRef { get; set; } = "";
        /// <summary>Programme GM du mode SoundFont (128 = kit de batterie).</summary>
        public int Program { get; set; } = 40;   // Violon : l'usage phare de l'entrée micro

        /// <summary>Chaîne d'inserts, sérialisée exactement comme dans un projet .sq (même classe), donc une
        /// chaîne peut être recopiée d'un projet vers le live à la main sans conversion.</summary>
        public List<TrackEffectData> Inserts { get; set; } = new List<TrackEffectData>();

        static LiveSettings _instance;
        public static LiveSettings Instance => _instance ?? (_instance = Load());

        public void Save()
        {
            try { SafeFile.WriteAllText(AppPaths.Roaming(FileName), System.Text.Json.JsonSerializer.Serialize(this)); }
            catch { /* réglages best-effort : un disque plein ne doit pas empêcher de jouer */ }
        }

        static LiveSettings Load()
        {
            try
            {
                string path = AppPaths.Roaming(FileName);
                if (File.Exists(path))
                    return System.Text.Json.JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(path)) ?? new LiveSettings();
            }
            catch { /* fichier corrompu = on repart des défauts plutôt que de refuser de démarrer */ }
            return new LiveSettings();
        }
    }
}
