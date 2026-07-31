using System;

namespace KotonStudio.Library
{
    /// <summary>
    /// Marque une classe comme instrument Koton découvrable. La classe doit :
    /// - implémenter <see cref="IKotonInstrument"/>
    /// - avoir un constructeur public sans paramètre
    /// - vivre dans un assembly <c>.ksl</c> déposé dans le dossier <c>plugins/</c> de l'hôte
    ///
    /// <see cref="DisplayName"/> apparaît dans le menu de sélection ; <see cref="Category"/> est réservé
    /// pour un futur regroupement (v1 = aplati). <see cref="Version"/> et <see cref="Vendor"/> sont
    /// informatifs (affichés dans un tooltip / About éventuel).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class KotonInstrumentAttribute : Attribute
    {
        public string DisplayName { get; }
        /// <summary>Id stable optionnel — si absent, le registre fallback sur le FullName du type. Permet
        /// à un plugin de renommer sa classe sans casser les projets sauvegardés qui référencent l'Id.</summary>
        public string Id { get; set; }
        public string Category { get; set; } = "";
        public string Version { get; set; } = "1.0";
        public string Vendor { get; set; } = "";

        public KotonInstrumentAttribute(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                throw new ArgumentException("displayName required", nameof(displayName));
            DisplayName = displayName;
        }
    }

    /// <summary>
    /// Marque une classe comme effet d'insert Koton découvrable. Mêmes règles que
    /// <see cref="KotonInstrumentAttribute"/>, mais la classe doit implémenter <see cref="IKotonEffect"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class KotonEffectAttribute : Attribute
    {
        public string DisplayName { get; }
        /// <summary>Id stable optionnel — cf. <see cref="KotonInstrumentAttribute.Id"/>.</summary>
        public string Id { get; set; }
        public string Category { get; set; } = "";
        public string Version { get; set; } = "1.0";
        public string Vendor { get; set; } = "";

        public KotonEffectAttribute(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                throw new ArgumentException("displayName required", nameof(displayName));
            DisplayName = displayName;
        }
    }

    /// <summary>
    /// Marque un preset intégré (méthode ou champ statique lisible) — l'hôte peut lister les presets sans
    /// instancier le plugin ni ouvrir son éditeur. Actuellement non utilisé par l'hôte v1 (les presets
    /// sont chargés depuis l'éditeur), mais l'attribut existe pour permettre à un futur hôte de peupler
    /// un menu "presets" directement dans le header de piste. Placé ici pour que le contrat reste stable
    /// dès la v1.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Field | AttributeTargets.Property,
                    AllowMultiple = true, Inherited = false)]
    public sealed class KotonPresetAttribute : Attribute
    {
        public string Name { get; }

        public KotonPresetAttribute(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("name required", nameof(name));
            Name = name;
        }
    }
}
