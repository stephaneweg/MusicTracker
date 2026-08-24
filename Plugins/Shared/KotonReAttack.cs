using System;
using System.Collections.Generic;
using KotonStudio.Library;

namespace KotonStudio.Plugins.Shared
{
    /// <summary>
    /// Ré-articulation périodique des notes tenues : une note longue est rejouée à intervalle régulier,
    /// attaque comprise, au lieu de sonner d'un seul tenant. Le paramètre exposé est un TAUX en Hz —
    /// 0 = une seule attaque (comportement historique), 6 = six coups par seconde, etc. Comme la durée
    /// de chaque note vaut l'inverse du taux, monter le curseur raccourcit mécaniquement les notes :
    /// on va du tenu au détaché puis au staccato serré avec un seul réglage.
    ///
    /// Selon la famille d'instrument, le geste porte un nom différent — coup de langue sur un souffleur,
    /// trémolo sur un pincé, détaché sur un archet — d'où le libellé passé au constructeur. En revanche
    /// l'identifiant reste <c>retrig</c> partout : un même id pour un même geste, ce qui permet de copier
    /// un réglage d'un instrument à l'autre et à l'automation de le cibler uniformément.
    ///
    /// **Intégration dans un plugin** : relayer <c>Prepare</c>, <c>Reset</c>, <c>NoteOn</c> et
    /// <c>NoteOff</c>, puis une ligne dans la boucle de rendu —
    /// <c>if (_retrig.Tick()) _retrig.Fire(_stroke);</c>. Rien à restructurer : <see cref="Fire"/>
    /// neutralise les notifications que le plugin renvoie en rejouant ses propres notes.
    ///
    /// **Ce que le geste doit faire selon la famille** :
    /// <list type="bullet">
    /// <item>modèle PERCUSSIF (pincé, frappé, résonateur excité par impulsion) : <c>NoteOff</c> puis
    /// <c>NoteOn</c> suffit, l'attaque y est instantanée par construction ;</item>
    /// <item>modèle AUTO-OSCILLANT (souffleur, archet) : rejouer la note ne suffit pas. La boucle
    /// acoustique met des dizaines de millisecondes à reconstruire sa résonance depuis un état vidé,
    /// et l'attaque relancée est celle réglée par l'utilisateur, qui peut valoir plusieurs secondes.
    /// Il faut un geste dédié — voir <c>BlownFluteVoice.ReTongue</c>, qui atténue le résonateur au lieu
    /// de le vider et confie la forme de la note à une enveloppe d'articulation sur la sortie.</item>
    /// </list>
    /// </summary>
    public sealed class KotonReAttack
    {
        /// <summary>Taux de ré-attaque en Hz. 0 = désactivé.</summary>
        public KotonParameter Rate { get; }

        /// <summary>Variation aléatoire de vélocité appliquée aux coups suivant le premier (0 = aucune).
        /// Une répétition rigoureusement identique à 6 Hz s'entend comme une machine ; quelques pour cent
        /// suffisent à la rendre vivante.</summary>
        public float Humanize { get; set; } = 0.10f;

        /// <summary>Durée de remontée de l'enveloppe d'articulation, en secondes. 0 (défaut) = pas
        /// d'enveloppe, ce qui convient aux modèles PERCUSSIFS : chez eux rejouer la note suffit, leur
        /// attaque est instantanée par construction.
        ///
        /// Non nul = modèle AUTO-OSCILLANT (souffleur, archet). Là, rejouer la note ne marche pas : la
        /// boucle acoustique met des dizaines de ms à reconstruire sa résonance depuis un état vidé, et
        /// l'attaque relancée est celle réglée par l'utilisateur — mesuré sur la flûte, le niveau tombait
        /// de 0,16 à 0,017 dès 5 coups par seconde. Or physiquement un coup de langue n'interrompt PAS la
        /// résonance du tube : il coupe le débit d'air. On laisse donc l'instrument sonner sans
        /// interruption et on met en forme sa SORTIE — <see cref="Gain"/> tombe à zéro au coup et remonte
        /// en <see cref="ArticulationSec"/>. 0,009 s donne un détaché net sur un souffleur.</summary>
        public float ArticulationSec { get; set; }

        /// <summary>Niveau du BRUIT D'ARTICULATION (la consonne « t » du coup de langue, le grain du
        /// changement d'archet). 0 = aucun.
        ///
        /// Sans lui, la ré-attaque d'un modèle auto-oscillant n'est qu'un gate de volume : mesuré, le
        /// rapport aigu/total à l'attaque était identique à celui du régime établi (écart de −1 à +0,7 dB
        /// sur les sept instruments concernés), là où une vraie ré-attaque percussive donne +6,3 dB.
        /// L'amplitude repartait bien de zéro, mais l'oreille n'entendait pas une note réarticulée.
        ///
        /// Ce bruit est ajouté APRÈS le gain d'articulation, et c'est voulu : la langue fait son bruit
        /// pendant la coupure, pas après. Il n'est pas non plus filtré par le résonateur — sur un
        /// instrument réel il rayonne à l'embouchure sans traverser toute la perce.</summary>
        public float ChiffAmount { get; set; }

