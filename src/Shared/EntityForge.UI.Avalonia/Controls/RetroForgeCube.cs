using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace EntityForge.UI.Avalonia.Controls;

/// <summary>A small, procedural demoscene homage to the open-frame EntityForge mark.</summary>
public sealed class RetroForgeCube : Control
{
    private const double SceneWidth = 320, SceneHeight = 190;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1d / 30) };
    private readonly Stopwatch _clock = new();
    private double _elapsed;
    private bool _playing = true;
    private static readonly (int A, int B)[] Edges = BuildEdges();
    private static readonly IBrush[] Sky = Enumerable.Range(0, 48).Select(i =>
        (IBrush)new SolidColorBrush(Color.FromRgb((byte)(9 + i / 3), (byte)(11 + i / 5), (byte)(28 + i / 2)))).ToArray();

    public RetroForgeCube()
    {
        ClipToBounds = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        _timer.Tick += (_, _) => InvalidateVisual();
        if (OperatingSystem.IsWindows() && SystemParametersInfo(0x1042, 0, out bool animations, 0))
        {
            _playing = animations;
        }
    }

    public bool IsPlaying
    {
        get => _playing;
        set
        {
            if (_playing == value)
            {
                return;
            }

            _elapsed += _clock.Elapsed.TotalSeconds;
            _clock.Reset();
            _playing = value;
            UpdateTimer();
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _elapsed += _clock.Elapsed.TotalSeconds;
        _clock.Reset();
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateTimer()
    {
        if (_playing && TopLevel.GetTopLevel(this) is not null)
        {
            _clock.Start();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        context.FillRectangle(Sky[0], new Rect(Bounds.Size));
        double scale = Math.Min(Bounds.Width / SceneWidth, Bounds.Height / SceneHeight);
        using var transform = context.PushTransform(new Matrix(scale, 0, 0, scale,
            (Bounds.Width - SceneWidth * scale) / 2, (Bounds.Height - SceneHeight * scale) / 2));
        for (int i = 0; i < Sky.Length; i++)
        {
            context.FillRectangle(Sky[i], new Rect(0, i * 4, SceneWidth, 4));
        }

        var floor = new Pen(new SolidColorBrush(Color.Parse("#29314D")), 1);
        for (int x = -240; x <= 560; x += 40)
        {
            context.DrawLine(floor, new Point(160, 160), new Point(x, 190));
        }

        foreach (int y in new[] { 161, 164, 170, 178, 188 })
        {
            context.DrawLine(floor, new Point(0, y), new Point(320, y));
        }

        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#585181")), 1), new Point(0, 160), new Point(320, 160));

        double time = _elapsed + _clock.Elapsed.TotalSeconds;
        // The initial isometric view exposes three faces, like the familiar brand mark.
        double yaw = Math.PI / 4 + time * 0.29;
        double pitch = -0.61548 + Math.Sin(time * 0.31) * 0.32;
        double roll = Math.Sin(time * 0.23) * 0.2;
        var points = new Vertex[8];
        for (int i = 0; i < 8; i++)
        {
            double x = (i & 1) == 0 ? -1 : 1, y = (i & 2) == 0 ? -1 : 1, z = (i & 4) == 0 ? -1 : 1;
            double a = x * Math.Cos(yaw) + z * Math.Sin(yaw), b = z * Math.Cos(yaw) - x * Math.Sin(yaw);
            double c = y * Math.Cos(pitch) - b * Math.Sin(pitch), d = y * Math.Sin(pitch) + b * Math.Cos(pitch);
            double u = a * Math.Cos(roll) - c * Math.Sin(roll), v = a * Math.Sin(roll) + c * Math.Cos(roll);
            points[i] = new(new Point(Math.Round(160 + u * 38), Math.Round(88 + v * 38)), d, u, v);
        }
        // Project the rotating frame onto the floor along a fixed, angled light direction.
        // The open structure casts an open shadow instead of a solid cube silhouette.
        var shadow = new Pen(new SolidColorBrush(Color.Parse("#B0060915")), 3, lineCap: PenLineCap.Round);
        foreach (var (a, b) in Edges)
        {
            context.DrawLine(shadow, FloorProjection(points[a]), FloorProjection(points[b]));
        }

        foreach (var vertex in points)
        {
            context.DrawEllipse(shadow.Brush, null, FloorProjection(vertex), 2, 1);
        }

        var layers = new List<(double Depth, Action Draw)>();
        foreach (var (a, b) in Edges)
        {
            var start = points[a];
            var end = points[b];
            double depth = (start.Depth + end.Depth) / 2;
            var ink = Ink((start.Point.X + end.Point.X) / 2, depth);
            layers.Add((depth, () => context.DrawLine(new Pen(ink, depth < 0 ? 3 : 5, lineCap: PenLineCap.Round), start.Point, end.Point)));
        }
        foreach (var vertex in points)
        {
            layers.Add((vertex.Depth + 0.01, () => context.DrawEllipse(Ink(vertex.Point.X, vertex.Depth), null, vertex.Point, vertex.Depth < 0 ? 3 : 5, vertex.Depth < 0 ? 3 : 5)));
        }

        var spark = new Point(160, 88);
        layers.Add((double.MaxValue, () =>
        {
            // Keep the central brand spark readable as the frame rotates behind it.
            context.DrawEllipse(Sky[22], null, spark, 12, 12);
            var star = new StreamGeometry();
            using (var path = star.Open())
            {
                path.BeginFigure(spark + new Vector(0, -18), true);
                foreach (var offset in new[] { new Vector(6, -6), new Vector(18, 0), new Vector(6, 6), new Vector(0, 18), new Vector(-6, 6), new Vector(-18, 0), new Vector(-6, -6) })
                {
                    path.LineTo(spark + offset);
                }

                path.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#7264FF")), null, star);
        }
        ));
        foreach (var layer in layers.OrderBy(layer => layer.Depth))
        {
            layer.Draw();
        }
        // Restrained scanlines keep the retro texture confined to the illustration.
        var scanline = new SolidColorBrush(Color.Parse("#14000000"));
        for (int y = 0; y < SceneHeight; y += 3)
        {
            context.FillRectangle(scanline, new Rect(0, y, SceneWidth, 1));
        }
    }

    private static IBrush Ink(double x, double depth)
    {
        double blend = Math.Clamp((x - 95) / 130, 0, 1);
        double light = depth < 0 ? 0.38 : 0.92 + Math.Min(depth, 1.7) * 0.04;
        return new SolidColorBrush(Color.FromRgb((byte)((2 + 115 * blend) * light), (byte)((105 - 40 * blend) * light), (byte)(255 * light)));
    }

    private static (int, int)[] BuildEdges() =>
        (from a in Enumerable.Range(0, 8) from bit in new[] { 1, 2, 4 } where a < (a ^ bit) select (a, a ^ bit)).ToArray();

    private static Point FloorProjection(Vertex vertex)
    {
        const double floorHeight = 2.3;
        double fall = floorHeight - vertex.Y;
        return new Point(Math.Round(160 + (vertex.X + fall * 0.25) * 22),
            Math.Round(172 + (vertex.Depth + fall * 0.15) * 4));
    }

    private readonly record struct Vertex(Point Point, double Depth, double X, double Y);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
}
