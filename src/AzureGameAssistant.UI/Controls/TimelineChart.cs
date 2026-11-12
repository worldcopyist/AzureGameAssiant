using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.UI.Services;

namespace AzureGameAssistant.UI.Controls;

public enum ChartMetric
{
    Combined,
    Cpu,
    Gpu,
    Memory,
    Vram,
    Disk,
    Frame,
}

[Flags]
public enum ChartSeries
{
    None = 0,
    Cpu = 1,
    Gpu = 2,
    Memory = 4,
    Vram = 8,
    Disk = 16,
    Frame = 32,
}

/// <summary>
/// Custom painted timeline. The combined mode overlays the enabled metrics on a
/// single 0-100% axis (VRAM is shown as a share of total) and keeps frame time on
/// a secondary right axis. A single metric can be isolated for the split cards.
/// </summary>
public sealed class TimelineChart : FrameworkElement
{
    private const double LeftGutter = 46;
    private const double FrameGutter = 44;
    private const double TopMargin = 22;
    private const double BottomMargin = 16;
    private const double TrackGap = 8;
    private const double MinWindowSeconds = 10;
    private const double MaxWindowSeconds = 1800;
    private const int SpikeMarkLimit = 512;

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
    private static readonly Brush FallbackVram = Frozen(Color.FromRgb(0x7C, 0x5C, 0xE0));
    private static readonly Brush FallbackDisk = Frozen(Color.FromRgb(0x0F, 0x9C, 0x8A));
    private static readonly Brush FallbackFrame = Frozen(Color.FromRgb(0xB0, 0x30, 0x8F));
    private static readonly Brush FallbackSpike = Frozen(Color.FromRgb(0xD9, 0x3A, 0x3A));
    private static readonly Dictionary<(Brush, double), Pen> PenCache = new();
    private static readonly Dictionary<Brush, Pen> BandPenCache = new();
    private static readonly Dictionary<Brush, Pen> DashPenCache = new();
    private static readonly Dictionary<Brush, Brush> BandFillCache = new();

    private readonly List<Point> _linePoints = new(1200);
    private readonly List<Point> _bandPoints = new(1200);
    private readonly List<Point> _spikePoints = new(64);
    private readonly Geometry?[] _lines = new Geometry?[5];
    private readonly Geometry?[] _bands = new Geometry?[5];
    private readonly double[] _seriesMax = new double[5];

    private SampleRecord[] _samples = Array.Empty<SampleRecord>();
    private int _count;
    private FrameTimePoint[] _frames = Array.Empty<FrameTimePoint>();
    private int _frameCount;
    private int _frameVersion;
    private double _frameScaleMax = 33.3;
    private Geometry? _frameLine;
    private Geometry? _frameBand;
    private float[] _minValues = Array.Empty<float>();
    private float[] _maxValues = Array.Empty<float>();
    private float[] _spikeValues = Array.Empty<float>();

    private ChartMetric _metric = ChartMetric.Combined;
    private ChartSeries _series = ChartSeries.Cpu | ChartSeries.Gpu | ChartSeries.Memory |
                                  ChartSeries.Vram | ChartSeries.Disk | ChartSeries.Frame;
    private double _windowSeconds = 60;
    private double _panSeconds;
    private long _eventStartUtc;
    private long _eventEndUtc;
    private int _version;

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

    public ChartMetric Metric
    {
        get => _metric;
        set
        {
            if (_metric == value)
            {
                return;
            }

            _metric = value;
            InvalidateVisual();
        }
    }

