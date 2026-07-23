using MeltySynth;
using NAudio.Midi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicTracker.Engine
{
    /// <summary>
    /// The fixed list of instruments offered in dropdowns (Set-instrument node + riff-editor
    /// preview). Index 0..127 = General MIDI program; index 128 = drum kit. The order is stable so
    /// the index can be stored/serialized safely.
    /// </summary>
    public static class InstrumentCatalog
    {
        public static Dictionary<int,Dictionary<int,Preset>> instrumentList;

        // All SoundFont samples are normalized to this rate at load (the engine's output rate),
        // so playback reads them 1:1 at the root pitch.
        public static int TargetSampleRate => AudioFormat.SampleRate;
        public const string DefaultSoundFont = "SoundFont\\MuseScore_General.sf2";
        // Bumped on every (re)load, so callers holding a resolved Preset can tell when a SoundFont was
        // swapped at runtime and re-resolve it.
        public static int Version { get; private set; }
        // The SoundFont file currently loaded into patchList (so settings can show / default it).
        public static string CurrentSoundFont { get; private set; }

        // The sample rate the current patchList was resampled to (so a rate-only change is detected,
        // since TargetSampleRate already reflects the new rate before the reload happens).
        public static int LoadedSampleRate { get; private set; }

        // The parsed MeltySynth SoundFont behind the current patch list. Shared with the MeltySynth
        // Synthesizer so playback reuses the already-loaded font instead of re-reading the file.
        public static MeltySynth.SoundFont SoundFontObject { get; private set; }


        private static readonly string[] gmNames =
        {
            "Acoustic Grand Piano","Bright Acoustic Piano","Electric Grand Piano","Honky-tonk Piano",
            "Electric Piano 1","Electric Piano 2","Harpsichord","Clavi",
            "Celesta","Glockenspiel","Music Box","Vibraphone","Marimba","Xylophone","Tubular Bells","Dulcimer",
            "Drawbar Organ","Percussive Organ","Rock Organ","Church Organ","Reed Organ","Accordion","Harmonica","Tango Accordion",
            "Acoustic Guitar (nylon)","Acoustic Guitar (steel)","Electric Guitar (jazz)","Electric Guitar (clean)","Electric Guitar (muted)","Overdriven Guitar","Distortion Guitar","Guitar Harmonics",
            "Acoustic Bass","Electric Bass (finger)","Electric Bass (pick)","Fretless Bass","Slap Bass 1","Slap Bass 2","Synth Bass 1","Synth Bass 2",
            "Violin","Viola","Cello","Contrabass","Tremolo Strings","Pizzicato Strings","Orchestral Harp","Timpani",
            "String Ensemble 1","String Ensemble 2","Synth Strings 1","Synth Strings 2","Choir Aahs","Voice Oohs","Synth Voice","Orchestra Hit",
            "Trumpet","Trombone","Tuba","Muted Trumpet","French Horn","Brass Section","Synth Brass 1","Synth Brass 2",
            "Soprano Sax","Alto Sax","Tenor Sax","Baritone Sax","Oboe","English Horn","Bassoon","Clarinet",
            "Piccolo","Flute","Recorder","Pan Flute","Blown Bottle","Shakuhachi","Whistle","Ocarina",
            "Lead 1 (square)","Lead 2 (sawtooth)","Lead 3 (calliope)","Lead 4 (chiff)","Lead 5 (charang)","Lead 6 (voice)","Lead 7 (fifths)","Lead 8 (bass + lead)",
            "Pad 1 (new age)","Pad 2 (warm)","Pad 3 (polysynth)","Pad 4 (choir)","Pad 5 (bowed)","Pad 6 (metallic)","Pad 7 (halo)","Pad 8 (sweep)",
            "FX 1 (rain)","FX 2 (soundtrack)","FX 3 (crystal)","FX 4 (atmosphere)","FX 5 (brightness)","FX 6 (goblins)","FX 7 (echoes)","FX 8 (sci-fi)",
            "Sitar","Banjo","Shamisen","Koto","Kalimba","Bag pipe","Fiddle","Shanai",
            "Tinkle Bell","Agogo","Steel Drums","Woodblock","Taiko Drum","Melodic Tom","Synth Drum","Reverse Cymbal",
            "Guitar Fret Noise","Breath Noise","Seashore","Bird Tweet","Telephone Ring","Helicopter","Applause","Gunshot",
        };

        /// <summary>Standard General MIDI program name for 0..127.</summary>
        public static string GmName(int program)
        {
            program = ((program % 128) + 128) % 128;
            return gmNames[program];
        }

        public const int DrumIndex = 128;
        public const int Count = 129;

        public static string Name(int index)
        {
            return index >= DrumIndex ? "Drum kit" : GmName(index);
        }
        // GM programs that have an "Expr." variant in bank 17 (MuseScore's CC2 single-note-dynamics
        // instruments — strings, brass, winds, organs, pads…). Sustained tracks are routed there so their
        // CC2->attenuation modulators drive the dynamics like in MuseScore.
        public const int ExprBank = 17;
        public static HashSet<int> ExprPrograms { get; private set; } = new HashSet<int>();

        static InstrumentCatalog()
        {
            instrumentList = new Dictionary<int,Dictionary<int, Preset>>();
            Load(DefaultSoundFont);
        }



        public static void Reload(string path)
        {
            Load(path);
        }

        static bool Load(string path)
        {
            path = AppPaths.Local(path); // resolve against the assembly dir (a relative path is launch-dir-agnostic)
            var list = LoadSoundFont(path);
            if (list.Count == 0) return false; // bad/missing file — keep whatever we already had
            instrumentList = list;                  // atomic reference swap (the audio thread reads the field)
            CurrentSoundFont = path;
            LoadedSampleRate = TargetSampleRate; // the rate `list` was resampled to
            Version++;
            return true;
        }

        public static Dictionary<int,Dictionary<int,Preset>> LoadSoundFont(string path)
        {
            Dictionary<int,Dictionary<int,Preset>> result = new Dictionary<int,Dictionary<int,Preset>>();
            if (System.IO.File.Exists(path))
            {
                MeltySynth.SoundFont sf = new MeltySynth.SoundFont(path);
                SoundFontObject = sf; // keep the parsed font so the MeltySynth Synthesizer can share it (no reload)
                ExprPrograms = new HashSet<int>(sf.Presets.Where(p => p.BankNumber == ExprBank).Select(p => p.PatchNumber));

                foreach(var preset in sf.Presets)
                {
                    if (!result.ContainsKey(preset.BankNumber))
                        result[preset.BankNumber] = new Dictionary<int, Preset>();
                    result[preset.BankNumber][preset.PatchNumber] = preset;
                }
            }
            return result;
        }

        public static Preset GetPreset(int index)
        {
            // Prefer the loaded SoundFont (real samples); fall back to the procedural preset.
            if (index >= DrumIndex) return SoundFontDrumKit();
            return SoundFontProgram(index);
        }

        public static Preset GetPreset(int bank, int patch)
        {
            var list = instrumentList;
            if (list == null) return null;
            Dictionary<int, Preset> bankDict;
            bankDict = (list.ContainsKey(bank)) ?list[bank]:instrumentList.FirstOrDefault().Value;
            return (bankDict.ContainsKey(patch)) ? bankDict[patch] : bankDict.Values.FirstOrDefault();
        }

        /// <summary>The SoundFont drum kit (bank 128) if present, else null.</summary>
        public static Preset SoundFontDrumKit()
        {
            return GetPreset(128, 0); // the "Standard" kit is patch 0
        }

        /// <summary>
        /// The loaded SoundFont's preset for a General MIDI program (bank 0), or null if it has none.
        /// </summary>
        public static Preset SoundFontProgram(int program)
        {
            return GetPreset(0, program); // bank 0 is the GM bank
        }

        public static List<string> Names()
        {
            var list = new List<string>(Count);
            for (int i = 0; i < Count; i++) list.Add(Name(i));
            return list;
        }

        // ---- drum kits (the bank-128 presets of the loaded SoundFont) ----

        /// <summary>The SoundFont's drum-kit presets (bank 128), ordered by patch number.</summary>
        public static List<Preset> DrumKits()
        {
            Dictionary<int, Preset> bankDict;
            if (instrumentList.ContainsKey(128))
                bankDict = instrumentList[128];
            else
                return new List<Preset>();
            return bankDict.Values.OrderBy(p => p.PatchNumber).ToList();
        }

        /// <summary>Display names of the drum kits (e.g. "Standard", "Room", "Jazz", "TR-808"…).</summary>
        public static List<string> DrumKitNames()
        {
            var kits = DrumKits();
            if (kits.Count == 0) return new List<string> { "Drum kit" };
            return kits.Select(k => CleanKitName(k.Name)).ToList();
        }

        /// <summary>Instrument for a specific drum kit (index into <see cref="DrumKits"/>); falls back to the procedural kit.</summary>
        public static Preset GetDrumKit(int kitIndex)
        {
            return GetPreset(128, kitIndex);
        }

        /// <summary>The GM program (patch number) of a drum kit — used to select it on MIDI channel 10.</summary>
        public static int DrumKitProgram(int kitIndex)
        {
            var kits = DrumKits();
            if (kitIndex < 0 || kitIndex >= kits.Count) return 0;
            return kits[kitIndex].PatchNumber;
        }

        static string CleanKitName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Kit";
            int i = name.IndexOf(" - ");
            return i >= 0 ? name.Substring(i + 3) : name;
        }
    }
}
