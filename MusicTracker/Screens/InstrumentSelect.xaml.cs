using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace MusicTracker.Screens
{
    /// <summary>
    /// Interaction logic for InstrumentSelect.xaml
    /// </summary>
    public partial class InstrumentSelect : Window
    {
        public Editor.Instrument SelectedInstrument { get; set; }
        Editor.Instrument newInstrument;
        public InstrumentSelect()
        {
            InitializeComponent();
            List<Editor.Instrument> instruments = UserData.Instance.InstrumentList.ToList();
            newInstrument = new Editor.Instrument { 
                ID = UserData.Instance.InstrumentList.Any()? UserData.Instance.InstrumentList.Max(x => x.ID) + 1 : 1,
                Name = "New instrument ...",
                WaveFunction= new Engine.SineWaveFunction()
            };
            instruments.Add(newInstrument);
            listInstrument.ItemsSource = instruments;
        }

        private void listInstrument_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedInstrument = (Editor.Instrument)listInstrument.SelectedItem;
            txtInstrumentName.Text = SelectedInstrument?.Name;
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            SelectedInstrument = null;
            this.DialogResult = false;
            this.Close();
        }

        private void btnOK_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedInstrument != null)
            {
                SelectedInstrument.Name = txtInstrumentName.Text;
                if (SelectedInstrument == newInstrument)
                {
                    UserData.Instance.InstrumentList.Add(SelectedInstrument);
                }
                this.DialogResult = true;
                this.Close();
            }
        }
    }
}
