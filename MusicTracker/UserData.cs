using MusicTracker.Engine;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MusicTracker
{
    public class UserData
    {
        /// <summary>True once the first-launch guided tour has been offered (so it isn't proposed again).</summary>
        public bool TutorialShown { get; set; } = false;

        // Chargement PARESSEUX, pas un initialiseur de champ statique : une exception dans un initialiseur
        // statique devient une TypeInitializationException que plus rien ne rattrape, et l'application ne démarre
        // tout simplement plus. C'était le cas avant — un userdata.json tronqué suffisait à la rendre
        // inutilisable, avec une boîte .NET brute pour toute explication.
        static UserData _instance;
        public static UserData Instance => _instance ?? (_instance = Load());

        public void Save()
        {
            try { Engine.SafeFile.WriteAllText(AppPaths.Roaming("userdata.json"), System.Text.Json.JsonSerializer.Serialize(this)); }
            catch { /* best-effort */ }
        }

        /// <summary>Ne lève JAMAIS et ne renvoie JAMAIS null : un fichier absent, tronqué ou illisible retombe sur
        /// les valeurs par défaut. Aligné sur AppSettings.Load et RecentFiles.Load, qui étaient déjà protégés.</summary>
        public static UserData Load()
        {
            try
            {
                string path = AppPaths.UserFile("userdata.json"); // roaming, migrating a legacy next-to-exe copy on first run
                if (System.IO.File.Exists(path))
                    return System.Text.Json.JsonSerializer.Deserialize<UserData>(System.IO.File.ReadAllText(path))
                           ?? new UserData();                      // "null" est un JSON valide : il ne doit pas passer
            }
            catch { /* fichier corrompu → valeurs par défaut */ }
            return new UserData();
        }


    }
}
