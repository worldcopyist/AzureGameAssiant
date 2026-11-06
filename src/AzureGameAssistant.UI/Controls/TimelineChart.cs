using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.UI.Services;

namespace AzureGameAssistant.UI.Controls;

/// <summary>
/// Custom painted timeline. Each pixel column aggregates the min/max of its time
/// slice, so spikes survive downsampling without a charting dependency.
/// </summary>
public sealed class TimelineChart : FrameworkElement
{
    private const double LeftGutter = 46;
    private const double TopMargin = 22;
    private const double BottomMargin = 16;
    private const double TrackGap = 8;
    private const double MinWindowSeconds = 10;
    private const double MaxWindowSeconds = 1800;

    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Segoe UI, Microsoft YaHei UI"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);

    private static readonly Brush FallbackText = Frozen(Color.FromRgb(0x5D, 0x6B, 0x7A));
    private static readonly Brush FallbackGrid = Frozen(Color.FromRgb(0xE3, 0xE9, 0xF0));
    private static readonly Brush FallbackCpu = Frozen(Color.FromRgb(0x2F, 0x8B, 0xFF));
    private static readonly Brush FallbackGpu = Frozen(Color.FromRgb(0x1F, 0x9D, 0x55));
    private static readonly Brush FallbackMem = Frozen(Color.FromRgb(0xE0, 0x8A, 0x00));
    private static readonly Dictionary<(Brush, double), Pen> PenCache = new();
    private static readonly Dictionary<Brush, Pen> BandPenCache = new();

    private readonly List<Point> _linePoints = new(1200);
    private readonly List<Point> _bandPoints = new(1200);

    private SampleRecord[] _samples = Array.Empty<SampleRecord>();
    private int _count;
    private float[] _minValues = Array.Empty<float>();
    private float[] _maxValues = Array.Empty<float>();

    private double _windowSeconds = 60;
    private double _panSeconds;
    private int _version;

    private Geometry? _cpuLine;
    private Geometry? _cpuBand;
    private Geometry? _gpuLine;
    private Geometry? _gpuBand;
    private Geometry? _memLine;
    private Geometry? _memBand;
    private double _memMax = 1024;
    private RenderKey _lastKey;

    private Rect _plot;
    private Point? _hoverPoint;
    private bool _dragging;
    private Point _dragStart;
    private double _dragStartPan;
    private DateTime _lastHoverPaint = DateTime.MinValue;