        /// <summary>Fond du creux d'articulation (0 = silence, 1 = rien). 0,30 par defaut : assez creux
        /// pour detacher, assez haut pour que le son ne soit jamais coupe — c'est ce qui distingue une
        /// note articulee d'un « tac ». Descendre vers 0 ramene le clic.</summary>
        public float DipLevel { get; set; } = 0.30f;

        /// <summary>Duree de la descente vers le creux. Une chute instantanee s'entend comme un clic ;
        /// 5 ms est assez rapide pour separer et assez lent pour rester musical.</summary>
        public float DipSec { get; set; } = 0.005f;

        /// <summary>Niveau tenu entre deux coups, legerement sous le maximum : c'est le « leger decay »
        /// qui suit chaque sursaut d'articulation, comme une note echantillonnee qui perd un peu de
        /// support jusqu'a la suivante.</summary>
        public float SagLevel { get; set; } = 0.88f;

        int _sr = 44100;
        double _phase;                       // avance de 0 à 1 sur une période
        float _art = 1f;
        float _fallCoef, _riseCoef, _sagCoef;
        int _dipLeft;            // echantillons restants de fermeture (phase descendante)
        float _chiff, _chiffDecay = 0.999f;  // enveloppe du bruit d'articulation
        float _lvl;                          // suiveur lent du niveau joué, pour caler le bruit sur la nuance
        float _chLp, _chHp;                  // passe-bande du bruit
        readonly List<Held> _held = new List<Held>();
        readonly Random _rng = new Random(12345);

        struct Held { public int Note; public int Velocity; }

        /// <param name="displayName">Libellé affiché — le terme juste pour la famille d'instrument
        /// (« Coup de langue », « Trémolo », « Détaché »…).</param>
        /// <param name="maxHz">Taux maximum. 20 Hz couvre du détaché large au frullato.</param>
        /// <param name="defaultHz">Défaut. Laisser à 0 pour qu'ajouter ce paramètre à un instrument
        /// existant ne change rien à ce qu'on entendait.</param>
        public KotonReAttack(string displayName = "Ré-attaque", double maxHz = 20.0, double defaultHz = 0.0)
        {
            Rate = new KotonParameter("retrig", displayName, 0.0, maxHz, defaultHz, "Hz");
        }

        public void Prepare(int sampleRate)
        {
            _sr = sampleRate <= 0 ? 44100 : sampleRate;
            // Bruit d'articulation : décroissance sur ~7 ms, la durée d'une consonne.
            _chiffDecay = (float)Math.Exp(-1.0 / (0.007 * _sr));
            // Pentes de l'enveloppe d'articulation. Filtres a un pole : pas d'angle, donc pas de clic.
            _fallCoef = 1f - (float)Math.Exp(-1.0 / Math.Max(1.0, DipSec * _sr * 0.35));
            _riseCoef = 1f - (float)Math.Exp(-1.0 / Math.Max(1.0, ArticulationSec * _sr * 0.5));
            _sagCoef  = 1f - (float)Math.Exp(-1.0 / Math.Max(1.0, 0.12 * _sr));
            Reset();
        }

        public void Reset()
        {
            _held.Clear();
            _phase = 0;
            _art = 1f;
            _dipLeft = 0;
            _chiff = 0f; _lvl = 0f; _chLp = 0f; _chHp = 0f;
        }

        /// <summary>À appeler depuis le <c>NoteOn</c> PUBLIC du plugin, avant de démarrer la voix.
        /// Remet la phase à zéro : le premier intervalle se compte depuis l'attaque réelle, sinon la
        /// première ré-attaque tomberait à un moment arbitraire.</summary>
        public void NoteOn(int note, int velocity)
        {
            if (_firing) return;
            if (velocity <= 0) { NoteOff(note); return; }
            for (int i = 0; i < _held.Count; i++)
                if (_held[i].Note == note) { _held[i] = new Held { Note = note, Velocity = velocity }; _phase = 0; return; }
            _held.Add(new Held { Note = note, Velocity = velocity });
            _phase = 0;
        }

        public void NoteOff(int note)
        {
            if (_firing) return;
            for (int i = 0; i < _held.Count; i++)
                if (_held[i].Note == note) { _held.RemoveAt(i); return; }
        }

