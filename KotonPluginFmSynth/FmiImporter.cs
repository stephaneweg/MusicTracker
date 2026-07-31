using System;
using System.Collections.Generic;
using System.Globalization;

namespace KotonPluginFmSynth
{
    /// <summary>
    /// Importeur des banques d'instruments FM au format <c>.FMI</c> texte du programme "fm-song"
    /// (Asher256, 1998) — format basé sur les registres de la puce Yamaha YM3812 (OPL2 / AdLib).
    /// Un fichier .FMI décrit un preset 2-opérateurs : 26 paramètres OPL par opérateur (attack rate,
    /// decay rate, sustain level, release rate, total level, key scale, waveform, multiplier,
    /// tremolo/vibrato/scaling flags) plus 2 globaux (feedback + connection FM/additive).
    ///
    /// **Rôle** : utilisé en OUTILLAGE HORS-RUNTIME pour convertir la banque publique de ~70 .FMI
    /// (github.com/Asher256/asher256-legacy/fmsong/INS) en <see cref="FmPresets.Preset"/> C#
    /// intégrés à <see cref="FmPresets"/>. Non appelé en production (les presets sont statiques),
    /// mais gardé compilé pour permettre un futur "importer un .fmi utilisateur" depuis l'UI, et
    /// pour documenter le mapping OPL2 → nos paramètres FM.
    ///
    /// **Ordre de sérialisation** (extrait de INSEDIT.BAS lignes 479-495 du projet fm-song) — chaque
    /// ligne est une paire "mod, car" en décimal séparé par virgule :
    /// <list type="number">
    /// <item>attack (AR) 0-15 par op</item>
    /// <item>decay (DR) 0-15</item>
    /// <item>sustain LEVEL (SL) 0-15 — inversé : 0 = niveau max, 15 = silence</item>
    /// <item>release (RR) 0-15</item>
    /// <item>output LEVEL (TL) 0-63 — inversé : 0 = max, 63 = silence</item>
    /// <item>key scale level (KSL) 0-3</item>
    /// <item>amplitude vibrato (tremolo AM) 0/1</item>
    /// <item>pitch vibrato (VIB) 0/1</item>
    /// <item>frequency multiplier (MULT) 0-15 (0 → OPL interprète × 0.5)</item>
    /// <item>envelope scaling (key scale rate KSR) 0/1</item>
    /// <item>waveselect (WS) 0-3 : 0=sinus, 1=demi-sinus, 2=|sinus|, 3=quart-sinus-pulsé</item>
    /// <item>sustaining flag (EGT) 0/1 — 0 = son percussif décroit après attaque, 1 = tenu</item>
    /// <item>feedback (FB) 0-7 + connection (CNT) 0=FM, 1=additive (paire globale, pas par op)</item>
    /// </list>
    ///
    /// **Le mapping est lossy** : notre synthé est 2-op sinus + 4 formes d'onde supplémentaires, la
    /// puce OPL vraie a beaucoup plus de subtilités (KSL / KSR / tremolo à taux fixe...). Un preset
    /// converti donne un son "OPL-like" reconnaissable mais pas identique.
    ///
    /// **Décodage des registres OPL2** (vérifié dans SUB setinstrument de fmlib.bas) — chaque
    /// paramètre écrit sur les registres 0x20..0xE0 :
    /// <list type="bullet">
    /// <item>Reg 0x20+op (par opérateur) : bit 7=VIB, bit 6=AM, bit 5=EGT (nommé `sustaininglevel`
    /// dans le code fmlib, TROMPEUR — c'est le flag Envelope Generation Type 0/1), bit 4=KSR,
    /// bits 0-3=MULT (0→×0.5).</item>
    /// <item>Reg 0x40+op : bits 6-7=KSL, bits 0-5=TL (atténuation 0-63, 0=max, 63=silence).</item>
    /// <item>Reg 0x60+op : bits 4-7=AR (attack rate 0-15, 15=quasi instantané), bits 0-3=DR.</item>
    /// <item>Reg 0x80+op : bits 4-7=SL (sustain LEVEL 0-15, 0=max, 15=silence — c'est le vrai
    /// sustain, nommé `sustain` dans fmlib), bits 0-3=RR.</item>
    /// <item>Reg 0xE0+op : bits 0-1=WS (0=Sine, 1=HalfSine, 2=AbsSine, 3=QuarterPulse).</item>
    /// <item>Reg 0xC0+ch (par CHANNEL, pas par op) : bits 1-3=FB (feedback, écrit *2 par fmlib →
    /// FB*2 dans le registre), bit 0=CNT (0=FM, 1=Additif).</item>
    /// </list>
    ///
    /// **Feedback OPL2 = bit-shift, pas scalaire** — critique pour éviter le bruit blanc à
    /// l'import. Yamaha implémente FB comme un shift : `mod_out >> (8 - FB)` avec FB=7 max = ×0.5.
    /// Notre synthé fait `phaseM + feedback * PI * modOut` linéaire — un feedback naïf à 1.0
    /// (mappé depuis FB=7) fait ±π de déphasage = auto-oscillation chaotique = bruit blanc. D'où
    /// le facteur *0.3 (max 0.35) appliqué en sortie du mapping ci-dessous, qui reste proche du
    /// facteur effectif OPL réel.
    /// </summary>
    internal static class FmiImporter
    {
        /// <summary>
        /// Parse un contenu texte de fichier .FMI et retourne un <see cref="FmPresets.Preset"/>
        /// converti selon le mapping OPL2 → nos paramètres.
        /// </summary>
        /// <param name="content">Contenu texte du fichier .FMI (14 lignes attendues).</param>
        /// <param name="fallbackName">Nom lisible à utiliser si le nom interne du fichier est vide
        /// ou tronqué (le format QB stocke iname en STRING * 8 → maxi 8 caractères).</param>
        /// <returns>Preset prêt à insérer dans FmPresets.All, ou <c>null</c> si le fichier est
        /// mal formé (< 13 paires de valeurs après le header).</returns>
        public static FmPresets.Preset ParseFmi(string content, string fallbackName)
        {
            if (string.IsNullOrEmpty(content)) return null;

            // Split en lignes non-vides et trim
            var lines = new List<string>();
            foreach (var raw in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = raw.Trim();
                if (trimmed.Length > 0) lines.Add(trimmed);
            }

            // Header : ligne 0 = "fm-song instrument" (guillemets QB), ligne 1 = nom
            // Puis 13 paires de valeurs
            if (lines.Count < 15) return null;

            // Nom : si présent dans le fichier et non vide, l'utiliser (nettoyé) sinon fallback.
            string name = StripQb(lines[1]);
            if (string.IsNullOrWhiteSpace(name)) name = fallbackName;
            name = PrettyName(name);

            // Parse 13 paires (lignes 2..14)
            int[] mod = new int[13];
            int[] car = new int[13];
            for (int i = 0; i < 13; i++)
            {
                if (!TryParsePair(lines[2 + i], out mod[i], out car[i])) return null;
            }

            int attackMod    = mod[0],  attackCar    = car[0];
            int decayMod     = mod[1],  decayCar     = car[1];
            int sustainMod   = mod[2],  sustainCar   = car[2];   // SL 0-15 inversé
            int releaseMod   = mod[3],  releaseCar   = car[3];
            int outLevelMod  = mod[4],  outLevelCar  = car[4];   // TL 0-63 inversé (0=max)
            // scalling (KSL 0-3) : lignes[5] — ignoré (pas d'équivalent 2-op simple)
            // amplitudevibrato (AM tremolo) : lignes[6] — ignoré (LFO agit sur pitch)
            int vibratoMod   = mod[7],  vibratoCar   = car[7];   // VIB flag 0/1 → LFO on/off
            int multMod      = mod[8],  multCar      = car[8];   // MULT 0-15
            // enveloppescalling (KSR) : lignes[9] — ignoré
            int waveMod      = mod[10], waveCar      = car[10];  // WS 0-3
            int egtMod       = mod[11], egtCar       = car[11];  // sustaining flag 0/1
            int feedback     = mod[12];                          // 0-7
            int connection   = car[12];                          // 0=FM, 1=additive

            // === Ratio : mod_mult / car_mult (avec OPL 0 → 0.5)
            double mMod = (multMod == 0) ? 0.5 : (double)multMod;
            double mCar = (multCar == 0) ? 0.5 : (double)multCar;
            double ratio = Clamp(mMod / mCar, 0.5, 8.0);

            // === Index de modulation : (63 - outp_mod) / 63 * MAX_INDEX
            // Plafond à 5.0 (au lieu de 8.0 théorique) : notre synth 2-op aliase dès qu'un partiel
            // dépasse Nyquist. Un index > 5 combiné à ratio > 3 + note aiguë produit un bruit haut du
            // spectre superposé à la fondamentale — d'où le "bruit blanc mélangé à des fréquences
            // identifiables" observé sur les presets OPL2 importés bruts. Perte de fidélité minime,
            // suppression totale du bruit d'aliasing.
            double index = Clamp((63 - outLevelMod) / 63.0 * 5.0, 0.0, 5.0);

            // === ADSR times : OPL rate 0-15 → ms empirique 4000 * 0.5^(rate/2)
            // Rate 0 → 4000ms (très lent), rate 8 → 62ms, rate 15 → 22ms (quasi instantané pour l'oreille).
            // On prend le carrier pour attack/decay/release (c'est lui qui module l'amplitude perçue).
            // Attack plancher à 5ms : en-dessous, notre enveloppe linéaire produit un click audible
            // (la vraie OPL a un ramp expo qui masque ça).
            double attackMs  = Clamp(OplRateToMs(attackCar),  5.0, 4000.0);
            double decayMs   = Clamp(OplRateToMs(decayCar),   1.0, 4000.0);
            double releaseMs = Clamp(OplRateToMs(releaseCar), 1.0, 4000.0);

            // === Sustain level (0..1) : SL du carrier (0=max, 15=silence)
            double sustain = (15 - sustainCar) / 15.0;
            // EGT flag : si le carrier n'est PAS "sustained", le son décroit après decay → sustain bas.
            if (egtCar == 0) sustain *= 0.3;
            sustain = Clamp(sustain, 0.0, 1.0);
            // Plancher sustain pour les instruments à attaque longue (> 100 ms) : sans ça, un preset
            // Cello/AltoViola avec SL_OPL=15 tombe à sustain=0 avec un ramp trop court → note quasi
            // inaudible dès la fin du decay. Heuristique : si le preset "swell" (attack > 100ms), on
            // suppose que l'utilisateur veut tenir la note → sustain minimum 0.2.
            if (attackMs > 100.0 && sustain < 0.15) sustain = 0.2;

            // === Waveforms : 0→Sine, 1→HalfSine, 2→AbsSine, 3→Sawtooth (approx pour pulse-quarter)
            var modWave = MapOplWave(waveMod);
            var carWave = MapOplWave(waveCar);

            // === Feedback : OPL 0-7 → 0..0.3 (BEAUCOUP moins que /7=1.0)
            // Notre implémentation : `fbPhase = phaseM + fb * PI * lastModOut` — avec fb=1.0 on
            // atteint ±π de déphasage instantané → auto-oscillation chaotique = bruit blanc.
            // La vraie OPL utilise un bit-shift (op_out >> (7-FB)) qui plafonne le feedback effectif
            // à environ 0.25-0.35 du signal. On calque cette plage : fb * 0.3, max 0.35.
            double fb = Clamp(feedback / 7.0 * 0.3, 0.0, 0.35);

            // === Volume dérivé du TL du carrier : 0=max (63 unités × 0.75 dB) = -47dB (silence).
            // On mappe TL_car [0..63] → volume [0.9..0.2] pour préserver l'info d'amplitude relative
            // (perdue si on met tout à 0.7 par défaut). Un preset "loud" (TL_car=0) sortira à 0.9,
            // un preset "soft" (TL_car=40) à ~0.3.
            double vol = Clamp((63 - outLevelCar) / 63.0 * 0.7 + 0.2, 0.2, 0.9);

            // === Additive : connection = 1
            bool additive = (connection == 1);

            // === LFO : pas de tremolo direct côté nous, on active un vibrato modéré si un des 2 flags est set.
            double lfoRate = 3.0, lfoDepth = 0.0;
            if (vibratoMod == 1 || vibratoCar == 1)
            {
                lfoRate = 5.0;
                lfoDepth = 0.15;
            }

            return new FmPresets.Preset
            {
                Name = name,
                Ratio = ratio,
                Index = index,
                Attack = attackMs,
                Decay = decayMs,
                Sustain = sustain,
                Release = releaseMs,
                Volume = vol,
                LfoRate = lfoRate,
                LfoDepth = lfoDepth,
                ModWave = modWave,
                CarWave = carWave,
                Feedback = fb,
                Additive = additive,
            };
        }

