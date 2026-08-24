using System;
using System.Collections.Generic;
using KotonStudio.Library;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Fait le pont entre une <see cref="PluginAutomationLane"/> (donnée sérialisée : « le paramètre
    /// <c>mod_depth</c> du 2e insert ») et le <see cref="KotonParameter"/> VIVANT correspondant, plus
    /// l'énumération des cibles automatisables d'une piste ou du bus master (pour peupler les menus).
    ///
    /// **Une seule source de vérité pour les instances** : la résolution passe par
    /// <see cref="KotonInstrumentCache"/> / <see cref="KotonEffectCache"/>, exactement comme le renderer
    /// et les éditeurs de plugin. Écrire sur le paramètre résolu ici, c'est écrire sur celui que le
    /// moteur audio lit au buffer suivant — et le curseur de l'éditeur ouvert bouge tout seul (le
    /// <see cref="KotonParameter.Changed"/> le notifie).
    ///
    /// **Tolérance aux projets désynchronisés** : un insert supprimé, réordonné ou remplacé par un autre
    /// plugin depuis la dernière sauvegarde fait simplement retourner <c>null</c> à
    /// <see cref="Resolve"/> — la lane survit dans le fichier et reste visible dans la timeline (pour que
    /// l'utilisateur la voie et la supprime), mais elle ne pilote rien.
    /// </summary>
    public static class PluginAutomation
    {
        /// <summary>Un plugin Koton automatisable trouvé sur une piste ou sur le master, avec ses paramètres.</summary>
        public sealed class Target
        {
            public PluginAutomationSlot Slot;
            public int InsertIndex = -1;          // -1 pour l'instrument
            public string PluginId;
            public string PluginName;
            public IReadOnlyList<KotonParameter> Params;
        }

        /// <summary>Cibles automatisables d'une piste : son instrument Koton (s'il y en a un) puis chacun de
        /// ses inserts Koton, dans l'ordre de la chaîne. Les inserts non-Koton (EQ maison, VST) sont ignorés :
        /// ils n'exposent pas de métadonnées de paramètres exploitables ici.</summary>
        public static List<Target> TargetsForTrack(TimelineTrack track, int sampleRate)
        {
            var list = new List<Target>();
            if (track == null) return list;
            if (!string.IsNullOrEmpty(track.KotonInstrumentId))
            {
                var adapter = KotonInstrumentCache.GetOrCreate(track, track.KotonInstrumentId, sampleRate);
                var ps = AutomatableParams(adapter?.Plugin);
                if (ps != null && ps.Count > 0)
                    list.Add(new Target
                    {
                        Slot = PluginAutomationSlot.Instrument,
                        InsertIndex = -1,
                        PluginId = track.KotonInstrumentId,
                        PluginName = adapter.DisplayName,
                        Params = ps,
                    });
            }
            AddInsertTargets(list, track.Inserts, sampleRate);
            return list;
        }

        /// <summary>Cibles automatisables du bus master : ses inserts Koton (le master n'a pas d'instrument).</summary>
        public static List<Target> TargetsForMaster(TimelineProject project, int sampleRate)
        {
            var list = new List<Target>();
            if (project != null) AddInsertTargets(list, project.MasterInserts, sampleRate);
            return list;
        }

        static void AddInsertTargets(List<Target> list, List<TrackEffectData> inserts, int sampleRate)
        {
            if (inserts == null) return;
            for (int i = 0; i < inserts.Count; i++)
            {
                var d = inserts[i];
                if (d == null || d.Kind != EffectFactory.KotonKind) continue;
                var adapter = KotonEffectCache.GetOrCreate(d, sampleRate);
                var ps = AutomatableParams(adapter?.Plugin);
                if (ps == null || ps.Count == 0) continue;
                list.Add(new Target
                {
                    Slot = PluginAutomationSlot.Insert,
                    InsertIndex = i,
                    PluginId = d.PluginPath,
                    PluginName = adapter.DisplayName,
                    Params = ps,
                });
            }
        }

        static IReadOnlyList<KotonParameter> SafeParams(IKotonPlugin p)
        {
            if (p == null) return null;
            try { return p.Parameters; }
            catch { return null; }
        }

        /// <summary>Les paramètres qu'une courbe peut piloter : ceux qui décrivent une PLAGE CONTINUE.
        /// Un sélecteur d'instrument, de forme d'onde ou de mode est exclu — ses valeurs sont des
        /// étiquettes et non des quantités, une courbe ne ferait que le faire sauter d'une option à
        /// l'autre en cours de note (voir <see cref="KotonParameter.Automatable"/>).</summary>
        static IReadOnlyList<KotonParameter> AutomatableParams(IKotonPlugin p)
        {
            var all = SafeParams(p);
            if (all == null) return null;
            var list = new List<KotonParameter>(all.Count);
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].Automatable) list.Add(all[i]);
            return list;
        }

        /// <summary>Fabrique une lane pour ce paramètre de cette cible. La courbe démarre VIDE et sa valeur
        /// tenue (<see cref="PluginAutomationLane.DefaultNorm"/>) est la valeur COURANTE du paramètre dans le
        /// plugin — ajouter la lane ne change donc rien à ce qu'on entendait.</summary>
        public static PluginAutomationLane CreateLane(Target t, KotonParameter p)
        {
            double span = p.Max - p.Min;
            return new PluginAutomationLane
            {
                Slot = t.Slot,
                InsertIndex = t.Slot == PluginAutomationSlot.Insert ? t.InsertIndex : -1,
                ParamId = p.Id,
                PluginId = t.PluginId,
                PluginName = t.PluginName,
                ParamName = p.Name,
                Min = p.Min,
                Max = p.Max,
                Unit = p.Unit,
                DefaultNorm = span > 1e-12 ? Clamp01((p.Value - p.Min) / span) : 0.0,
                Enabled = true,
            };
        }

        /// <summary>Vrai si une lane pilote déjà ce paramètre de cette cible (pour griser l'entrée de menu).</summary>
        public static bool Exists(IEnumerable<PluginAutomationLane> lanes, Target t, KotonParameter p)
        {
            if (lanes == null) return false;
            foreach (var l in lanes)
            {
                if (l == null || !string.Equals(l.ParamId, p.Id, StringComparison.Ordinal)) continue;
                if (l.Slot != t.Slot) continue;
                if (t.Slot == PluginAutomationSlot.Insert && l.InsertIndex != t.InsertIndex) continue;
                return true;
            }
            return false;
        }

        /// <summary>Le <see cref="KotonParameter"/> vivant visé par la lane, ou <c>null</c> si la cible n'existe
        /// plus (insert supprimé, plugin remplacé, paramètre disparu d'une nouvelle version du plugin).
        /// <paramref name="track"/> vaut <c>null</c> pour une lane du bus master.</summary>
        public static KotonParameter Resolve(PluginAutomationLane lane, TimelineTrack track, TimelineProject project, int sampleRate)
        {
            if (lane == null || string.IsNullOrEmpty(lane.ParamId)) return null;
            IKotonPlugin plugin = null;
            if (lane.Slot == PluginAutomationSlot.Instrument)
            {
                if (track == null || string.IsNullOrEmpty(track.KotonInstrumentId)) return null;
                // Le plugin a changé depuis la création de la lane : on ne pilote pas un homonyme par accident.
                if (!string.IsNullOrEmpty(lane.PluginId) && !string.Equals(lane.PluginId, track.KotonInstrumentId, StringComparison.Ordinal)) return null;
                plugin = KotonInstrumentCache.GetOrCreate(track, track.KotonInstrumentId, sampleRate)?.Plugin;
            }
            else
            {
                var inserts = track != null ? track.Inserts : project?.MasterInserts;
                if (inserts == null || lane.InsertIndex < 0 || lane.InsertIndex >= inserts.Count) return null;
                var d = inserts[lane.InsertIndex];
                if (d == null || d.Kind != EffectFactory.KotonKind) return null;
                if (!string.IsNullOrEmpty(lane.PluginId) && !string.Equals(lane.PluginId, d.PluginPath, StringComparison.Ordinal)) return null;
                plugin = KotonEffectCache.GetOrCreate(d, sampleRate)?.Plugin;
            }
            var ps = SafeParams(plugin);
            if (ps == null) return null;
            for (int i = 0; i < ps.Count; i++)
                if (string.Equals(ps[i].Id, lane.ParamId, StringComparison.Ordinal)) return ps[i];
            return null;
        }

        /// <summary>Recale les <see cref="PluginAutomationLane.InsertIndex"/> devenus faux parce que la chaîne
        /// d'inserts a bougé depuis la dernière fois (un effet retiré ou déplacé décale tous les suivants).
        /// Une lane est recalée quand la chaîne contient EXACTEMENT UN insert du plugin qu'elle vise : le
        /// rattachement est alors sans ambiguïté. S'il y en a deux (deux réverbes du même modèle en série),
        /// on ne devine pas — la lane garde son index, qui reste le meilleur pari.</summary>
        public static void Fixup(List<PluginAutomationLane> lanes, List<TrackEffectData> inserts)
        {
            if (lanes == null || inserts == null) return;
            foreach (var l in lanes)
            {
                if (l == null || l.Slot != PluginAutomationSlot.Insert || string.IsNullOrEmpty(l.PluginId)) continue;
                if (IsKotonAt(inserts, l.InsertIndex, l.PluginId)) continue;   // index encore juste : rien à faire
                int found = -1, count = 0;
                for (int i = 0; i < inserts.Count; i++)
                    if (IsKotonAt(inserts, i, l.PluginId)) { count++; found = i; }
                if (count == 1) l.InsertIndex = found;
            }
        }

        /// <summary>Recale toutes les lanes du projet (chaque piste + le bus master).</summary>
        public static void FixupAll(TimelineProject project)
        {
            if (project == null) return;
            if (project.Tracks != null)
                foreach (var t in project.Tracks)
                    if (t != null) Fixup(t.PluginAutomationLanes, t.Inserts);
            Fixup(project.MasterAutomationLanes, project.MasterInserts);
        }

        static bool IsKotonAt(List<TrackEffectData> inserts, int index, string pluginId)
        {
            if (index < 0 || index >= inserts.Count) return false;
            var d = inserts[index];
            return d != null && d.Kind == EffectFactory.KotonKind && string.Equals(d.PluginPath, pluginId, StringComparison.Ordinal);
        }

        /// <summary>Étiquette affichée sur la lane : « Aurora Shimmer · Shimmer ». Lue depuis les libellés
        /// mémorisés dans la lane, donc lisible même si le plugin n'est plus installé.</summary>
        public static string Label(PluginAutomationLane lane)
        {
            if (lane == null) return "";
            string plug = !string.IsNullOrEmpty(lane.PluginName) ? lane.PluginName : (lane.PluginId ?? "?");
            string par = !string.IsNullOrEmpty(lane.ParamName) ? lane.ParamName : (lane.ParamId ?? "?");
            return plug + " · " + par;
        }

        /// <summary>Valeur réelle correspondant à une position normalisée de la courbe, avec l'unité — pour
        /// l'infobulle de la lane (« 0.62 → 4.3 kHz »).</summary>
        public static string FormatValue(PluginAutomationLane lane, double norm)
        {
            if (lane == null) return "";
            double v = lane.Min + Clamp01(norm) * (lane.Max - lane.Min);
            string s = Math.Abs(v) >= 100 ? v.ToString("0") : (Math.Abs(v) >= 10 ? v.ToString("0.#") : v.ToString("0.##"));
            return string.IsNullOrEmpty(lane.Unit) ? s : s + " " + lane.Unit;
        }

        static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }
}
