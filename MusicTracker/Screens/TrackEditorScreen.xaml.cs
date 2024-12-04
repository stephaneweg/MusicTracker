using Microsoft.Win32;
using MusicTracker.Engine;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Runtime.Remoting.Channels;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        Engine.TrackWaveProvider waveProvider;
        public TrackEditorScreen()
        {
            waveProvider = new Engine.TrackWaveProvider(null);

            waveProvider.NoteSelected = SelectNote;
            waveOut.DesiredLatency = 100;
            waveOut.Init(waveProvider);

            this.Foreground = new SolidColorBrush(color: Color.FromRgb(136, 136, 136));


            InitializeComponent();
            headerPressets.ItemsSource = UserData.Instance.InstrumentList;
            comboBPM.DataContext = waveProvider;
            comboBPM.ItemsSource = Enumerable.Range(1, 40).Select(i => i * 10d).ToList();

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

            int channelIndex = contentGrid.Children.Count;
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





            Controls.TrackChannelView trackChannelView = new Controls.TrackChannelView
            {
                Foreground = this.Foreground,
                Background = this.Background,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            var h = contentScroll.ActualHeight - 20;
            if (h < 0) h = outerGrid.ActualHeight;
            trackChannelView.Height =h;
            trackChannelView.TrackChanel = Track.Channels[channelIndex];

            Grid.SetColumn(trackChannelView, channelIndex);
            Grid.SetRow(trackChannelView, 0);
            contentGrid.Children.Add(trackChannelView);
            trackChannelView.MouseDown += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    SelectChannel(Grid.GetColumn(s as Controls.TrackChannelView));
                    int yIndex = (int)(e.GetPosition(s as Controls.TrackChannelView).Y + slScroll.Value) / 20;

                    SelectNote(yIndex);
                }
            };
        }





        public void NewMusic()
        {
            var newTrack = new ProjectTrack();
            Track = newTrack;
            for (int i = 0; i < 4; i++)
            {
                AddChannel();
            }
        }

        public void TrackChanged()
        {
            slScroll.Value = 0;
            slScroll.Maximum = (Track.NoteCount) * 20 - (contentScroll.ActualHeight - 20);

            sharpsInTrack = new List<int>();
            waveProvider.Track = Track;
            columnHeaderGrid.Children.Clear();
            columnHeaderGrid.ColumnDefinitions.Clear();
            contentGrid.Children.Clear();
            contentGrid.RowDefinitions.Clear();
            contentGrid.ColumnDefinitions.Clear();
            rowHeaderGrid.RowDefinitions.Clear();
            rowHeaderGrid.Children.Clear();


            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            rowHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock textBlock = new TextBlock { Text = "", Margin = new Thickness(0, 0, 5, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Border border = new Border { BorderBrush = this.Foreground, BorderThickness = new Thickness(0.5), Background = this.themeBackground };
            border.Child = textBlock;
            Grid.SetRow(border, 0);
            textBlock.Text = Enumerable.Range(0, Track.NoteCount).Select(x => $"&H{x.ToString("X4")}").Aggregate((s1, s2) => s1 + "\n" + s2);
            textBlock.LineHeight = 20;
            textBlock.VerticalAlignment = VerticalAlignment.Top;
            rowHeaderGrid.Children.Add(border);
            foreach (var c in Track.Channels)
            {
                ChannelAdded();
            }
        }





        public void SetTrackColor(int i, Brush brush)
        {
            if (i >= 0 && i < Track.Channels.Count)
            {
                contentGrid.Children.OfType<Controls.TrackChannelView>().Where(b => Grid.GetColumn(b) == i).ToList().ForEach(b =>
                {
                    b.BorderBrush = brush;
                    b.Foreground = brush;
                    b.Redraw();
                }
                );

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

                var posX = (contentGrid.Children.OfType<Controls.TrackChannelView>().First(b => Grid.GetColumn(b) == i)).TransformToAncestor(contentGrid).Transform(new Point(0, 0)).X;
                if (posX - 10 < contentScroll.HorizontalOffset)
                {
                    contentScroll.ScrollToHorizontalOffset(posX - 10);
                }
                if (posX + 110 > contentScroll.HorizontalOffset + contentScroll.ActualWidth)
                {
                    contentScroll.ScrollToHorizontalOffset(posX - contentScroll.ActualWidth + 110);
                }
            }
        }

        public void SelectNote(int i)
        {
            /*
            if (selectedNote > -1 && selectedNote < rowHeaderGrid.Children.OfType<Border>().Count())
            {
                rowHeaderGrid.Children.OfType<Border>().ElementAt(selectedNote).BorderBrush = Foreground;
                (rowHeaderGrid.Children.OfType<Border>().ElementAt(selectedNote).Child as TextBlock).Foreground = Foreground;
            }
            */
            selectedNote = i;
            /*
            if (selectedNote>-1 && selectedNote< rowHeaderGrid.Children.OfType<Border>().Count())
            {
                rowHeaderGrid.Children.OfType<Border>().ElementAt(selectedNote).BorderBrush = selectedBrush;
                (rowHeaderGrid.Children.OfType<Border>().ElementAt(selectedNote).Child as TextBlock).Foreground = selectedBrush;
            }
            */





            if (selectedNote >= 0 && selectedNote < Track.NoteCount)
            {
                //
                /*
                var posY = (selectedNote * 20) - slScroll.Value;
                if (posY <0)
                {
                    slScroll.Value += posY;
                    rowHeaderScroll.ScrollToVerticalOffset(slScroll.Value);
                }
                if (posY+20>outerGrid.ActualHeight)
                {
                    slScroll.Value += posY+20 - outerGrid.ActualHeight;
                    rowHeaderScroll.ScrollToVerticalOffset(slScroll.Value);

                }
                */
                int nbrPerScreen = (int)(contentScroll.ActualHeight - 20) / 20;
                int pageNum = selectedNote / nbrPerScreen;
                var newValue = pageNum * nbrPerScreen * 20;
                if (newValue != slScroll.Value)
                {
                    slScroll.Value = newValue;
                    rowHeaderScroll.ScrollToVerticalOffset(slScroll.Value);
                }
            }

            cursor.Margin = new Thickness(0, selectedNote * 20 - slScroll.Value, 0, 0);
            cursor.Width = Track.Channels.Count * 100;
        }


        private void contentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            columnHeaderScroll.ScrollToHorizontalOffset(contentScroll.HorizontalOffset);
            foreach (var trackChannelView in contentGrid.Children.OfType<Controls.TrackChannelView>())
            {
                trackChannelView.Margin = new Thickness(0, 0, 0, 0);
            }
        }

        private void btnEditInstrument_Click(object sender, RoutedEventArgs e)
        {
            InstrumentEditorScreen editor = new InstrumentEditorScreen();

            editor.SetWaveFunction(Track.Channels[Grid.GetColumn(sender as Button)].WaveFunction.Clone());
            editor.ShowDialog();
            Track.Channels[Grid.GetColumn(sender as Button)].WaveFunction = editor.WaveFunction.Clone();
        }

        private void headerPressets_Click(object sender, RoutedEventArgs e)
        {
            if (selectedChannel > -1)
            {
                Editor.Instrument instrument = ((e.OriginalSource as MenuItem).DataContext as Editor.Instrument);
                Track.Channels[selectedChannel].WaveFunction = instrument.WaveFunction.Clone();
            }
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

            if (e.Key == Key.Down && selectedNote < Track.NoteCount - 1)
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
                    var newIndex = Math.Min(Track.Channels[selectedChannel].notes.Length - 1, selectedNote + interval);
                    SelectNote(newIndex);
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
            (contentGrid.Children[selectedChannel] as Controls.TrackChannelView).RedrawNote(selectedNote);
        }

        

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            foreach (var item in contentGrid.Children.OfType<Controls.TrackChannelView>())
            {
                item.Height = contentScroll.ActualHeight - 20;
            }
            slScroll.Maximum = (Track.NoteCount) * 20 - (contentScroll.ActualHeight - 20);
            cursor.Margin = new Thickness(0, selectedNote * 20 - slScroll.Value, 0, 0);

            if (selectedChannel >= 0 && selectedChannel < Track.Channels.Count)
            {

                var posX = (contentGrid.Children.OfType<Controls.TrackChannelView>().First(c => Grid.GetColumn(c) == selectedChannel)).TransformToAncestor(contentGrid).Transform(new Point(0, 0)).X;
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
                SelectNote(selectedNote);
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

        private void menuNewMusic_Click(object sender, RoutedEventArgs e)
        {
            NewMusic();
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
                        newTrack.Channels.Add(new TrackChannel { WaveFunction = m, Name = music.InstrumentNames[x], notes = music.Notes[x].ToArray() });
                        x++;
                    }

                    Track = newTrack;
                    waveProvider.SpeedFactor = 1;


                }
            }
        }

        private void menuImportMidi_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "MIDI files (*.mid)|*.mid|All files (*.*)|*.*";
            if (dlg.ShowDialog() == true)
            {
                Engine.MidiFile music = new Engine.MidiFile(dlg.FileName);
                if (music != null)
                {
                    var newTrack = new ProjectTrack();
                    waveProvider.Track = newTrack;
                    newTrack.Channels.Clear();
                    int i = 1;
                    double ratio = 24;
                    var alltracks = music.Tracks.Where(t => t.MidiEvents.Any(me => me.MidiEventType == MidiEventType.NoteOn)).ToList();
                    newTrack.NoteCount = (int)alltracks.Max(t => t.MidiEvents.Max(me => (me.Time * ratio) / music.TicksPerQuarterNote));
                    var division = alltracks.SelectMany(t => t.MidiEvents.Select(me => (double)((me.Time * 96) / (double)music.TicksPerQuarterNote)));
                    foreach (var track in alltracks)
                    {
                        var channel1 = new TrackChannel { WaveFunction = new Engine.SineWaveFunction(), Name = $"track {i}" };
                        var channel2 = new TrackChannel { WaveFunction = new Engine.SineWaveFunction(), Name = $"track {i}" };
                        var channel3 = new TrackChannel { WaveFunction = new Engine.SineWaveFunction(), Name = $"track {i}" };
                        channel1.notes = new int[newTrack.NoteCount + 1];
                        channel2.notes = new int[newTrack.NoteCount + 1];
                        channel3.notes = new int[newTrack.NoteCount + 1];
                        int n = 0;
                        foreach (var tevent in track.TextEvents)
                        {
                            if (tevent.TextEventType == TextEventType.TrackName)
                            {
                                channel1.Name = tevent.Value;
                                channel2.Name = tevent.Value;
                                channel3.Name = tevent.Value;
                            }
                        }
                        int lastNote1 = 0;
                        int lastNote2 = 0;
                        int lastNote3 = 0;
                        for (; n < track.MidiEvents.Count; n++)
                        {
                            var midiEvent = track.MidiEvents[n];
                            var pos = (int)((midiEvent.Time * ratio) / music.TicksPerQuarterNote);
                            if (midiEvent.MidiEventType == MidiEventType.NoteOn)
                            {

                                if (pos < channel1.notes.Length)
                                {
                                    if (channel1.notes[pos] == 0 || channel1.notes[pos] == -1) { channel1.notes[pos] = (midiEvent.Note) - 11; lastNote1 = midiEvent.Note; }
                                    else if (channel3.notes[pos] == 0 || channel2.notes[pos] == -1) { channel2.notes[pos] = (midiEvent.Note) - 11; lastNote2 = midiEvent.Note; }
                                    else if (channel3.notes[pos] == 0 || channel3.notes[pos] == -1) { channel3.notes[pos] = (midiEvent.Note) - 11; lastNote3 = midiEvent.Note; }
                                }
                            }
                            else if (midiEvent.MidiEventType == MidiEventType.NoteOff)
                            {
                                if (pos < channel1.notes.Length)
                                {
                                    if (lastNote1 == midiEvent.Note) { channel1.notes[pos] = -1; lastNote1 = 0; }
                                    else if (lastNote2 == midiEvent.Note) { channel2.notes[pos] = -1; ; lastNote2 = 0; }
                                    else if (lastNote3 == midiEvent.Note) { channel3.notes[pos] = -1; ; lastNote3 = 0; }
                                    channel1.notes[pos] = -1;

                                }
                            }
                        }
                        //channel.notes[n] = -1;
                        if (channel1.notes.Any(nn => nn != 0))
                            newTrack.Channels.Add(channel1);
                        if (channel2.notes.Any(nn => nn != 0))
                            newTrack.Channels.Add(channel2);
                        if (channel3.notes.Any(nn => nn != 0))
                            newTrack.Channels.Add(channel3);
                        i++;
                    }

                    Track = newTrack;
                    waveProvider.SpeedFactor = ratio / 4;


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


        private void slScroll_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            foreach (var item in contentGrid.Children.OfType<Controls.TrackChannelView>())
            {
                item.ScrollOffset = slScroll.Value;
            }
            rowHeaderScroll.ScrollToVerticalOffset(slScroll.Value);
            cursor.Margin = new Thickness(0, selectedNote * 20 - slScroll.Value, 0, 0);
        }

       
    }
}
