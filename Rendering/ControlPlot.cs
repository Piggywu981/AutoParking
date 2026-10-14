using System;
using System.Numerics;
using ETS2LA.Overlay;
using Hexa.NET.ImGui;

namespace AutoParking;

/// <summary>
///  Time-series view of the terms the follower decides, drawn from <see cref="ControlTrace"/>.
///
///  The status table already shows each term as a number; what a number cannot show is shape - whether
///  the cross-track error is converging or being ridden as an oscillation, whether the steer rate
///  limiter is clipping the pursuit law, whether the integral wound up before a slip. Those read off a
///  curve or not at all.
///
///  Drawn with the plain ImDrawList rather than ImPlot on purpose: the official PIDLib monitor does
///  24 LINQ min/max passes and twelve array allocations every render (see
///  docs/2026-10-14-official-plugin-reverse-engineering-applied.md §3.2), which is tolerable for a
///  window opened deliberately and closed again, and is not tolerable as the thing left open through a
///  whole maneuver. Everything here is allocated once, in the constructor.
/// </summary>
internal sealed class ControlPlot
{
    public const string WindowTitle = "AutoParking Control Terms";

    public const int WindowWidth = 580;
    public const int WindowHeight = 440;

    /// <summary>Visible span. The ring holds about 17 s at 60 Hz, so the window slides inside it.</summary>
    private const double WindowSeconds = 10.0;

    private const int HeaderHeight = 20;
    private const int BandPadding = 6;
    private const int LeftPad = 6;
    private const int ValueColumnWidth = 104;
    private const int ValueLineHeight = 12;

    private static uint Color(float r, float g, float b, float a = 1f)
    {
        uint ri = (uint)(Math.Clamp(r, 0f, 1f) * 255f + 0.5f);
        uint gi = (uint)(Math.Clamp(g, 0f, 1f) * 255f + 0.5f);
        uint bi = (uint)(Math.Clamp(b, 0f, 1f) * 255f + 0.5f);
        uint ai = (uint)(Math.Clamp(a, 0f, 1f) * 255f + 0.5f);
        return ri | (gi << 8) | (bi << 16) | (ai << 24);
    }

    private static readonly uint ColorText = Color(0.86f, 0.86f, 0.82f);
    private static readonly uint ColorAxis = Color(0.45f, 0.45f, 0.44f, 0.8f);
    private static readonly uint ColorGrid = Color(0.34f, 0.34f, 0.33f, 0.55f);
    private static readonly uint ColorReverse = Color(0.98f, 0.62f, 0.20f, 0.13f);
    private static readonly uint ColorReverseLabel = Color(0.98f, 0.62f, 0.20f);

    private static readonly uint ColorCrossTrack = Color(0.95f, 0.55f, 0.55f);
    private static readonly uint ColorHeading = Color(0.60f, 0.85f, 0.98f);
    private static readonly uint ColorSteer = Color(0.45f, 0.92f, 0.52f);
    private static readonly uint ColorSteerRaw = Color(0.45f, 0.92f, 0.52f, 0.40f);
    private static readonly uint ColorTarget = Color(0.85f, 0.85f, 0.45f);
    private static readonly uint ColorActual = Color(0.97f, 0.97f, 0.95f);
    private static readonly uint ColorCommand = Color(0.97f, 0.97f, 0.95f);
    private static readonly uint ColorP = Color(0.55f, 0.80f, 0.98f);
    private static readonly uint ColorI = Color(0.80f, 0.58f, 0.95f);
    private static readonly uint ColorD = Color(0.98f, 0.72f, 0.42f);

    /// <summary>One plotted line. References a staging buffer allocated in the constructor.</summary>
    private readonly struct Line
    {
        public Line(double[] values, uint color, float thickness, string label)
        {
            Values = values;
            Color = color;
            Thickness = thickness;
            Label = label;
        }

        public double[] Values { get; }
        public uint Color { get; }
        public float Thickness { get; }
        public string Label { get; }
    }

    /// <summary>
    ///  One row of the chart. <see cref="Floor"/> is the smallest half-range the row will use, so a
    ///  dead-flat signal cannot magnify single-bit noise into a full-height square wave.
    /// </summary>
    private readonly struct Band
    {
        public Band(string title, double floor, Line[] lines)
        {
            Title = title;
            Floor = floor;
            Lines = lines;
        }

        public string Title { get; }
        public double Floor { get; }
        public Line[] Lines { get; }
    }

    private readonly ControlTrace trace;
    private readonly Band[] bands;

    private WindowDefinition definition;
    private bool registered;

