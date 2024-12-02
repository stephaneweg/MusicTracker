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
            System.IO.File.WriteAllText("userdata.json", System.Text.Json.JsonSerializer.Serialize(this));
        }
        public static UserData Load()
        {
            UserData result = new UserData();
            if (System.IO.File.Exists("userdata.json"))
            {
                result = System.Text.Json.JsonSerializer.Deserialize<UserData>(System.IO.File.ReadAllText("userdata.json"));
            }
            return result;
        }

        public ObservableCollection<Editor.Instrument> InstrumentList { get; set; } = new ObservableCollection<Editor.Instrument>();

    }
}
