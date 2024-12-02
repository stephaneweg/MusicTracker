using Microsoft.Win32;
using MusicTracker.Engine;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Runtime.Remoting.Channels;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MusicTracker.Screens
{
    /// <summary>
    /// Interaction logic for TrackEditorScreen.xaml
    /// </summary>
    public partial class TrackEditorScreen : UserControl
    {
        
        public ProjectTrack Track
        {
            get { return waveProvider.Track; }
            set
            {
                //if (value != waveProvider.Track)
                {
                    waveProvider.Track = value;
                    TrackChanged();
                }
            }
        }

        List<TextBlock[]> noteTexts = new List<TextBlock[]>();

        int selectedChannel = -1;
        int selectedNote = -1;
        int selectedOctave = 4;

        List<int> sharpsInTrack = new List<int>();
        SolidColorBrush selectedBrush = new SolidColorBrush(Colors.Yellow);
        SolidColorBrush themeBackground = new SolidColorBrush(Color.FromRgb(51, 51, 51));

        Dictionary<Key, int> NoteValues = new Dictionary<Key, int>
        {
            { Key.C,1 },
            { Key.D,3 },
            { Key.E,5 },
            { Key.F,6 },
            { Key.G,8 },
            { Key.A,10 },
            { Key.B,12 }
        };

        Dictionary<int, string> NoteStrings = new Dictionary<int, string>
        {
            {0,"C" },
            {1,"C#" },
            {2,"D" },
            {3,"D#" },
            {4,"E" },
            {5,"F" },
            {6,"F#" },
            {7,"G" },
            {8,"G#" },
            {9,"A" },
            {10,"A#" },
            {11,"B" }
        };


        WaveOut waveOut = new WaveOut();
        Engine.WaveProvider waveProvider;
        public TrackEditorScreen()
        {
            waveProvider = new Engine.WaveProvider(null);
            waveProvider.NoteSelected = SelectNote;
            waveOut.DesiredLatency = 100;
            waveOut.Init(waveProvider);

            this.Foreground = new SolidColorBrush(color: Color.FromRgb(136, 136, 136));


            InitializeComponent();

            NewMusic();

        }

       


        public void AddChannel()
        {
            Track.Channels.Add(new TrackChannel { WaveFunction = new Engine.SineWaveFunction(), Name = "Default" });
            ChannelAdded();
        }

        public void RemoveChannel()
        {
            if (selectedChannel >= 0 && selectedChannel < Track.Channels.Count)
            {
                Track.Channels.RemoveAt(selectedChannel);
                noteTexts.RemoveAt(selectedChannel);
                sharpsInTrack.RemoveAt(selectedChannel);
                List<FrameworkElement> toRemove = new List<FrameworkElement>();
                foreach (FrameworkElement h in columnHeaderGrid.Children)
                {
                    if (Grid.GetColumn(h) == selectedChannel)
                    {
                        toRemove.Add(h);
                    }
                    else if (Grid.GetColumn(h) > selectedChannel)
                    {
                        Grid.SetColumn(h, Grid.GetColumn(h) - 1);
                        h.Tag = (Grid.GetColumn(h)).ToString();
                    }
                }
                foreach (FrameworkElement c in contentGrid.Children)
                {
                    var col = Grid.GetColumn(c);
                    if (col == selectedChannel)
                    {
                        toRemove.Add(c);
                    }
                    else if (col > selectedChannel)
                    {
                        Grid.SetColumn(c, Grid.GetColumn(c) - 1);
                    }

                }
                toRemove.ForEach(u => (u.Parent as Grid).Children.Remove(u));

                contentGrid.ColumnDefinitions.RemoveAt(selectedChannel);
                columnHeaderGrid.ColumnDefinitions.RemoveAt(selectedChannel);


                if (selectedChannel >= Track.Channels.Count) { selectedChannel = selectedChannel - 1; };
                SelectChannel(selectedChannel);
                SelectNote(selectedNote);
            }
        }

        public void ChannelAdded()
        {

            sharpsInTrack.Add(0);

            int channelIndex = noteTexts.Count;
            var channel = Track.Channels[channelIndex];

            columnHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });


            Button btnEditInstrument = new Button { Content = "Edit Instr.", Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch };
            btnEditInstrument.Click += btnEditInstrument_Click;
            btnEditInstrument.Tag = channelIndex.ToString();
            Grid.SetRow(btnEditInstrument, 0);
            Grid.SetColumn(btnEditInstrument, channelIndex);
            columnHeaderGrid.Children.Add(btnEditInstrument);

            Border brdHeader = new Border { BorderBrush = this.Foreground, BorderThickness = new Thickness(0.5), Background = this.themeBackground };
            Grid.SetRow(brdHeader, 1);
            Grid.SetColumn(brdHeader, channelIndex);
            columnHeaderGrid.Children.Add(brdHeader);

            TextBox txtHeader = new TextBox { TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Foreground = this.Foreground, Text = channel.Name, Background = Brushes.Transparent };
            brdHeader.Child = txtHeader;



            TextBlock[] textBlocks = new TextBlock[Track.NoteCount];
            noteTexts.Add(textBlocks);
            for (int i = 0; i < Track.NoteCount; i++)
            {
                TextBlock textBlock = new TextBlock { Text = "", Margin = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                Border border = new Border { BorderBrush = this.Foreground, BorderThickness = new Thickness(0.5), Background = this.themeBackground };
                border.Child = textBlock;
                Grid.SetRow(border, i);
                Grid.SetColumn(border, channelIndex);
                contentGrid.Children.Add(border);
                border.MouseDown += (s, e) =>
                {
                    if (e.LeftButton == MouseButtonState.Pressed)
                    {
                        SelectChannel(Grid.GetColumn(s as Border));
                        SelectNote(Grid.GetRow(s as Border));
                    }
                };
                textBlocks[i] = textBlock;

                updateNoteText(channelIndex, i);
            }
        }





        public void NewMusic()
        {
            var newTrack = new ProjectTrack();
            Track = newTrack;
            for(int i=0;i<4;i++)
            {
                AddChannel();
            }
        }

        public void TrackChanged()
        {
            noteTexts.Clear();
            sharpsInTrack = new List<int>();
            waveProvider.Track = Track;
            columnHeaderGrid.Children.Clear();
            columnHeaderGrid.ColumnDefinitions.Clear();
            contentGrid.Children.Clear();
            contentGrid.RowDefinitions.Clear();
            contentGrid.ColumnDefinitions.Clear();
            rowHeaderGrid.RowDefinitions.Clear();
            rowHeaderGrid.Children.Clear();

            for (int x = 0; x < Track.NoteCount; x++)
            {
                rowHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
                contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
                TextBlock textBlock = new TextBlock { Text = $"&H{x.ToString("X4")}", Margin = new Thickness(0, 0, 5, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Border border = new Border { BorderBrush = this.Foreground, BorderThickness = new Thickness(0.5), Background = this.themeBackground };
                border.Child = textBlock;
                Grid.SetRow(border, x);
                rowHeaderGrid.Children.Add(border);
            }
            foreach (var c in Track.Channels)
            {
                ChannelAdded();
            }
        }





        public void SetTrackColor(int i, Brush brush)
        {
            if (i >= 0 && i < Track.Channels.Count)
            {
                for (int j = 0; j < Track.NoteCount; j++)
                {
                    noteTexts[i][j].Foreground = brush;
                    (noteTexts[i][j].Parent as Border).BorderBrush = brush;
                }
            }
            columnHeaderGrid.Children.OfType<Border>().ElementAt(i).BorderBrush = brush;
            (columnHeaderGrid.Children.OfType<Border>().ElementAt(i).Child as TextBox).Foreground = brush;
        }



        public void SelectChannel(int i)
        {
            if (selectedChannel >= 0 && selectedChannel < Track.Channels.Count)
            {
                SetTrackColor(selectedChannel, this.Foreground);
            }
            selectedChannel = i;
            if (selectedChannel >= 0 && selectedChannel < Track.Channels.Count)
            {
                SetTrackColor(selectedChannel, selectedBrush);

                var posX = noteTexts[selectedChannel][0].TransformToAncestor(contentGrid).Transform(new Point(0, 0)).X;
                if (posX - 10 < contentScroll.HorizontalOffset )
                {
                    contentScroll.ScrollToHorizontalOffset(posX-10);
                }
                if (posX+110 > contentScroll.HorizontalOffset + contentScroll.ActualWidth)
                {
                    contentScroll.ScrollToHorizontalOffset(posX - contentScroll.ActualWidth+110);
                }
            }
        }

        public void SelectNote(int i)
        {
            if (selectedNote >= 0 && selectedNote < Track.NoteCount)
            {
                for (int j = 0; j < Track.Channels.Count; j++)
                {
                    if (j == selectedChannel)
                    {
                        noteTexts[j][selectedNote].Foreground = selectedBrush;
                        (noteTexts[j][selectedNote].Parent as Border).BorderBrush = selectedBrush;
                    }
                    else
                    {
                        noteTexts[j][selectedNote].Foreground = this.Foreground;
                        (noteTexts[j][selectedNote].Parent as Border).BorderBrush = this.Foreground;
                    }
                    noteTexts[j][selectedNote].FontWeight = FontWeights.Normal;
                    (noteTexts[j][selectedNote].Parent as Border).BorderThickness = new Thickness(0.5);
                }
            }
            selectedNote = i;
            if (selectedNote >= 0 && selectedNote < Track.NoteCount)
            {
                for (int j = 0; j < Track.Channels.Count; j++)
                {
                    noteTexts[j][selectedNote].Foreground = Brushes.White;
                    (noteTexts[j][selectedNote].Parent as Border).BorderBrush = Brushes.White;

                    if (j == selectedChannel)
                    {
                        noteTexts[j][selectedNote].FontWeight = FontWeights.Bold;
                        (noteTexts[j][selectedNote].Parent as Border).BorderThickness = new Thickness(2);
                    }
                }
                if (Track.Channels.Count > 0)
                {
                    var posY = noteTexts[0][selectedNote].TransformToAncestor(contentGrid).Transform(new Point(0, 0)).Y;
                    if (posY - 10 < contentScroll.VerticalOffset)
                    {
                        contentScroll.ScrollToVerticalOffset(posY - 10);
                    }
                    if (posY + 40 > contentScroll.VerticalOffset + contentScroll.ActualHeight)
                    {
                        contentScroll.ScrollToVerticalOffset(posY - contentScroll.ActualHeight + 40);
                    }
                }
                else
                {
                    contentScroll.ScrollToHorizontalOffset(0);
                    contentScroll.ScrollToVerticalOffset(0);
                }
            }
        }


        private void contentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            rowHeaderScroll.ScrollToVerticalOffset(contentScroll.VerticalOffset);
            columnHeaderScroll.ScrollToHorizontalOffset(contentScroll.HorizontalOffset);
        }

        private void btnEditInstrument_Click(object sender, RoutedEventArgs e)
        {
            InstrumentEditorScreen editor = new InstrumentEditorScreen();

            editor.SetWaveFunction(Track.Channels[Grid.GetColumn(sender as Button)].WaveFunction);
            editor.ShowDialog();
            Track.Channels[Grid.GetColumn(sender as Button)].WaveFunction = editor.WaveFunction.Clone();
        }


        private bool hasTextBoxKeyFocus()
        {
           return columnHeaderGrid.Children.OfType<Border>().Select(b => b.Child).OfType<TextBox>().Any(t => t.IsFocused || t.IsKeyboardFocusWithin);
        }
        private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (hasTextBoxKeyFocus())
            {
                return;
            }
            if (e.Key == Key.Decimal)
            {
                toggleDot.IsChecked = !toggleDot.IsChecked;
            }
            if (e.Key == Key.NumPad1)
            {
                listDuration.SelectedIndex = 0;
            }
            if (e.Key == Key.NumPad2)
            {
                listDuration.SelectedIndex = 1;
            }
            if (e.Key == Key.NumPad3)
            {
                listDuration.SelectedIndex = 2;
            }
            if (e.Key == Key.NumPad4)
            {
                listDuration.SelectedIndex = 3;
            }
            if (e.Key == Key.NumPad5)
            {
                listDuration.SelectedIndex = 4;
            }

            if (e.Key == Key.Down && selectedNote < 255)
            {
                SelectNote(selectedNote + 1);
                e.Handled = true;
            }
            if (e.Key == Key.Up && selectedNote > 0)
            {
                SelectNote(selectedNote - 1);
                e.Handled = true;
            }
            if (e.Key == Key.Right && selectedChannel < Track.Channels.Count - 1)
            {
                SelectChannel(selectedChannel + 1);
                SelectNote(selectedNote);
                e.Handled = true;
            }
            if (e.Key == Key.Left && selectedChannel > 0)
            {
                SelectChannel(selectedChannel - 1);
                SelectNote(selectedNote);
                e.Handled = true;
            }

            if (selectedChannel >= 0 && selectedChannel < Track.Channels.Count && selectedNote >= 0 && selectedNote < Track.NoteCount)
            {
                if (NoteValues.ContainsKey(e.Key))
                {
                    int note = NoteValues[e.Key] + selectedOctave * 12;
                    if ((sharpsInTrack[selectedChannel] & (1 << NoteValues[e.Key] - 1)) > 0)
                    {
                        note += 1;
                    }
                    setNote(note);
                    var interval = (int)Math.Pow(2, listDuration.SelectedIndex);
                    if (interval > 1 && toggleDot.IsChecked == true)
                    {
                        interval = (int)(interval * 1.5);
                    }
                    SelectNote(selectedNote + interval);
                    e.Handled = true;
                }
                if (e.Key == Key.Space)
                {
                    setNote(-1);
                    e.Handled = true;
                }
                if (e.Key == Key.Back)
                {
                    setNote(0);
                    e.Handled = true;
                }
                if (e.Key == Key.Add)
                {
                    int noteNum = Track.note(selectedChannel, selectedNote);
                    if (noteNum > 0)
                    {
                        setNote(noteNum + 12);
                        selectedOctave = (noteNum - 1) / 12;
                    }
                    e.Handled = true;
                }
                if (e.Key == Key.Subtract)
                {
                    int noteNum = Track.note(selectedChannel, selectedNote);
                    if (noteNum > 12)
                    {
                        setNote(noteNum - 12);
                        selectedOctave = (noteNum - 1) / 12;
                    }
                    e.Handled = true;
                }
                if (e.Key == Key.Multiply)
                {
                    int noteNum = ((Track.note(selectedChannel, selectedNote) - 1) % 12);
                    if (noteNum == 1 || noteNum == 3 || noteNum == 6 || noteNum == 8 || noteNum == 10)
                    {
                        sharpsInTrack[selectedChannel] ^= (1 << (noteNum - 1));
                        setNote(Track.note(selectedChannel, selectedNote) - 1);
                    }
                    else if (noteNum == 0 || noteNum == 2 || noteNum == 5 || noteNum == 7 || noteNum == 9 || noteNum == 11)
                    {
                        sharpsInTrack[selectedChannel] |= 1 << noteNum;
                        setNote(Track.note(selectedChannel, selectedNote) + 1);
                    }

                }
            }

        }

        private void setNote(int noteNum)
        {
            Track.setNote(selectedChannel, selectedNote, noteNum);
            updateNoteText(selectedChannel, selectedNote);
        }

        private void updateNoteText(int i, int j)
        {
            int noteNum = Track.note(i, j);
            if (noteNum > 1)
            {
                string noteString = NoteStrings[(noteNum - 1) % 12];
                int octave = (noteNum - 1) / 12;
                noteTexts[i][j].Text = $"{noteString}{octave}";
            }
            else if (noteNum == 0)
            {
                noteTexts[i][j].Text = "------";
            }
            else
            {
                noteTexts[i][j].Text = "";
            }
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (selectedChannel >= 0 && selectedChannel < Track.Channels.Count)
            {

                var posX = noteTexts[selectedChannel][0].TransformToAncestor(contentGrid).Transform(new Point(0, 0)).X;
                if (posX - contentScroll.HorizontalOffset < 0)
                {
                    contentScroll.ScrollToHorizontalOffset(posX);
                }
                if (posX - contentScroll.HorizontalOffset > contentScroll.ActualWidth - 160)
                {
                    contentScroll.ScrollToHorizontalOffset(posX - contentScroll.HorizontalOffset + 160);
                }
            }
            if (selectedNote >= 0 && selectedNote < Track.NoteCount)
            {
                var posY = noteTexts[0][selectedNote].TransformToAncestor(contentGrid).Transform(new Point(0, 0)).Y;
                if (posY - contentScroll.VerticalOffset < 0)
                {
                    contentScroll.ScrollToVerticalOffset(posY);
                }
                if (posY - contentScroll.VerticalOffset > contentScroll.ActualHeight - 60)
                {
                    contentScroll.ScrollToVerticalOffset(posY - contentScroll.ActualHeight + 60);
                }
            }
        }


        private void startPlay()
        {
            if (!waveProvider.Playing)
            {
                menuPlay.Header = "Stop";
                waveProvider.Start();
                waveOut.Play();
            }

        }
        private void stopPlay()
        {
            if (waveProvider.Playing)
            {
                menuPlay.Header = "Start";
                waveProvider.Stop(); 
                waveOut.Stop();
            }
        }
        private void menuPlay_Click(object sender, RoutedEventArgs e)
        {
            waveProvider.BMP = 120;
            SelectNote(0);
            if (!waveProvider.Playing)
            {
                startPlay();
            }
            else
            {
                stopPlay();
            }

        }


        private void menuImportFMS_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "FMS files (*.fms)|*.fms|All files (*.*)|*.*";
            if (dlg.ShowDialog() == true)
            {
                Engine.FMSMusic music = Engine.FMSMusic.Import(dlg.FileName);
                if (music != null)
                {
                    var newTrack = new ProjectTrack();
                    waveProvider.Track = newTrack;
                    newTrack.Channels.Clear();
                    newTrack.NoteCount = music.noteIdx[0];


                    int x = 0;
                    foreach (var m in music.Instruments)
                    {
                        newTrack.Channels.Add(new TrackChannel { WaveFunction = m, Name = music.InstrumentNames[x], });
                        x++;
                    }
                    for (int i = 0; i < newTrack.Channels.Count; i++)
                    {
                        for (int j = 0; j < music.Notes[i].Count; j++)
                        {
                            newTrack.setNote(i, j, music.Notes[i][j]);
                        }
                    }

                    Track = newTrack;


                }
            }
        }

        private void menuAddChannel_Click(object sender, RoutedEventArgs e)
        {
            AddChannel();
        }

        private void menuRemoveChannel_Click(object sender, RoutedEventArgs e)
        {
            RemoveChannel();
        }
    }
}