    // Read-side staging, filled under the trace gate and drawn after it is released. Copying rather
    // than drawing under the lock is what stops a 60 Hz write from tearing a polyline mid-frame.
    private readonly double[] xBuf = new double[ControlTrace.Capacity];
    private readonly double[] crossBuf = new double[ControlTrace.Capacity];
    private readonly double[] headingBuf = new double[ControlTrace.Capacity];
    private readonly double[] steerBuf = new double[ControlTrace.Capacity];
    private readonly double[] steerRawBuf = new double[ControlTrace.Capacity];
    private readonly double[] targetBuf = new double[ControlTrace.Capacity];
    private readonly double[] speedBuf = new double[ControlTrace.Capacity];
    private readonly double[] pidPBuf = new double[ControlTrace.Capacity];
    private readonly double[] pidIBuf = new double[ControlTrace.Capacity];
    private readonly double[] pidDBuf = new double[ControlTrace.Capacity];
    private readonly double[] commandBuf = new double[ControlTrace.Capacity];
    private readonly bool[] reverseBuf = new bool[ControlTrace.Capacity];

    private int count;
    private double windowStart;
    private double windowSpan = WindowSeconds;

    public ControlPlot(ControlTrace trace)
    {
        this.trace = trace;

        bands = new[]
        {
            new Band("横向误差 m", 0.05, new[]
            {
                new Line(crossBuf, ColorCrossTrack, 1.7f, "横向"),
            }),
            new Band("航向误差 °", 1.0, new[]
            {
                new Line(headingBuf, ColorHeading, 1.7f, "航向"),
            }),
            // Raw under shaped on purpose: where the two separate is the stretch the rate limiter is
            // fighting, which no single number in the status table reveals.
            new Band("转向指令 -1..1", 0.05, new[]
            {
                new Line(steerRawBuf, ColorSteerRaw, 1.2f, "原始"),
                new Line(steerBuf, ColorSteer, 1.8f, "整形"),
            }),
            new Band("速度 km/h", 1.0, new[]
            {
                new Line(targetBuf, ColorTarget, 1.4f, "目标"),
                new Line(speedBuf, ColorActual, 1.8f, "实际"),
            }),
            // P/I/D are the terms before the launch and approach-to-stop clamps; the command line is
            // what was returned. Command leaving the sum of the terms means a clamp is acting.
            new Band("纵向 m/s²", 0.05, new[]
            {
                new Line(pidPBuf, ColorP, 1.2f, "P"),
                new Line(pidIBuf, ColorI, 1.2f, "I"),
                new Line(pidDBuf, ColorD, 1.2f, "D"),
                new Line(commandBuf, ColorCommand, 1.8f, "指令"),
            }),
        };

        definition = new WindowDefinition
        {
            Title = WindowTitle,
            Width = WindowWidth,
            Height = WindowHeight,
            // Beside the map window rather than on top of it: the two get read together when a
            // maneuver goes wrong.
            X = 600,
            Y = 180,
            Alpha = 0.86f
        };
    }

    /// <summary>
    ///  Registers and opens the window, or unregisters it. Registered only while wanted, unlike the map
    ///  panel: the point of this window is that leaving it off is free, and a closed-but-registered
    ///  window is still a callback the host holds.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (visible)
        {
            if (!registered)
            {
                OverlayHandler.Current.RegisterWindow(definition, Render);
                registered = true;
            }

            OverlayHandler.Current.OpenWindow(WindowTitle);
            return;
        }

        if (!registered)
            return;

