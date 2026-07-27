using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MusicTracker.Localization;

namespace MusicTracker.Controls
{
    /// <summary>One step of a guided tour: an element to spotlight (null = a centered card, no spotlight) + text.</summary>
    public sealed class TourStep
    {
        public Func<FrameworkElement> Target; // resolved lazily (menus/buttons may not exist until shown)
        public string Title;
        public string Text;
        public TourStep(Func<FrameworkElement> target, string title, string text) { Target = target; Title = title; Text = text; }
    }

    /// <summary>
    /// A lightweight interactive "coach-mark" tour: a translucent overlay window sized over the app dims everything
    /// except a rounded spotlight cut-out around the current step's target element, with a text card and
    /// Précédent / Suivant / Passer controls. Self-contained (a separate topmost transparent window), so it needs no
    /// changes to the host's visual tree. Steps advance on the card's buttons.
    /// </summary>
    public sealed class GuidedTour
    {
        readonly Window owner;
        readonly IList<TourStep> steps;
        int idx;

        Window overlay;
        Canvas canvas;
        Path dim;
        Border card;
        TextBlock titleTb, bodyTb, counterTb;
        Button prevBtn, nextBtn;

        GuidedTour(Window owner, IList<TourStep> steps) { this.owner = owner; this.steps = steps; }

        /// <summary>Run a tour over <paramref name="owner"/>. No-op if there are no steps or no owner.</summary>
        public static void Run(Window owner, IList<TourStep> steps)
        {
            if (owner == null || steps == null || steps.Count == 0) return;
            new GuidedTour(owner, steps).Start();
        }

        void Start()
        {
            canvas = new Canvas();
            dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(0xB4, 0x0A, 0x0B, 0x0E)) };
            canvas.Children.Add(dim);

            titleTb = new TextBlock { Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            bodyTb = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xE3)), FontSize = 13, TextWrapping = TextWrapping.Wrap };
            counterTb = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

            var skipBtn = new Button { Content = Loc.T("TourSkip"), Padding = new Thickness(10, 3, 10, 3), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAE, 0xB6)), BorderThickness = new Thickness(0) };
            prevBtn = new Button { Content = Loc.T("TourPrev"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand };
            nextBtn = new Button { Content = Loc.T("TourNext"), Padding = new Thickness(16, 4, 16, 4), Cursor = System.Windows.Input.Cursors.Hand };
            try { prevBtn.Style = owner.TryFindResource("cancelButton") as Style; nextBtn.Style = owner.TryFindResource("okButton") as Style; } catch { }

            skipBtn.Click += (s, e) => Close();
            prevBtn.Click += (s, e) => Show(idx - 1);
            nextBtn.Click += (s, e) => { if (idx >= steps.Count - 1) Close(); else Show(idx + 1); };

            var btnRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(skipBtn, Dock.Left);
            btnRow.Children.Add(skipBtn);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(prevBtn); right.Children.Add(nextBtn);
            btnRow.Children.Add(right);

            var stack = new StackPanel();
            stack.Children.Add(counterTb);
            stack.Children.Add(titleTb);
            stack.Children.Add(bodyTb);
            stack.Children.Add(btnRow);

            card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x24, 0x25, 0x2B)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0xB6, 0xC3)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 14, 18, 14),
                Width = 340,
                Child = stack,
            };
            canvas.Children.Add(card);

            overlay = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = canvas,
            };
            PositionOverlayToOwner();
            overlay.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape) Close();
                else if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Right) { if (idx >= steps.Count - 1) Close(); else Show(idx + 1); }
                else if (e.Key == System.Windows.Input.Key.Left) Show(idx - 1);
            };
            // Keep the overlay glued to the app while the tour runs.
            owner.LocationChanged += OwnerMoved;
            owner.SizeChanged += OwnerMoved;

            overlay.Loaded += (s, e) => Show(0);
            overlay.Show();
            overlay.Focus();
        }

        void OwnerMoved(object sender, EventArgs e)
        {
            if (overlay == null) return;
            PositionOverlayToOwner();
            Show(idx);
        }

        void PositionOverlayToOwner()
        {
            // The host uses custom chrome (WindowStyle=None), so its Left/Top/ActualWidth/Height are the client area.
            overlay.Left = owner.Left;
            overlay.Top = owner.Top;
            overlay.Width = Math.Max(1, owner.ActualWidth);
            overlay.Height = Math.Max(1, owner.ActualHeight);
            if (canvas != null) { canvas.Width = overlay.Width; canvas.Height = overlay.Height; }
        }

        void Show(int i)
        {
            if (i < 0 || i >= steps.Count) return;
            idx = i;
            var step = steps[i];

            double W = overlay.Width, H = overlay.Height;
            var full = new RectangleGeometry(new Rect(0, 0, W, H));

            Rect spot = Rect.Empty;
            FrameworkElement target = null;
            try { target = step.Target?.Invoke(); } catch { target = null; }
            if (target != null && target.IsVisible && target.ActualWidth > 0)
            {
                try
                {
                    var root = owner.Content as UIElement ?? owner;
                    var tl = target.TransformToVisual(root).Transform(new Point(0, 0));
                    spot = new Rect(tl.X - 6, tl.Y - 6, target.ActualWidth + 12, target.ActualHeight + 12);
                }
                catch { spot = Rect.Empty; }
            }

            // Dim everything, cutting a rounded hole over the target (EvenOdd fill rule).
            if (!spot.IsEmpty)
            {
                var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
                geo.Children.Add(full);
                geo.Children.Add(new RectangleGeometry(spot, 8, 8));
                dim.Data = geo;
            }
            else dim.Data = full;

            counterTb.Text = (i + 1) + " / " + steps.Count;
            titleTb.Text = step.Title ?? "";
            bodyTb.Text = step.Text ?? "";
            prevBtn.Visibility = i > 0 ? Visibility.Visible : Visibility.Collapsed;
            nextBtn.Content = i >= steps.Count - 1 ? Loc.T("TourDone") : Loc.T("TourNext");

            // Place the card: below the spotlight if there's room, else above; centered when there's no target.
            card.Measure(new Size(card.Width, double.PositiveInfinity));
            double cardH = card.DesiredSize.Height > 0 ? card.DesiredSize.Height : 140;
            double cardW = card.Width;
            double left, top;
            if (spot.IsEmpty)
            {
                left = (W - cardW) / 2; top = (H - cardH) / 2;
            }
            else
            {
                left = Math.Min(Math.Max(12, spot.Left), W - cardW - 12);
                if (spot.Bottom + 12 + cardH <= H - 12) top = spot.Bottom + 12;
                else if (spot.Top - 12 - cardH >= 12) top = spot.Top - 12 - cardH;
                else top = Math.Max(12, (H - cardH) / 2);
            }
            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, top);
        }

        void Close()
        {
            try { owner.LocationChanged -= OwnerMoved; owner.SizeChanged -= OwnerMoved; } catch { }
            try { overlay?.Close(); } catch { }
            overlay = null;
        }
    }
}
