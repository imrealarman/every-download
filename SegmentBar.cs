using EveryDownload.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace EveryDownload;

/// <summary>
/// The file drawn as a strip: filled where bytes are on disk, with a bright mark at the head of every connection.
/// It shows at a glance how the file was cut up, how many connections are working, and where each one is.
/// </summary>
public sealed class SegmentBar : UserControl
{
    private readonly Canvas canvas = new();
    private readonly Border frame = new();

    public SegmentBar()
    {
        Height = 18;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        frame.Background = (Brush)Application.Current.Resources["CanvasBrush"];
        frame.BorderBrush = (Brush)Application.Current.Resources["BorderBrush1"];
        frame.BorderThickness = new Thickness(1);
        frame.CornerRadius = new CornerRadius(4);
        frame.Child = canvas;
        Content = frame;
        SizeChanged += (_, _) => Draw(shown);
    }

    private DownloadItem? shown;

    public void Show(DownloadItem? item)
    {
        shown = item;
        Draw(item);
    }

    private void Draw(DownloadItem? item)
    {
        canvas.Children.Clear();
        double w = ActualWidth - 2, h = ActualHeight - 2;
        if (item == null || w < 4 || h < 2) return;
        var accent = (Brush)Application.Current.Resources["AccentBrush"];

        if (item.IsFinished)
        {
            canvas.Children.Add(new Rectangle { Width = w, Height = h, Fill = accent });
            return;
        }
        long total = item.TotalBytes;
        if (total <= 0) return;

        Segment[] segments;
        lock (item.Gate) segments = item.Segments.Select(s => new Segment { Start = s.Start, End = s.End, Position = s.Position, Busy = s.Busy }).ToArray();
        var head = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        foreach (var s in segments)
        {
            long upTo = s.End == long.MaxValue ? s.Position : Math.Min(s.Position, s.End + 1);
            double x = s.Start * w / total, width = Math.Max(0, (upTo - s.Start) * w / total);
            if (width >= 0.5)
            {
                var r = new Rectangle { Width = width, Height = h, Fill = accent };
                Canvas.SetLeft(r, x);
                canvas.Children.Add(r);
            }
            if (s.Busy && !s.IsDone) // the connection's current position
            {
                var mark = new Rectangle { Width = 2, Height = h, Fill = head };
                Canvas.SetLeft(mark, Math.Min(w - 2, upTo * w / total));
                canvas.Children.Add(mark);
            }
            if (s.End != long.MaxValue && s.End + 1 < total) // a thin divider where one range ends and the next begins
            {
                var line = new Rectangle { Width = 1, Height = h, Fill = (Brush)Application.Current.Resources["BorderBrush2"], Opacity = 0.7 };
                Canvas.SetLeft(line, (s.End + 1) * w / total);
                canvas.Children.Add(line);
            }
        }
    }
}
