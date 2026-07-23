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

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// Interaction logic for NumberDialog.xaml
    /// </summary>
    public partial class NumberDialog : Window
    {
        public NumberDialog()
        {
            InitializeComponent();
            this.DataContext = this;
            titleBar.MouseLeftButtonDown += (a, b) => { if (b.ButtonState == MouseButtonState.Pressed) DragMove(); };
        }

        public int Value { get; set; } = 0;
        public string Message { get; set; } = "Enter a number:";


        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void btnOK_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }
    }
}