        /// <summary>Avance d'un échantillon. Renvoie vrai à l'instant précis où il faut ré-attaquer —
        /// le plugin parcourt alors <see cref="Count"/> et redémarre chaque voix via <see cref="NoteAt"/>
        /// et <see cref="VelocityAt"/>.</summary>
        public bool Tick()
        {
            // Enveloppe d'articulation, en trois temps et SANS AUCUN ANGLE : elle descend vers un creux
            // PARTIEL, remonte, puis flechit doucement jusqu'au coup suivant.
            //
            // La version precedente tombait a zero instantanement puis remontait en rampe lineaire — deux
            // discontinuites, et on entendait un « tac » a chaque articulation au lieu d'une note. Un
            // detache reel ne coupe pas le son : il le creuse. Les trois pentes sont ici des filtres a un
            // pole, donc continues, et le son ne passe jamais par zero.
            if (ArticulationSec > 0f)
            {
                float target;
                if (_dipLeft > 0) { _dipLeft--; target = DipLevel; _art += (target - _art) * _fallCoef; }
                else if (_art < SagLevel - 1e-4f) { _art += (1f - _art) * _riseCoef; }
                else { _art += (SagLevel - _art) * _sagCoef; }
                if (_art > 1f) _art = 1f;
            }
            double rate = Rate.Value;
            // En dessous de 0,05 Hz la période dépasse 20 s : c'est l'arrêt, et on garde la phase à zéro
            // pour que réactiver le paramètre reparte proprement de l'attaque suivante.
            if (rate < 0.05 || _held.Count == 0) { _phase = 0; return false; }
            _phase += rate / _sr;
            if (_phase < 1.0) return false;
            _phase -= 1.0;
            if (ArticulationSec > 0f) _dipLeft = (int)(DipSec * _sr);
            if (ChiffAmount > 0f) _chiff = 1f;
            return true;
        }

        /// <summary>Gain d'articulation courant (0..1), à appliquer à la sortie du plugin quand
        /// <see cref="ArticulationSec"/> est non nul. Vaut toujours 1 sinon.</summary>
        public float Gain => _art;

        /// <summary>Échantillon de bruit d'articulation à AJOUTER à la sortie. À appeler exactement une
        /// fois par frame (il fait avancer l'enveloppe du bruit et le suiveur de niveau).
        /// <paramref name="levelRef"/> = amplitude courante de l'instrument AVANT le gain d'articulation,
        /// pour que le bruit suive la nuance : un coup de langue piano ne souffle pas comme un forte.</summary>
        public float Chiff(float levelRef)
        {
            if (ChiffAmount <= 0f) return 0f;
            float a = levelRef < 0 ? -levelRef : levelRef;
            _lvl += (a - _lvl) * 0.0004f;                 // ~55 ms
            if (_chiff <= 1e-4f) { _chiff = 0f; return 0f; }
            float n = (float)(_rng.NextDouble() * 2 - 1);
            // Passe-bande grossier 1,5-5 kHz : la bande où s'entend le bruit de langue et le grain d'archet.
            _chLp += 0.45f * (n - _chLp);
            _chHp += 0.10f * (_chLp - _chHp);
            float band = _chLp - _chHp;
            float outv = band * _chiff * _lvl * ChiffAmount * 4.0f;
            _chiff *= _chiffDecay;
            return outv;
        }


        // Vrai pendant l'exécution d'un coup. Rejouer une note fait forcément repasser le plugin par
        // ses propres NoteOff/NoteOn ; sans cette garde, chaque coup se retirerait lui-même de la liste
        // des notes tenues et remettrait la phase à zéro — plus aucune ré-attaque ne partirait.
        bool _firing;

        /// <summary>
        /// Exécute un coup sur chaque note tenue. Le plugin fournit le geste : dans l'immense majorité
        /// des cas <c>(n, v) =&gt; { NoteOff(n); NoteOn(n, v); }</c>, ce qui ne demande AUCUNE
        /// restructuration du plugin. Les notifications que ces appels renvoient ici sont neutralisées
        /// le temps du coup.
        ///
        /// Garder le délégué dans un champ du plugin plutôt que de l'écrire en lambda sur place : cette
        /// méthode est appelée depuis la boucle de rendu, une lambda capturante y allouerait à chaque coup.
        /// </summary>
        /// <summary>Ouvre/ferme la neutralisation à la main, quand le plugin préfère écrire sa boucle
        /// sur place plutôt que de passer un délégué :
        /// <c>if (_retrig.Tick()) { _retrig.BeginStroke(); for (…) NoteOn(…); _retrig.EndStroke(); }</c>.
        /// Même effet que <see cref="Fire"/>, sans allocation ni champ supplémentaire dans le plugin.</summary>
        public void BeginStroke() { _firing = true; }
        public void EndStroke() { _firing = false; }

        public void Fire(Action<int, int> stroke)
        {
            if (stroke == null || _held.Count == 0) return;
            _firing = true;
            try { for (int i = 0; i < _held.Count; i++) stroke(_held[i].Note, VelocityAt(i)); }
            finally { _firing = false; }
        }

        public int Count => _held.Count;
        public int NoteAt(int i) => _held[i].Note;

        /// <summary>Vélocité du coup, légèrement variée selon <see cref="Humanize"/>.</summary>
        public int VelocityAt(int i)
        {
            int v = _held[i].Velocity;
            if (Humanize > 0f)
            {
                double f = 1.0 - Humanize + _rng.NextDouble() * Humanize * 2.0;
                v = (int)Math.Round(v * f);
            }
            return v < 1 ? 1 : (v > 127 ? 127 : v);
        }
    }
}
