using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MusicTracker.Live
{
    /// <summary>Moteur audio temps réel utilisé par <see cref="LiveEngine"/>.</summary>
    public enum LiveBackend
    {
        /// <summary>WASAPI partagé : marche sur n'importe quelle carte son Windows, ~15-30 ms aller-retour.</summary>
        Wasapi,
        /// <summary>ASIO : pilote fourni par la carte son (ou ASIO4ALL). Full-duplex dans un seul callback,
        /// ~3-10 ms — le seul mode réellement jouable pour du monitoring d'un micro à travers des effets.</summary>
        Asio,
    }

    /// <summary>
    /// Descripteur d'un périphérique proposé dans les combos de <see cref="LiveWindow"/>. Pour WASAPI,
    /// <see cref="Id"/> est l'identifiant d'endpoint MMDevice (stable entre deux sessions, contrairement à
    /// l'index) ; pour ASIO, c'est le nom du pilote (la seule clé qu'expose <see cref="AsioOut"/>).
    /// </summary>
    public sealed class LiveDeviceInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>Nombre de canaux du format partagé (WASAPI) ou du pilote (ASIO). 0 = inconnu.</summary>
        public int Channels { get; set; }
        /// <summary>Fréquence d'échantillonnage du format partagé WASAPI (0 pour ASIO — négociée au démarrage).</summary>
        public int SampleRate { get; set; }
        public override string ToString() => Name;
    }

    /// <summary>
    /// Énumération des périphériques d'entrée / sortie, tolérante aux pannes : un pilote qui jette pendant
    /// l'énumération (cas classique d'un ASIO installé mais sans matériel branché) est simplement absent de
    /// la liste au lieu de faire tomber la fenêtre.
    /// </summary>
    public static class LiveDevices
    {
        /// <summary>Sorties WASAPI actives. La sortie par défaut de Windows est en tête de liste.</summary>
        public static List<LiveDeviceInfo> Outputs() => Enumerate(DataFlow.Render);

        /// <summary>Entrées WASAPI actives (micros, entrées ligne, boucles de capture).</summary>
        public static List<LiveDeviceInfo> Inputs() => Enumerate(DataFlow.Capture);

        static List<LiveDeviceInfo> Enumerate(DataFlow flow)
        {
            var list = new List<LiveDeviceInfo>();
            try
            {
                var en = new MMDeviceEnumerator();
                string defaultId = null;
                try { defaultId = en.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID; } catch { }
                foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    var info = new LiveDeviceInfo { Id = d.ID, Name = d.FriendlyName };
                    try { var mix = d.AudioClient.MixFormat; info.Channels = mix.Channels; info.SampleRate = mix.SampleRate; }
                    catch { }
                    if (info.Id == defaultId) list.Insert(0, info); else list.Add(info);
                }
            }
            catch { /* pas de service audio / pas de périphérique : liste vide, la fenêtre le signale */ }
            return list;
        }

        /// <summary>Résout un endpoint par son Id ; <c>null</c> (ou un Id disparu) retombe sur le périphérique
        /// par défaut de Windows, pour qu'un réglage sauvegardé pointant vers une carte débranchée démarre
        /// quand même sur quelque chose d'audible.</summary>
        public static MMDevice Resolve(string id, DataFlow flow)
        {
            var en = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(id))
            {
                try { return en.GetDevice(id); } catch { }
            }
            return en.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        }

        /// <summary>Pilotes ASIO installés sur la machine (liste vide = aucun, l'UI grise alors le moteur ASIO).</summary>
        public static List<string> AsioDrivers()
        {
            var list = new List<string>();
            try { foreach (var n in AsioOut.GetDriverNames()) if (!string.IsNullOrWhiteSpace(n)) list.Add(n); }
            catch { }
            return list;
        }

        /// <summary>Périphériques d'entrée MIDI (index = celui attendu par <see cref="LiveMidiInput"/>).</summary>
        public static List<LiveDeviceInfo> MidiInputs()
        {
            var list = new List<LiveDeviceInfo>();
            try
            {
                for (int i = 0; i < NAudio.Midi.MidiIn.NumberOfDevices; i++)
                    list.Add(new LiveDeviceInfo { Id = i.ToString(), Name = NAudio.Midi.MidiIn.DeviceInfo(i).ProductName });
            }
            catch { }
            return list;
        }

        /// <summary>Périphériques d'entrée WinMM (WaveIn) — utilisés par la détection de hauteur au micro,
        /// qui capture pour son compte (indépendamment du moteur de sortie WASAPI/ASIO).</summary>
        public static List<LiveDeviceInfo> WaveInDevices()
        {
            var list = new List<LiveDeviceInfo>();
            try
            {
                for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                    list.Add(new LiveDeviceInfo { Id = i.ToString(), Name = WaveInEvent.GetCapabilities(i).ProductName });
            }
            catch { }
            return list;
        }
    }
}
