using MusicTracker.Graphics;
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
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace MusicTracker.Controls
{
    /// <summary>
    /// Interaction logic for TrackChannelView.xaml
    /// </summary>
    public partial class TrackChannelView : UserControl
    {
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

        public static readonly DependencyProperty TrackChanelProperty =
            DependencyProperty.Register(
                "TrackChanel",
                typeof(TrackChannel),
                typeof(TrackChannelView),
                new PropertyMetadata(null, OnTrackChannelChanged));

        public static readonly DependencyProperty ScrollOffsetProperty =
           DependencyProperty.Register(
               "ScrollOffset",
               typeof(double),
               typeof(TrackChannelView),
               new PropertyMetadata(0d, ScrollOffsetChanged));


        public TrackChannelView()
        {
            InitializeComponent();
            BorderBrush = this.Foreground;

        }

        public TrackChannel TrackChanel
        {
            get
            {
                return (TrackChannel)GetValue(TrackChanelProperty);
            }
            set
            {
                SetValue(TrackChanelProperty, value);
            }
        }

        public double ScrollOffset
        {
            get
            {
                return (double)GetValue(ScrollOffsetProperty);
            }
            set
            {
                SetValue(ScrollOffsetProperty, value);
            }
        }

        private static void OnTrackChannelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var visual = (TrackChannelView)d;
            visual.OnTrackChannelChanged((TrackChannel)e.NewValue);
        }

        private static void ScrollOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var visual = (TrackChannelView)d;
            visual.ScrollOffsetChanged((double)e.NewValue);
        }

        double cellHeight = 20;
        WriteableBitmap wbmp;
        private void ReCreateImage()
        {
            int pixelWidth = 100;
            int pixelHeight = (int)this.ActualHeight;
            if (pixelHeight==0)
            {
                return;
            }
            wbmp = new WriteableBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Bgra32, null);
            img.Source = wbmp;
            img.Width = pixelWidth;
            img.Height = pixelHeight;
            img.HorizontalAlignment = HorizontalAlignment.Left; ;
            img.VerticalAlignment = VerticalAlignment.Top;
            this.HorizontalAlignment = HorizontalAlignment.Stretch;
            this.VerticalAlignment = VerticalAlignment.Stretch;

        }
        private void OnTrackChannelChanged(TrackChannel trackChannel)
        {
            ChangedCommon();
        }

        private void ScrollOffsetChanged(double scrollOffset)
        {
            ChangedCommon();
        }

        private void ChangedCommon()
        {
            if (wbmp == null)
            {
                ReCreateImage();
            }
            if (wbmp == null) return;
            Redraw();
        }

        private string getNoteString(int noteNum)
        {
            string str = "";
            if (noteNum > 1)
            {
                string noteString = NoteStrings[(noteNum - 1) % 12];
                int octave = (noteNum - 1) / 12;
                str = $"{noteString}{octave}";
            }
            else if (noteNum == 0)
            {
                str = "------";
            }
            else
            {
                str = "";
            }
            return str;
        }

        public void Redraw()
        {
            wbmp.Lock();
            wbmp.FillRectangle(0, 0, wbmp.PixelWidth-1, wbmp.PixelHeight-1, (this.Background as SolidColorBrush).Color);
            wbmp.DrawLine(0, 0, 0, wbmp.PixelHeight - 1, (this.BorderBrush as SolidColorBrush).Color);
            wbmp.DrawLine(wbmp.PixelWidth-1, 0, wbmp.PixelWidth-1, wbmp.PixelHeight - 1, (this.BorderBrush as SolidColorBrush).Color);
            var font = Graphics.Font.ML;
           ;
            for (int i = (int)Math.Floor(ScrollOffset / cellHeight); i < TrackChanel.notes.Length; i++)
            {
                int y = (int)(((i * cellHeight) + (cellHeight - font.FontHeight) / 2) - ScrollOffset);
                if (y + 8 > 0 && y < wbmp.PixelHeight)
                {
                    wbmp.DrawText(getNoteString(TrackChanel.notes[i]), 10, y, (this.Foreground as SolidColorBrush).Color, Graphics.Font.ML);
                }
                if (y>=wbmp.PixelHeight)
                {
                    break;
                }
            }
            wbmp.AddDirtyRect(new System.Windows.Int32Rect(0, 0, wbmp.PixelWidth, wbmp.PixelHeight));
            wbmp.Unlock();

           

            

        }

        public void RedrawNote(int noteIndex)
        {
            var font = Graphics.Font.ML;
            ;
            int y = (int)(((noteIndex * cellHeight) + (cellHeight - font.FontHeight) / 2) - ScrollOffset);
            int y1 = (int)(((noteIndex * cellHeight) ) - ScrollOffset) ;
            int y2 =(int)( y1 + cellHeight - 1);
            if (y + 8 > 0 && y < wbmp.PixelHeight)
            {
                wbmp.FillRectangle(0,y1,wbmp.PixelWidth-1,y2, (this.Background as SolidColorBrush).Color);
                wbmp.DrawLine(0,y1, 0, y2, (this.BorderBrush as SolidColorBrush).Color);
                wbmp.DrawLine(wbmp.PixelWidth - 1, y1, wbmp.PixelWidth - 1, y2, (this.BorderBrush as SolidColorBrush).Color);
                wbmp.DrawText(getNoteString(TrackChanel.notes[noteIndex]), 10, y, (this.Foreground as SolidColorBrush).Color, Graphics.Font.ML);
            }
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ReCreateImage();
            if (wbmp!=null)
                Redraw();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            ReCreateImage();
            if (wbmp != null)
                Redraw();
        }
    }
}