        // ----------------------------------------------------------------- helpers

        static bool TryParsePair(string line, out int a, out int b)
        {
            a = 0; b = 0;
            if (string.IsNullOrEmpty(line)) return false;
            var parts = line.Split(',');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out a)) return false;
            if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out b)) return false;
            return true;
        }

        static string StripQb(string s)
        {
            // QBasic WRITE encadre les strings de guillemets doubles + espaces éventuels + NUL.
            if (s == null) return "";
            s = s.Trim().Trim('"').Trim();
            // STRING * 8 → padding NUL/espaces à droite.
            s = s.TrimEnd('\0').TrimEnd();
            return s;
        }

        /// <summary>Rend "BAGPIPES" ou "ENGLHORN" en "Bagpipes" / "English Horn" — casse-titre.</summary>
        static string PrettyName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "OPL Preset";

            // Dictionnaire d'expansions pour les abréviations 8-caractères du format QBasic.
            switch (raw.ToUpperInvariant())
            {
                case "ACCORDN":  return "Accordion";
                case "ALTOVIOL": return "Alto Viola";
                case "BAGPIPES": return "Bagpipes";
                case "BANJO":    return "Banjo";
                case "BASSHARP": return "Bass Harp";
                case "BASSOON":  return "Bassoon";
                case "BDRUM1":   return "Bass Drum 1";
                case "BDRUM2":   return "Bass Drum 2";
                case "BDRUMSUS": return "Bass Drum Sustained";
                case "BELLS":    return "Bells";
                case "BELPIANO": return "Bell Piano";
                case "BRASS":    return "OPL Brass";
                case "BRUSH":    return "Brush";
                case "CELESTA":  return "Celesta";
                case "CELLO":    return "Cello";
                case "CHIRP":    return "Chirp";
                case "CLARINET": return "Clarinet";
                case "CLAVES":   return "Claves";
                case "CONTRAB":  return "Contrabass";
                case "CYMBAL":   return "Cymbal";
                case "DRILL":    return "Drill";
                case "DRIP":     return "Drip";
                case "ELGUITAR": return "Electric Guitar";
                case "ELPIANO":  return "OPL Electric Piano";
                case "ENGLHORN": return "English Horn";
                case "FLUTE":    return "Flute";
                case "FREHORN":  return "French Horn";
                case "GUITAR":   return "Guitar";
                case "HARMONCA": return "Harmonica";
                case "HARP":     return "Harp";
                case "HARPSI":   return "Harpsichord";
                case "HIHAT":    return "Hi-Hat";
                case "JAVAICAN": return "Javanese Gamelan";
                case "KUORSAUS": return "Kuorsaus";
                case "LASER1":   return "Laser 1";
                case "LASER2":   return "Laser 2";
                case "LOGDRUM":  return "Log Drum";
                case "MARIMBA":  return "Marimba";
                case "MARS":     return "Mars";
                case "MOON":     return "Moon";
                case "NOISEIN":  return "Noise In";
                case "NOISEON":  return "Noise On";
                case "OBOE":     return "Oboe";
                case "ORGAN":    return "OPL Organ";
                case "PIANO":    return "OPL Piano";
                case "PICCOLO":  return "Piccolo";
                case "PIPES":    return "Pipes";
                case "POPBASS":  return "Pop Bass";
                case "SAXAFONE": return "Saxophone";
                case "SCRATCH":  return "Scratch";
                case "SFTBRASS": return "Soft Brass";
                case "SITAR":    return "Sitar";
                case "SLAPBAS":  return "Slap Bass";
                case "SNARE1":   return "Snare 1";
                case "SNARE2":   return "Snare 2";
                case "SNARELNG": return "Snare Long";
                case "STRINGS":  return "Strings";
                case "SYNDRUM":  return "Syn Drum";
                case "SYNTH1":   return "Synth 1";
                case "SYNTH2":   return "Synth 2";
                case "TENSAX":   return "Tenor Sax";
                case "TINCAN":   return "Tin Can";
                case "TOM":      return "Tom";
                case "TRIANGLE": return "OPL Triangle";
                case "TROMBONE": return "Trombone";
                case "TRUMPET":  return "Trumpet";
                case "TUBA":     return "Tuba";
                case "VIBRA":    return "Vibraphone";
                case "VIOLIN":   return "Violin";
                case "XYLOFONE": return "Xylophone";
                case "ALIEN":    return "Alien";
            }
            // Fallback : Title Case simple.
            return char.ToUpperInvariant(raw[0]) + raw.Substring(1).ToLowerInvariant();
        }

        static FmWaveform MapOplWave(int ws)
        {
            switch (ws)
            {
                case 0: return FmWaveform.Sine;
                case 1: return FmWaveform.HalfSine;
                case 2: return FmWaveform.AbsSine;
                case 3: return FmWaveform.Sawtooth; // approximation du pulse-quarter-sine OPL
                default: return FmWaveform.Sine;
            }
        }

        static double OplRateToMs(int rate)
        {
            if (rate < 0) rate = 0;
            if (rate > 15) rate = 15;
            // 4000 * 0.5^(rate/2) : rate 0 → 4000ms (très lent), 15 → ~22ms (quasi instantané pour l'oreille)
            return 4000.0 * Math.Pow(0.5, rate / 2.0);
        }

        static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
