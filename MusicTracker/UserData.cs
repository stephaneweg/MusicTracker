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
        static UserData _instance = Load();
        public static UserData Instance
        {
            get
            {
                return _instance;
            }
        }

        public void Save()
        {
            try { System.IO.File.WriteAllText(AppPaths.Local("userdata.json"), System.Text.Json.JsonSerializer.Serialize(this)); }
            catch { /* best-effort */ }
        }
        public static UserData Load()
        {
            UserData result = new UserData();
            string path = AppPaths.Local("userdata.json");
            if (System.IO.File.Exists(path))
            {
                result = System.Text.Json.JsonSerializer.Deserialize<UserData>(System.IO.File.ReadAllText(path));
            }

            return result;
        }


    }
}