    public ChartSeries Series
    {
        get => _series;
        set
        {
            if (_series == value)
            {
                return;
            }

            _series = value;
            InvalidateVisual();
        }
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

    public void SetFrames(FrameTimePoint[] frames, int count)
    {
        _frames = frames;
        _frameCount = Math.Clamp(count, 0, frames.Length);
        _frameVersion++;
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

    /// <summary>
    /// Marks the confirmed stutter window so the replay and the exported PNG show
    /// which part of the timeline the attribution refers to.
    /// </summary>
    public void SetEventWindow(long startUtc, long endUtc)
    {
        if (_eventStartUtc == startUtc && _eventEndUtc == endUtc)
        {
            return;
        }

        _eventStartUtc = startUtc;
        _eventEndUtc = endUtc;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);

        bool combined = _metric == ChartMetric.Combined;
        if (bounds.Width < 120 || bounds.Height < (combined ? 140 : 70))
        {
            return;
        }

        bool combinedFrame = combined && HasSeries(ChartSeries.Frame);
        _plot = new Rect(
            LeftGutter,
            TopMargin,
            Math.Max(1, bounds.Width - LeftGutter - (combinedFrame ? FrameGutter : 8)),
            Math.Max(1, bounds.Height - TopMargin - BottomMargin));

        DrawLegend(dc, bounds);

        bool frameChart = _metric == ChartMetric.Frame;
        if (_count == 0 && (!frameChart || _frameCount == 0))
        {
            DrawCentered(dc, LocalizationService.Instance["NoData"], _plot, 12);
            return;
        }

        double baseEndTicks = _count > 0 ? _samples[_count - 1].UtcTicks : _frames[_frameCount - 1].UtcTicks;
        if ((frameChart || combinedFrame) && _frameCount > 0)
        {
            baseEndTicks = Math.Max(baseEndTicks, _frames[_frameCount - 1].UtcTicks);
        }

        double endTicks = baseEndTicks - (_panSeconds * TimeSpan.TicksPerSecond);
        double startTicks = endTicks - (_windowSeconds * TimeSpan.TicksPerSecond);

        var key = new RenderKey(
            (int)_plot.Width,
            (int)_plot.Height,
            _windowSeconds,
            _panSeconds,
            _version,
            _frameVersion,
            _metric,
            _series);
        if (key != _lastKey)
        {
            Rebuild(key, startTicks, endTicks);
            _lastKey = key;
        }

        if (combined)
        {
            DrawCombined(dc);
        }
        else if (frameChart)
        {
            DrawFrameTrack(dc, TrackRect(0, 1));
        }
        else
        {
            DrawMetric(dc, TrackRect(0, 1), MetricIndex(_metric));
        }

        DrawEventWindow(dc, startTicks, endTicks);
        DrawTimeAxis(dc, startTicks, endTicks);
        DrawHover(dc, startTicks, endTicks);
    }

    private Rect TrackRect(int index, int count)
    {
        if (count <= 1)
        {
            return _plot;
        }

        double height = (_plot.Height - (TrackGap * (count - 1))) / count;
        return new Rect(_plot.Left, _plot.Top + (index * (height + TrackGap)), _plot.Width, height);
    }

    private void Rebuild(RenderKey key, double startTicks, double endTicks)
    {
        double span = Math.Max(1, endTicks - startTicks);
        int width = Math.Max(1, key.Width - 1);
        EnsureBuffers(width);

        if (_metric == ChartMetric.Combined)
        {
            for (int index = 0; index < 5; index++)
            {
                if (HasSeries(SeriesForIndex(index)))
                {
                    BuildMetric(index, _plot, startTicks, endTicks, span, width, normalizeVram: true);
                }
            }

            if (HasSeries(ChartSeries.Frame))
            {
                BuildFrameTrack(_plot, startTicks, endTicks, span, width);
            }
        }
        else if (_metric == ChartMetric.Frame)
        {
            BuildFrameTrack(TrackRect(0, 1), startTicks, endTicks, span, width);
        }
        else
        {
            BuildMetric(MetricIndex(_metric), TrackRect(0, 1), startTicks, endTicks, span, width);
        }
    }

    private void BuildMetric(
        int index,
        Rect rect,
        double startTicks,
        double endTicks,
        double span,
        int width,
        bool normalizeVram = false)
    {
        switch (index)
        {
            case 0:
                _seriesMax[0] = 100;
                _lines[0] = BuildSeries(rect, startTicks, span, width, static s => s.CpuUtilityPct, 100, out Geometry cpuBand);
                _bands[0] = cpuBand;
                break;

            case 1:
                _seriesMax[1] = 100;
                _lines[1] = BuildSeries(rect, startTicks, span, width, static s => s.GpuUtilPct, 100, out Geometry gpuBand);
                _bands[1] = gpuBand;
                break;

            case 2:
                _seriesMax[2] = 100;
                _lines[2] = BuildSeries(
                    rect,
                    startTicks,
                    span,
                    width,
                    static s => TelemetryMath.MemoryUsagePct(s),
                    100,
                    out Geometry memBand);
                _bands[2] = memBand;
                break;

            case 3:
                if (normalizeVram)
                {
                    _seriesMax[3] = 100;
                    _lines[3] = BuildSeries(
                        rect,
                        startTicks,
                        span,
                        width,
                        static s => VramUsagePct(s),
                        100,
                        out Geometry vramPctBand);
                    _bands[3] = vramPctBand;
                    break;
                }

                double vramMax = Math.Max(
                    256,
                    Math.Ceiling(WindowMaximum(startTicks, endTicks, static s => s.GpuDedicatedMb) / 256.0) * 256);
                _seriesMax[3] = vramMax;
                _lines[3] = BuildSeries(rect, startTicks, span, width, static s => s.GpuDedicatedMb, vramMax, out Geometry vramBand);
                _bands[3] = vramBand;
                break;

            default:
                _seriesMax[4] = 100;
                _lines[4] = BuildSeries(rect, startTicks, span, width, static s => s.DiskBusyPct, 100, out Geometry diskBand);
                _bands[4] = diskBand;
                break;
        }
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

        return CreateSeriesGeometry(plot, width, maxValue, out band);
    }

    private Geometry CreateSeriesGeometry(Rect plot, int width, double maxValue, out Geometry band)
    {
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

    private void BuildFrameTrack(Rect rect, double startTicks, double endTicks, double span, int width)
    {
        if (_minValues.Length < width || _spikeValues.Length < width)
        {
            _minValues = new float[width];
            _maxValues = new float[width];
            _spikeValues = new float[width];
        }

        Array.Fill(_minValues, float.NaN, 0, width);
        Array.Fill(_maxValues, float.NaN, 0, width);
        Array.Fill(_spikeValues, float.NaN, 0, width);

        double windowMax = 0;
        for (int i = 0; i < _frameCount; i++)
        {
            FrameTimePoint frame = _frames[i];
            double offset = (frame.UtcTicks - startTicks) / span;
            if (offset < 0 || offset > 1)
            {
                continue;
            }

            float value = frame.FrameTimeMs;
            if (!(value > 0))
            {
                continue;
            }

            int x = Math.Clamp((int)(offset * width), 0, width - 1);
            if (float.IsNaN(_minValues[x]) || value < _minValues[x])
            {
                _minValues[x] = value;
            }

            if (float.IsNaN(_maxValues[x]) || value > _maxValues[x])
            {
                _maxValues[x] = value;
            }

            if (frame.Spike && (float.IsNaN(_spikeValues[x]) || value > _spikeValues[x]))
            {
                _spikeValues[x] = value;
            }

            windowMax = Math.Max(windowMax, value);
        }

        _frameScaleMax = FrameScaleMax(windowMax);
        _frameLine = CreateSeriesGeometry(rect, width, _frameScaleMax, out Geometry band);
        _frameBand = band;

        _spikePoints.Clear();
        double scale = rect.Height / _frameScaleMax;
        for (int x = 0; x < width && _spikePoints.Count < SpikeMarkLimit; x++)
        {
            float spike = _spikeValues[x];
            if (float.IsNaN(spike))
            {
                continue;
            }

            double y = Math.Clamp(rect.Bottom - (spike * scale), rect.Top, rect.Bottom);
            _spikePoints.Add(new Point(rect.Left + x, y));
        }
    }

    private static double FrameScaleMax(double max) => max switch
    {
        <= 16.7 => 16.7,
        <= 33.3 => 33.3,
        <= 50 => 50,
        <= 66.7 => 66.7,
        <= 100 => 100,
        _ => Math.Ceiling(max / 50.0) * 50.0,
    };

    private double WindowMaximum(double startTicks, double endTicks, Func<SampleRecord, double> selector)
    {
        double max = 0;
        for (int i = 0; i < _count; i++)
        {
            SampleRecord sample = _samples[i];
            if (sample.UtcTicks >= startTicks && sample.UtcTicks <= endTicks)
            {
                max = Math.Max(max, selector(sample));
            }
        }

        return max;
    }

    private static double VramUsagePct(in SampleRecord sample) =>
        TelemetryMath.TryGetVramTotalMb(sample, out double total) && total > 0
            ? Math.Clamp(sample.GpuDedicatedMb / total * 100.0, 0, 100)
            : 0;

    private void DrawCombined(DrawingContext dc)
    {
        DrawGrid(dc, _plot, "100%", "0%");

        for (int index = 0; index < 5; index++)
        {
            if (!HasSeries(SeriesForIndex(index)) || _lines[index] is not Geometry line)
            {
                continue;
            }

            dc.DrawGeometry(null, GetPen(GetBrush(BrushKey(index), Fallback(index)), 1.6), line);
        }

        if (!HasSeries(ChartSeries.Frame))
        {
            return;
        }

        if (_frameLine is not null)
        {
            dc.DrawGeometry(null, GetPen(GetBrush("ChartFrameBrush", FallbackFrame), 1.4), _frameLine);
        }

        Brush textBrush = GetBrush("TextSecondaryBrush", FallbackText);
        DrawText(dc, _frameScaleMax.ToString("F0", CultureInfo.CurrentUICulture) + " ms",
            new Point(_plot.Right + 4, _plot.Top - 6), textBrush, 10);
        DrawText(dc, "0", new Point(_plot.Right + 4, _plot.Bottom - 12), textBrush, 10);
    }

    private void DrawGrid(DrawingContext dc, Rect rect, string topLabel, string bottomLabel)
    {
        Brush gridBrush = GetBrush("ChartGridBrush", FallbackGrid);
        Brush textBrush = GetBrush("TextSecondaryBrush", FallbackText);

        Pen gridPen = GetPen(gridBrush, 1);
        dc.DrawLine(gridPen, new Point(rect.Left, rect.Top), new Point(rect.Right, rect.Top));
        dc.DrawLine(gridPen, new Point(rect.Left, rect.Top + (rect.Height * 0.5)), new Point(rect.Right, rect.Top + (rect.Height * 0.5)));
        dc.DrawLine(gridPen, new Point(rect.Left, rect.Bottom), new Point(rect.Right, rect.Bottom));

        DrawText(dc, topLabel, new Point(4, rect.Top - 6), textBrush, 10);
        DrawText(dc, bottomLabel, new Point(4, rect.Bottom - 12), textBrush, 10);
    }

    private void DrawMetric(DrawingContext dc, Rect rect, int index)
    {
        string top = index == 3
            ? _seriesMax[3].ToString("F0", CultureInfo.CurrentUICulture) + " MB"
            : "100%";
        string bottom = index == 3 ? "0" : "0%";

        DrawTrack(dc, rect, _lines[index], _bands[index], top, bottom, BrushKey(index), Fallback(index));
    }

    private void DrawFrameTrack(DrawingContext dc, Rect rect)
    {
        Brush gridBrush = GetBrush("ChartGridBrush", FallbackGrid);
        Brush textBrush = GetBrush("TextSecondaryBrush", FallbackText);
        Brush seriesBrush = GetBrush("ChartFrameBrush", FallbackFrame);

        Pen gridPen = GetPen(gridBrush, 1);
        dc.DrawLine(gridPen, new Point(rect.Left, rect.Top), new Point(rect.Right, rect.Top));
        dc.DrawLine(gridPen, new Point(rect.Left, rect.Top + (rect.Height * 0.5)), new Point(rect.Right, rect.Top + (rect.Height * 0.5)));
        dc.DrawLine(gridPen, new Point(rect.Left, rect.Bottom), new Point(rect.Right, rect.Bottom));

        DrawText(dc, _frameScaleMax.ToString("F0", CultureInfo.CurrentUICulture) + " ms", new Point(4, rect.Top - 6), textBrush, 10);
        DrawText(dc, "0", new Point(4, rect.Bottom - 12), textBrush, 10);

        if (_frameBand is not null)
        {
            dc.DrawGeometry(null, GetBandPen(seriesBrush), _frameBand);
        }

        if (_frameLine is not null)
        {
            dc.DrawGeometry(null, GetPen(seriesBrush, 1.4), _frameLine);
        }

        Brush spikeBrush = GetBrush("DangerBrush", FallbackSpike);
        foreach (Point point in _spikePoints)
        {
            dc.DrawEllipse(spikeBrush, null, point, 2, 2);
        }
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

        if (_metric == ChartMetric.Combined)
        {
            for (int index = 0; index < 5; index++)
            {
                if (HasSeries(SeriesForIndex(index)))
                {
                    x = DrawLegendItem(dc, x, y, loc[LegendKey(index)], GetBrush(BrushKey(index), Fallback(index)));
                }
            }

            if (HasSeries(ChartSeries.Frame))
            {
                DrawLegendItem(dc, x, y, loc["FrameTimeAxis"], GetBrush("ChartFrameBrush", FallbackFrame));
            }
        }
        else if (_metric == ChartMetric.Frame)
        {
            x = DrawLegendItem(dc, x, y, loc["FrameTimeAxis"], GetBrush("ChartFrameBrush", FallbackFrame));
            DrawDotLegendItem(dc, x, y, loc["FrameSpike"], GetBrush("DangerBrush", FallbackSpike));
        }
        else
        {
            int index = MetricIndex(_metric);
            DrawLegendItem(dc, x, y, loc[LegendKey(index)], GetBrush(BrushKey(index), Fallback(index)));
        }

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

    private double DrawDotLegendItem(DrawingContext dc, double x, double y, string text, Brush brush)
    {
        dc.DrawEllipse(brush, null, new Point(x + 4, y + 8), 3, 3);
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

    private void DrawEventWindow(DrawingContext dc, double startTicks, double endTicks)
    {
        if (_eventStartUtc <= 0)
        {
            return;
        }

        double span = Math.Max(1, endTicks - startTicks);
        double rawLeft = _plot.Left + ((_eventStartUtc - startTicks) / span) * _plot.Width;
        double rawRight = _eventEndUtc > _eventStartUtc
            ? _plot.Left + ((_eventEndUtc - startTicks) / span) * _plot.Width
            : rawLeft;
        if (rawRight < _plot.Left || rawLeft > _plot.Right)
        {
            return;
        }

        double left = Math.Clamp(rawLeft, _plot.Left, _plot.Right);
        double right = Math.Clamp(rawRight, _plot.Left, _plot.Right);
        Brush marker = GetBrush("ChartFrameBrush", FallbackFrame);

        if (right - left > 1)
        {
            dc.DrawRectangle(GetBandFill(marker), null, new Rect(left, _plot.Top, right - left, _plot.Height));
        }

        Pen dashed = GetDashPen(marker);
        dc.DrawLine(dashed, new Point(left, _plot.Top), new Point(left, _plot.Bottom));
        if (right - left > 2)
        {
            dc.DrawLine(dashed, new Point(right, _plot.Top), new Point(right, _plot.Bottom));
        }

        DrawText(
            dc,
            LocalizationService.Instance["SessionEventMarker"],
            new Point(left + 4, _plot.Top + 1),
            GetBrush("TextSecondaryBrush", FallbackText),
            10);
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

        if (_metric == ChartMetric.Frame)
        {
            DrawFrameHover(dc, hover, targetTicks);
            return;
        }

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
        string text = FormatTime(sample.UtcTicks);
        if (_metric == ChartMetric.Combined)
        {
            for (int index = 0; index < 5; index++)
            {
                if (HasSeries(SeriesForIndex(index)))
                {
                    text += "\n" + DescribeSample(sample, index);
                }
            }

            if (HasSeries(ChartSeries.Frame) && TryFindNearestFrame(targetTicks, out FrameTimePoint frame))
            {
                text += "\n" + DescribeFrame(frame);
            }
        }
        else
        {
            text += "\n" + DescribeSample(sample, MetricIndex(_metric));
        }

        DrawHoverTip(dc, hover, text);
    }

    private void DrawFrameHover(DrawingContext dc, Point hover, long targetTicks)
    {
        if (!TryFindNearestFrame(targetTicks, out FrameTimePoint frame))
        {
            return;
        }

        DrawHoverTip(dc, hover, FormatTime(frame.UtcTicks) + "\n" + DescribeFrame(frame));
    }

    private bool TryFindNearestFrame(long targetTicks, out FrameTimePoint frame)
    {
        frame = default;
        int nearest = -1;
        long best = long.MaxValue;
        for (int i = 0; i < _frameCount; i++)
        {
            long distance = Math.Abs(_frames[i].UtcTicks - targetTicks);
            if (distance < best)
            {
                best = distance;
                nearest = i;
            }
        }

        if (nearest < 0)
        {
            return false;
        }

        frame = _frames[nearest];
        return true;
    }

    private static string DescribeFrame(in FrameTimePoint frame)
    {
        LocalizationService loc = LocalizationService.Instance;
        double fps = frame.FrameTimeMs > 0 ? 1000.0 / frame.FrameTimeMs : 0;
        string detail = loc.Format("FrameTimeHover", frame.FrameTimeMs, fps);
        if (frame.Spike)
        {
            detail += " · " + loc["FrameSpike"];
        }

        return detail;
    }

    private void DrawHoverTip(DrawingContext dc, Point hover, string text)
    {
        Brush lineBrush = GetBrush("TextSecondaryBrush", FallbackText);
        dc.DrawLine(GetPen(lineBrush, 1), new Point(hover.X, _plot.Top), new Point(hover.X, _plot.Bottom));

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

    private string DescribeSample(in SampleRecord sample, int index)
    {
        LocalizationService loc = LocalizationService.Instance;

        switch (index)
        {
            case 0:
                return $"CPU {sample.CpuUtilityPct:F1}%";

            case 1:
                return $"GPU {sample.GpuUtilPct:F1}%";

            case 2:
                if (SystemMemory.TryGetUsage(sample, out double usedMb, out double totalMb))
                {
                    return $"{loc["Memory"]} {TelemetryMath.MemoryUsagePct(sample):F1}%  " +
                           $"{FormatMb(usedMb)} / {FormatMb(totalMb)}";
                }

                return $"{loc["Memory"]} {TelemetryMath.MemoryUsagePct(sample):F1}%  " +
                       $"{FormatMb(sample.MemCommitMb)} / {FormatMb(sample.MemCommitLimitMb)}";

            case 3:
                string vram = $"{loc["Vram"]} {FormatMb(sample.GpuDedicatedMb)}";
                if (TelemetryMath.TryGetVramTotalMb(sample, out double total))
                {
                    vram += $" / {FormatMb(total)} ({VramUsagePct(sample):F1}%)";
                }

                return vram;

            default:
                return $"{loc["Disk"]} {sample.DiskBusyPct:F1}%";
        }
    }

    private static string FormatMb(double megabytes) => megabytes >= 1024
        ? (megabytes / 1024.0).ToString("F1", CultureInfo.CurrentUICulture) + " GB"
        : megabytes.ToString("F0", CultureInfo.CurrentUICulture) + " MB";

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

    private bool HasSeries(ChartSeries series) => (_series & series) != 0;

    private static ChartSeries SeriesForIndex(int index) => index switch
    {
        0 => ChartSeries.Cpu,
        1 => ChartSeries.Gpu,
        2 => ChartSeries.Memory,
        3 => ChartSeries.Vram,
        _ => ChartSeries.Disk,
    };

    private static int MetricIndex(ChartMetric metric) => metric switch
    {
        ChartMetric.Cpu => 0,
        ChartMetric.Gpu => 1,
        ChartMetric.Memory => 2,
        ChartMetric.Vram => 3,
        ChartMetric.Disk => 4,
        _ => 0,
    };

    private static string LegendKey(int index) => index switch
    {
        0 => "Cpu",
        1 => "Gpu",
        2 => "Memory",
        3 => "Vram",
        _ => "Disk",
    };

    private static string BrushKey(int index) => index switch
    {
        0 => "ChartCpuBrush",
        1 => "ChartGpuBrush",
        2 => "ChartMemBrush",
        3 => "ChartVramBrush",
        _ => "ChartDiskBrush",
    };

    private static Brush Fallback(int index) => index switch
    {
        0 => FallbackCpu,
        1 => FallbackGpu,
        2 => FallbackMem,
        3 => FallbackVram,
        _ => FallbackDisk,
    };

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

    private static Pen GetDashPen(Brush brush)
    {
        if (!DashPenCache.TryGetValue(brush, out Pen? pen))
        {
            pen = new Pen(brush, 1) { DashStyle = DashStyles.Dash };
            pen.Freeze();
            DashPenCache[brush] = pen;
        }

        return pen;
    }

    private static Brush GetBandFill(Brush brush)
    {
        if (!BandFillCache.TryGetValue(brush, out Brush? fill))
        {
            fill = brush;
            if (brush is SolidColorBrush solid)
            {
                var copy = new SolidColorBrush(solid.Color) { Opacity = 0.10 };
                copy.Freeze();
                fill = copy;
            }

            BandFillCache[brush] = fill;
        }

        return fill;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private readonly record struct RenderKey(
        int Width,
        int Height,
        double WindowSeconds,
        double PanSeconds,
        int Version,
        int FrameVersion,
        ChartMetric Metric,
        ChartSeries Series);
}