        OverlayHandler.Current.UnregisterWindow(definition);
        registered = false;
    }

    /// <summary>
    ///  Acquires nothing but the trace gate. The tick thread reaches that gate while holding the
    ///  plugin's own lock, so taking the plugin lock here too, in the opposite order, would deadlock
    ///  on an unlucky frame boundary.
    /// </summary>
    private void Render()
    {
        StageWindow();

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector2 origin = ImGui.GetWindowPos();
        Vector2 size = ImGui.GetWindowSize();

        float chartLeft = origin.X + LeftPad;
        float chartWidth = Math.Max(60f, size.X - LeftPad - ValueColumnWidth);
        float top = origin.Y + HeaderHeight;
        float stackHeight = Math.Max(60f, size.Y - HeaderHeight - 8f);
        float bandHeight = (stackHeight - (bands.Length - 1) * BandPadding) / bands.Length;

        drawList.AddText(new Vector2(chartLeft, origin.Y + 4), ColorText,
                         $"最近 {windowSpan:0.#} s · {count} 点");
        drawList.AddText(new Vector2(chartLeft + chartWidth + 6f, origin.Y + 4), ColorReverseLabel, "橙底=倒车");

        if (count < 2)
        {
            drawList.AddText(new Vector2(chartLeft, top + 12), ColorText, "尚无采样 - 开始泊车后这里才会出现曲线");
            return;
        }

        DrawReverseSpans(drawList, chartLeft, chartWidth, top, stackHeight);

        for (int i = 0; i < bands.Length; i++)
        {
            DrawBand(drawList, bands[i], new Vector2(chartLeft, top + i * (bandHeight + BandPadding)),
                     chartWidth, bandHeight);
        }
    }

    private void StageWindow()
    {
        count = 0;
        windowSpan = WindowSeconds;

        lock (trace.Gate)
        {
            int available = trace.Count;
            if (available == 0)
                return;

            int oldest = (trace.Head - available + ControlTrace.Capacity) % ControlTrace.Capacity;

            // Anchor the right edge to the newest sample rather than to wall clock. The trace's clock
            // never stops, so a wall-clock window would push the run off the left edge within
            // WindowSeconds of the maneuver ending - which is exactly when the curves are being read.
            ControlSample newest = trace.Samples[(oldest + available - 1) % ControlTrace.Capacity];
            ControlSample oldestSample = trace.Samples[oldest];

            // Show the last WindowSeconds if that much exists, otherwise all of it. The header prints
            // the real span rather than claiming ten seconds over a three-second run.
            double left = Math.Max(oldestSample.Elapsed, newest.Elapsed - WindowSeconds);
            windowStart = left;
            windowSpan = Math.Max(0.001, newest.Elapsed - left);

            for (int i = 0; i < available; i++)
            {
                ControlSample sample = trace.Samples[(oldest + i) % ControlTrace.Capacity];
                if (sample.Elapsed < windowStart)
                    continue;

                xBuf[count] = sample.Elapsed;
                crossBuf[count] = sample.CrossTrackM;
                headingBuf[count] = sample.HeadingErrorDeg;
                steerBuf[count] = sample.Steer;
                steerRawBuf[count] = sample.SteerRaw;
                targetBuf[count] = sample.TargetSpeedMps * 3.6;
                speedBuf[count] = sample.SpeedMps * 3.6;
                pidPBuf[count] = sample.PidP;
                pidIBuf[count] = sample.PidI;
                pidDBuf[count] = sample.PidD;
                commandBuf[count] = sample.CommandMps2;
                reverseBuf[count] = sample.Reversing;
                count++;
            }
        }
    }

    /// <summary>
    ///  One translucent strip per contiguous reverse leg, spanning the whole stack, so a gear boundary
    ///  is visible wherever the eye is rather than only on a dedicated row.
    /// </summary>
    private void DrawReverseSpans(ImDrawListPtr drawList, float chartLeft, float chartWidth, float top, float height)
    {
        int runStart = -1;

        for (int i = 0; i <= count; i++)
        {
            bool reversing = i < count && reverseBuf[i];

            if (reversing && runStart < 0)
                runStart = i;

            if (!reversing && runStart >= 0)
            {
                float x0 = chartLeft + ScaleX(xBuf[runStart], chartWidth);
                float x1 = chartLeft + ScaleX(xBuf[i - 1], chartWidth);
                drawList.AddRectFilled(new Vector2(x0, top), new Vector2(x1 + 1f, top + height), ColorReverse);
                runStart = -1;
            }
        }
    }

    private void DrawBand(ImDrawListPtr drawList, Band band, Vector2 topLeft, float chartWidth, float height)
    {
        float midY = topLeft.Y + height / 2f;
        float inner = Math.Max(2f, height / 2f - 2f);

        double half = band.Floor;
        foreach (Line line in band.Lines)
        {
            for (int i = 0; i < count; i++)
                half = Math.Max(half, Math.Abs(line.Values[i]));
        }

        drawList.AddLine(new Vector2(topLeft.X, midY), new Vector2(topLeft.X + chartWidth, midY), ColorAxis, 1f);
        drawList.AddRect(new Vector2(topLeft.X, topLeft.Y), new Vector2(topLeft.X + chartWidth, topLeft.Y + height),
                         ColorGrid, 0f, 0, 1f);

        // The fitted half-range is printed with the title: knowing a band is showing ±0.06 m and not
        // ±1 m is the difference between "converging" and "drifting" on a curve with no grid numbers.
        drawList.AddText(new Vector2(topLeft.X + 4f, topLeft.Y + 2f), ColorText,
                         $"{band.Title}  ±{Format(half)}");

        double scale = half / inner;

        for (int k = 0; k < band.Lines.Length; k++)
        {
            Line line = band.Lines[k];
            Plot(drawList, line, topLeft.X, chartWidth, midY, scale, height);

            double latest = line.Values[count - 1];
            drawList.AddText(new Vector2(topLeft.X + chartWidth + 6f,
                                         topLeft.Y + 2f + k * ValueLineHeight),
                             line.Color, $"{line.Label} {Format(latest)}");
        }
    }

    private void Plot(ImDrawListPtr drawList, Line line, float chartLeft, float chartWidth, float midY,
                      double scale, float height)
    {
        float previousX = 0f;
        float previousY = 0f;
        bool hasPrevious = false;

        float clampTop = midY - height / 2f;
        float clampBottom = midY + height / 2f;

        for (int i = 0; i < count; i++)
        {
            float x = chartLeft + ScaleX(xBuf[i], chartWidth);
            float y = (float)Math.Clamp(midY - line.Values[i] / scale, clampTop, clampBottom);

            if (hasPrevious)
                drawList.AddLine(new Vector2(previousX, previousY), new Vector2(x, y), line.Color, line.Thickness);

            previousX = x;
            previousY = y;
            hasPrevious = true;
        }
    }

    private float ScaleX(double elapsed, float width)
    {
        double t = (elapsed - windowStart) / windowSpan;
        return (float)Math.Clamp(t, 0.0, 1.0) * width;
    }

    private static string Format(double value) => value.ToString("0.00");
}