    public TimelineChart()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = false;
        Cursor = System.Windows.Input.Cursors.Cross;
    }

    public double WindowSeconds
    {
        get => _windowSeconds;
        set
        {
            double clamped = Math.Clamp(value, MinWindowSeconds, MaxWindowSeconds);
            if (Math.Abs(clamped - _windowSeconds) < 0.01)
            {
                return;
            }

            _windowSeconds = clamped;
            ClampPan();
            InvalidateVisual();
        }
    }

    public bool IsPanned => _panSeconds > 0.5;

    public void SetSamples(SampleRecord[] samples, int count)
    {
        _samples = samples;
        _count = Math.Clamp(count, 0, samples.Length);
        _version++;
        InvalidateVisual();
    }

    public void ResetPan()
    {
        if (_panSeconds == 0)
        {
            return;
        }

        _panSeconds = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);

        if (bounds.Width < 120 || bounds.Height < 140)
        {
            return;
        }

        _plot = new Rect(
            LeftGutter,
            TopMargin,
            Math.Max(1, bounds.Width - LeftGutter - 8),
            Math.Max(1, bounds.Height - TopMargin - BottomMargin));

        DrawLegend(dc, bounds);

        if (_count == 0)
        {
            DrawCentered(dc, LocalizationService.Instance["NoData"], _plot, 12);
            return;
        }

        double endTicks = _samples[_count - 1].UtcTicks - (_panSeconds * TimeSpan.TicksPerSecond);
        double startTicks = endTicks - (_windowSeconds * TimeSpan.TicksPerSecond);

        var key = new RenderKey((int)_plot.Width, (int)_plot.Height, _windowSeconds, _panSeconds, _version);
        if (key != _lastKey)
        {
            Rebuild(key, startTicks, endTicks);
            _lastKey = key;
        }

        DrawTrack(dc, TrackRect(0), _cpuLine, _cpuBand, "100%", "0%", "ChartCpuBrush", FallbackCpu);
        DrawTrack(dc, TrackRect(1), _gpuLine, _gpuBand, "100%", "0%", "ChartGpuBrush", FallbackGpu);

        string memTop = _memMax.ToString("F0") + " MB";
        DrawTrack(dc, TrackRect(2), _memLine, _memBand, memTop, "0", "ChartMemBrush", FallbackMem);

        DrawTimeAxis(dc, startTicks, endTicks);
        DrawHover(dc, startTicks, endTicks);
    }

    private Rect TrackRect(int index)
    {
        double height = (_plot.Height - (TrackGap * 2)) / 3;
        return new Rect(_plot.Left, _plot.Top + (index * (height + TrackGap)), _plot.Width, height);
    }

    private void Rebuild(RenderKey key, double startTicks, double endTicks)
    {
        double span = Math.Max(1, endTicks - startTicks);
        int width = Math.Max(1, key.Width - 1);
        EnsureBuffers(width);

        _cpuLine = BuildSeries(TrackRect(0), startTicks, span, width, static s => s.CpuUtilityPct, 100, out _cpuBand);
        _gpuLine = BuildSeries(TrackRect(1), startTicks, span, width, static s => s.GpuUtilPct, 100, out _gpuBand);

        _memMax = Math.Max(512, Math.Ceiling(WindowMemoryMax(startTicks, endTicks) / 512.0) * 512);
        _memLine = BuildSeries(TrackRect(2), startTicks, span, width, static s => s.MemAvailableMb, _memMax, out _memBand);
    }

    private Geometry BuildSeries(
        Rect plot,
        double startTicks,
        double span,
        int width,
        Func<SampleRecord, double> selector,
        double maxValue,
        out Geometry band)
    {
        if (_minValues.Length < width)
        {
            _minValues = new float[width];
            _maxValues = new float[width];
        }

        Array.Fill(_minValues, float.NaN, 0, width);
        Array.Fill(_maxValues, float.NaN, 0, width);

        for (int i = 0; i < _count; i++)
        {
            SampleRecord sample = _samples[i];
            double offset = (sample.UtcTicks - startTicks) / span;
            if (offset < 0 || offset > 1)
            {
                continue;
            }

            int x = Math.Clamp((int)(offset * width), 0, width - 1);
            float value = (float)Math.Max(0, selector(sample));

            float currentMin = _minValues[x];
            if (float.IsNaN(currentMin) || value < currentMin)
            {
                _minValues[x] = value;
            }

            float currentMax = _maxValues[x];
            if (float.IsNaN(currentMax) || value > currentMax)
            {
                _maxValues[x] = value;
            }
        }

        double left = plot.Left;
        double top = plot.Top;
        double height = plot.Height;
        double scale = height / maxValue;
        double bottom = plot.Bottom;

        _linePoints.Clear();
        _bandPoints.Clear();

        double previousX = double.NaN;
        for (int x = 0; x < width; x++)
        {
            float min = _minValues[x];
            if (float.IsNaN(min))
            {
                continue;
            }

            float max = _maxValues[x];
            double px = left + x;
            double yMax = Math.Clamp(bottom - (max * scale), top, bottom);
            double yMin = Math.Clamp(bottom - (min * scale), top, bottom);

            _linePoints.Add(new Point(px, (yMin + yMax) * 0.5));

            if (previousX < 0 || px - previousX > 1.5)
            {
                if (_bandPoints.Count > 0)
                {
                    _bandPoints.Add(new Point(double.NaN, double.NaN));
                }
            }

            _bandPoints.Add(new Point(px, yMax));
            _bandPoints.Add(new Point(px, yMin));
            previousX = px;
        }

        var line = new StreamGeometry();
        using (StreamGeometryContext context = line.Open())
        {
            bool started = false;
            foreach (Point point in _linePoints)
            {
                if (!started)
                {
                    context.BeginFigure(point, false, false);
                    started = true;
                }
                else
                {
                    context.LineTo(point, true, false);
                }
            }
        }

        line.Freeze();

        var bandGeometry = new StreamGeometry();
        using (StreamGeometryContext context = bandGeometry.Open())
        {
            bool open = false;
            foreach (Point point in _bandPoints)
            {
                if (double.IsNaN(point.X))
                {
                    open = false;
                    continue;
                }

                if (!open)
                {
                    context.BeginFigure(point, false, false);
                    open = true;
                }
                else
                {
                    context.LineTo(point, true, false);
                }
            }
        }

        bandGeometry.Freeze();
        band = bandGeometry;
        return line;
    }

    private double WindowMemoryMax(double startTicks, double endTicks)
    {
        double max = 0;
        for (int i = 0; i < _count; i++)
        {
            SampleRecord sample = _samples[i];
            if (sample.UtcTicks >= startTicks && sample.UtcTicks <= endTicks && sample.MemAvailableMb > max)
            {
                max = sample.MemAvailableMb;
            }
        }

        return max;
    }

    private void DrawTrack(
        DrawingContext dc,
        Rect rect,
        Geometry? line,
        Geometry? band,
        string topLabel,
        string bottomLabel,
        string brushKey,
        Brush seriesFallback)
    {
        Brush gridBrush = GetBrush("ChartGridBrush", FallbackGrid);
        Brush textBrush = GetBrush("TextSecondaryBrush", FallbackText);
        Brush seriesBrush = GetBrush(brushKey, seriesFallback);

        Pen gridPen = GetPen(gridBrush, 1);
        dc.DrawLine(gridPen, new Point(rect.Left, rect.Top), new Point(rect.Right, rect.Top));
        dc.DrawLine(gridPen, new Point(rect.Left, rect.Top + (rect.Height * 0.5)), new Point(rect.Right, rect.Top + (rect.Height * 0.5)));
        dc.DrawLine(gridPen, new Point(rect.Left, rect.Bottom), new Point(rect.Right, rect.Bottom));

        DrawText(dc, topLabel, new Point(4, rect.Top - 6), textBrush, 10);
        DrawText(dc, bottomLabel, new Point(4, rect.Bottom - 12), textBrush, 10);

        if (band is not null)
        {
            dc.DrawGeometry(null, GetBandPen(seriesBrush), band);
        }

        if (line is not null)
        {
            dc.DrawGeometry(null, GetPen(seriesBrush, 1.6), line);
        }
    }

    private void DrawLegend(DrawingContext dc, Rect bounds)
    {
        LocalizationService loc = LocalizationService.Instance;
        double x = LeftGutter;
        double y = 2;
        x = DrawLegendItem(dc, x, y, loc["Cpu"], GetBrush("ChartCpuBrush", FallbackCpu));
        x = DrawLegendItem(dc, x, y, loc["Gpu"], GetBrush("ChartGpuBrush", FallbackGpu));
        DrawLegendItem(dc, x, y, loc["Memory"], GetBrush("ChartMemBrush", FallbackMem));

        if (_panSeconds > 0.5)
        {
            DrawText(
                dc,
                loc["History"],
                new Point(bounds.Width - 52, 2),
                GetBrush("WarnBrush", FallbackText),
                11);
        }
    }

    private double DrawLegendItem(DrawingContext dc, double x, double y, string text, Brush brush)
    {
        dc.DrawRectangle(brush, null, new Rect(x, y + 4, 8, 8));
        double width = DrawText(dc, text, new Point(x + 12, y), GetBrush("TextSecondaryBrush", FallbackText), 11);
        return x + 12 + width + 14;
    }

    private void DrawTimeAxis(DrawingContext dc, double startTicks, double endTicks)
    {
        Brush brush = GetBrush("TextSecondaryBrush", FallbackText);
        double y = _plot.Bottom + 2;
        DrawText(dc, FormatTime(startTicks), new Point(_plot.Left, y), brush, 10);

        FormattedText right = Format(FormatTime(endTicks), brush, 10);
        dc.DrawText(right, new Point(_plot.Right - right.WidthIncludingTrailingWhitespace, y));
    }

    private void DrawHover(DrawingContext dc, double startTicks, double endTicks)
    {
        if (_hoverPoint is not Point hover)
        {
            return;
        }

        if (hover.X < _plot.Left || hover.X > _plot.Right || hover.Y < _plot.Top || hover.Y > _plot.Bottom)
        {
            return;
        }

        double ratio = (hover.X - _plot.Left) / _plot.Width;
        long targetTicks = (long)(startTicks + (ratio * (endTicks - startTicks)));

        int nearest = -1;
        long best = long.MaxValue;
        for (int i = 0; i < _count; i++)
        {
            long distance = Math.Abs(_samples[i].UtcTicks - targetTicks);
            if (distance < best)
            {
                best = distance;
                nearest = i;
            }
        }

        if (nearest < 0)
        {
            return;
        }

        SampleRecord sample = _samples[nearest];
        Brush lineBrush = GetBrush("TextSecondaryBrush", FallbackText);
        dc.DrawLine(GetPen(lineBrush, 1), new Point(hover.X, _plot.Top), new Point(hover.X, _plot.Bottom));

        LocalizationService loc = LocalizationService.Instance;
        string text = string.Create(
            CultureInfo.CurrentUICulture,
            $"{FormatTime(sample.UtcTicks)}\nCPU {sample.CpuUtilityPct:F1}%   GPU {sample.GpuUtilPct:F1}%\n{loc["Memory"]} {sample.MemAvailableMb / 1024.0:F1} GB");

        FormattedText formatted = Format(text, GetBrush("TextPrimaryBrush", FallbackText), 11);
        double width = formatted.Width + 14;
        double height = formatted.Height + 10;
        double left = hover.X + 12;
        if (left + width > _plot.Right)
        {
            left = hover.X - width - 12;
        }

        double top = Math.Max(_plot.Top, hover.Y - height - 8);
        dc.DrawRoundedRectangle(
            GetBrush("SurfaceBrush", Brushes.White),
            GetPen(GetBrush("BorderBrush", FallbackGrid), 1),
            new Rect(left, top, width, height),
            4,
            4);

        dc.DrawText(formatted, new Point(left + 7, top + 5));
    }

    private void OnHoverChanged(Point point)
    {
        _hoverPoint = point;
        DateTime now = DateTime.UtcNow;
        if (now - _lastHoverPaint < TimeSpan.FromMilliseconds(100))
        {
            return;
        }

        _lastHoverPaint = now;
        InvalidateVisual();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging)
        {
            Point position = e.GetPosition(this);
            double deltaX = _dragStart.X - position.X;
            double seconds = (deltaX / Math.Max(1, _plot.Width)) * _windowSeconds;
            _panSeconds = Math.Max(0, _dragStartPan + seconds);
            ClampPan();
            InvalidateVisual();
            return;
        }

        OnHoverChanged(e.GetPosition(this));
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverPoint = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (e.ClickCount == 2)
        {
            ResetPan();
            e.Handled = true;
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragStartPan = _panSeconds;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(System.Windows.Input.MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        double factor = e.Delta > 0 ? 1.25 : 0.8;
        double maxSpan = Math.Max(MinWindowSeconds, _count > 1
            ? (_samples[_count - 1].UtcTicks - _samples[0].UtcTicks) / (double)TimeSpan.TicksPerSecond
            : MaxWindowSeconds);

        double maxWindow = Math.Max(
            MinWindowSeconds,
            Math.Min(MaxWindowSeconds, maxSpan));

        WindowSeconds = Math.Clamp(_windowSeconds * factor, MinWindowSeconds, maxWindow);
        e.Handled = true;
    }

    private void ClampPan()
    {
        double span = _count > 1
            ? (_samples[_count - 1].UtcTicks - _samples[0].UtcTicks) / (double)TimeSpan.TicksPerSecond
            : 0;

        double maxPan = Math.Max(0, span - _windowSeconds);
        _panSeconds = Math.Clamp(_panSeconds, 0, maxPan);
    }

    private void EnsureBuffers(int width)
    {
        if (_minValues.Length < width)
        {
            _minValues = new float[width];
            _maxValues = new float[width];
        }
    }

    private void DrawCentered(DrawingContext dc, string text, Rect area, double size)
    {
        FormattedText formatted = Format(text, GetBrush("TextSecondaryBrush", FallbackText), size);
        dc.DrawText(
            formatted,
            new Point(
                area.Left + ((area.Width - formatted.Width) * 0.5),
                area.Top + ((area.Height - formatted.Height) * 0.5)));
    }

    private double DrawText(DrawingContext dc, string text, Point origin, Brush brush, double size)
    {
        FormattedText formatted = Format(text, brush, size);
        dc.DrawText(formatted, origin);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private FormattedText Format(string text, Brush brush, double size) => new(
        text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        LabelTypeface,
        size,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string FormatTime(double ticks)
    {
        long value = (long)ticks;
        if (value <= 0)
        {
            return "--:--:--";
        }

        return new DateTime(value, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentUICulture);
    }

    private Brush GetBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static Pen GetPen(Brush brush, double thickness)
    {
        if (!PenCache.TryGetValue((brush, thickness), out Pen? pen))
        {
            pen = new Pen(brush, thickness);
            PenCache[(brush, thickness)] = pen;
        }

        return pen;
    }

    private static Pen GetBandPen(Brush brush)
    {
        if (!BandPenCache.TryGetValue(brush, out Pen? pen))
        {
            Brush faded = brush;
            if (brush is SolidColorBrush solid)
            {
                var copy = new SolidColorBrush(solid.Color) { Opacity = 0.45 };
                copy.Freeze();
                faded = copy;
            }

            pen = new Pen(faded, 1);
            BandPenCache[brush] = pen;
        }

        return pen;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private readonly record struct RenderKey(int Width, int Height, double WindowSeconds, double PanSeconds, int Version);
}
