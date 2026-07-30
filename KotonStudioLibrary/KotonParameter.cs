using System;

namespace KotonStudio.Library
{
    /// <summary>
    /// Un paramètre exposé par un plugin Koton (slider ou combo, selon l'éditeur). Le plugin construit
    /// ses paramètres une fois pour toutes dans son constructeur et les expose via
    /// <see cref="IKotonPlugin.Parameters"/>. L'hôte peut lire/écrire <see cref="Value"/> ; l'éditeur
    /// du plugin observe <see cref="Changed"/> pour rafraîchir son UI.
    ///
    /// **Live update** : le plugin lit <see cref="Value"/> à chaque appel de Render/Process (pas de
    /// snapshot au démarrage). Bouger un slider pendant la lecture s'entend au prochain buffer audio
    /// (latence typique &lt; 500 ms selon le LookaheadBuffer côté hôte).
    ///
    /// **Bornes** : les valeurs affectées à <see cref="Value"/> sont CLAMPÉES entre <see cref="Min"/>
    /// et <see cref="Max"/> — pas d'exception si on affecte hors-plage. <see cref="Default"/> sert au
    /// bouton "reset" et à l'état initial.
    /// </summary>
    public sealed class KotonParameter
    {
        /// <summary>Identifiant stable inter-versions (utilisé par les blobs de sauvegarde, l'automation
        /// externe et l'affichage debug). Convention: snake_case court, ex. "ratio", "lfo_rate".</summary>
        public string Id { get; }

        /// <summary>Nom affichable dans l'éditeur (peut contenir des espaces et accents).</summary>
        public string Name { get; }

        /// <summary>Borne inférieure inclusive.</summary>
        public double Min { get; }

        /// <summary>Borne supérieure inclusive.</summary>
        public double Max { get; }

        /// <summary>Valeur par défaut (posée à la construction et utilisée par un bouton "reset").</summary>
        public double Default { get; }

        /// <summary>Suffixe d'unité affiché à côté de la valeur ("Hz", "dB", "%", "ms" — libre). Vide
        /// pour un paramètre adimensionnel (ratio, index).</summary>
        public string Unit { get; }

        double _value;

        /// <summary>Valeur courante. Les affectations HORS PLAGE sont clampées silencieusement
        /// (pas d'exception). Une affectation qui ne change rien n'émet pas <see cref="Changed"/> — la
        /// boucle éditeur → paramètre → éditeur reste stable même en 2-way binding.</summary>
        public double Value
        {
            get => _value;
            set
            {
                double v = value;
                if (v < Min) v = Min;
                else if (v > Max) v = Max;
                if (v != _value)
                {
                    _value = v;
                    Changed?.Invoke(v);
                }
            }
        }

        /// <summary>Levé quand <see cref="Value"/> change effectivement (une affectation identique est
        /// no-op). Utilisé par l'éditeur pour rafraîchir un slider quand l'automation externe pose la
        /// valeur.</summary>
        public event Action<double> Changed;

        /// <summary>Construit un paramètre borné avec sa valeur par défaut (clampée aux bornes si hors
        /// plage passée par erreur).</summary>
        public KotonParameter(string id, string name, double min, double max, double @default, string unit = "")
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("id required", nameof(id));
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
            if (min > max) throw new ArgumentException("min > max", nameof(min));
            Id = id;
            Name = name;
            Min = min;
            Max = max;
            Default = @default;
            Unit = unit ?? "";
            _value = @default < min ? min : (@default > max ? max : @default);
        }

        /// <summary>Remet la valeur au défaut (utile pour un bouton "reset" dans l'éditeur).</summary>
        public void ResetToDefault() => Value = Default;
    }
}
