using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using MecchaCamouflage.Controller;

namespace MecchaCamouflage.WebHost;

internal sealed class EspOverlayForm : Form
{
    private static readonly (int A, int B)[] SkeletonPairs =
    [
        (2, 3), (3, 4), (4, 5), (5, 6),
        (5, 8), (8, 9), (9, 10), (10, 11),
        (5, 13), (13, 14), (14, 15), (15, 16),
        (2, 18), (18, 19), (19, 20), (20, 21),
        (2, 23), (23, 24), (24, 25), (25, 26)
    ];

    private readonly HostSession session;
    private readonly EspMemoryReader reader = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    private EspFrame? frame;
    private bool reading;
    private bool started;
    private DateTimeOffset nextErrorLog = DateTimeOffset.MinValue;
    private string lastLoggedError = "";

    public EspOverlayForm(HostSession session)
    {
        this.session = session;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        TransparencyKey = Color.Black;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        timer.Tick += TickOverlay;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int layered = 0x00080000;
            const int transparent = 0x00000020;
            const int toolWindow = 0x00000080;
            const int noActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= layered | transparent | toolWindow | noActivate;
            return parameters;
        }
    }

    public void Start()
    {
        if (started)
            return;
        started = true;
        timer.Start();
    }

    private async void TickOverlay(object? sender, EventArgs eventArgs)
    {
        var settings = session.Settings.Esp;
        if (!settings.Enabled)
        {
            HideOverlay();
            return;
        }

        var gameWindow = reader.GameWindow;
        if (gameWindow != IntPtr.Zero && GetForegroundWindow() != gameWindow)
        {
            HideOverlay();
            return;
        }

        if (!reading)
        {
            reading = true;
            try
            {
                frame = await Task.Run(() => reader.ReadFrame(session.Settings.GameProcessName));
            }
            finally
            {
                reading = false;
            }
        }

        gameWindow = reader.GameWindow;
        if (frame is null || gameWindow == IntPtr.Zero || GetForegroundWindow() != gameWindow ||
            !TryGetClientBounds(gameWindow, out var bounds))
        {
            LogReaderError();
            HideOverlay();
            return;
        }

        if (Bounds != bounds)
            Bounds = bounds;
        if (!Visible)
            Show();
        Invalidate();
    }

    private void LogReaderError()
    {
        var error = reader.LastError;
        if (string.IsNullOrWhiteSpace(error))
            return;
        var now = DateTimeOffset.UtcNow;
        if (error == lastLoggedError && now < nextErrorLog)
            return;
        lastLoggedError = error;
        nextErrorLog = now.AddSeconds(10);
        session.Log.Warn("ESP: " + error);
    }

    private void HideOverlay()
    {
        if (Visible)
            Hide();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var current = frame;
        var settings = session.Settings.Esp;
        if (!settings.Enabled || current is null)
            return;

        var color = Color.FromArgb(255, settings.Color.R, settings.Color.G, settings.Color.B);
        using var outlinePen = new Pen(Color.FromArgb(230, 1, 1, 1), 4f);
        using var colorPen = new Pen(color, 2f);
        using var textBrush = new SolidBrush(color);
        using var shadowBrush = new SolidBrush(Color.FromArgb(230, 1, 1, 1));
        using var font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        eventArgs.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        foreach (var player in current.Players)
        {
            if (!TryGetBounds(current.View, player, ClientSize, out var bounds))
                continue;

            if (settings.Boxes)
            {
                eventArgs.Graphics.DrawRectangle(outlinePen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                eventArgs.Graphics.DrawRectangle(colorPen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            }
            if (settings.Snaplines)
            {
                var from = new PointF(ClientSize.Width / 2f, ClientSize.Height - 2f);
                var to = new PointF(bounds.X + bounds.Width / 2f, bounds.Bottom);
                eventArgs.Graphics.DrawLine(outlinePen, from, to);
                eventArgs.Graphics.DrawLine(colorPen, from, to);
            }
            if (settings.Skeletons && player.Bones.Count >= 28)
                DrawSkeleton(eventArgs.Graphics, current.View, player.Bones, ClientSize, outlinePen, colorPen);

            var labels = new List<string>(2);
            if (settings.Names)
                labels.Add(player.Name);
            if (settings.Distance)
                labels.Add($"{Math.Round(player.DistanceMeters):0} m");
            if (labels.Count > 0)
            {
                var label = string.Join("  ", labels);
                var size = eventArgs.Graphics.MeasureString(label, font);
                var point = new PointF(bounds.X + (bounds.Width - size.Width) / 2f, bounds.Y - size.Height - 3f);
                eventArgs.Graphics.DrawString(label, font, shadowBrush, point.X + 1f, point.Y + 1f);
                eventArgs.Graphics.DrawString(label, font, textBrush, point);
            }
        }
    }

    private static void DrawSkeleton(
        Graphics graphics,
        EspMemoryReader.ViewInfo view,
        IReadOnlyList<EspMemoryReader.Vec3> bones,
        Size viewport,
        Pen outline,
        Pen color)
    {
        foreach (var (a, b) in SkeletonPairs)
        {
            if (!TryProject(view, bones[a], viewport, out var start) ||
                !TryProject(view, bones[b], viewport, out var end))
                continue;
            graphics.DrawLine(outline, start, end);
            graphics.DrawLine(color, start, end);
        }
    }

    private static bool TryGetBounds(
        EspMemoryReader.ViewInfo view,
        EspPlayer player,
        Size viewport,
        out RectangleF bounds)
    {
        if (player.Bones.Count >= 28)
        {
            var points = player.Bones
                .Select(bone => TryProject(view, bone, viewport, out var point) ? point : (PointF?)null)
                .Where(point => point.HasValue)
                .Select(point => point!.Value)
                .Where(point => point.X >= -viewport.Width && point.X <= viewport.Width * 2 &&
                                point.Y >= -viewport.Height && point.Y <= viewport.Height * 2)
                .ToArray();
            if (points.Length >= 2)
            {
                var left = points.Min(point => point.X) - 3f;
                var right = points.Max(point => point.X) + 3f;
                var top = points.Min(point => point.Y) - 3f;
                var bottom = points.Max(point => point.Y) + 3f;
                if (right > left && bottom > top && right - left < viewport.Width && bottom - top < viewport.Height)
                {
                    bounds = RectangleF.FromLTRB(left, top, right, bottom);
                    return true;
                }
            }
        }

        var topWorld = player.Location with { Z = player.Location.Z + 90.0 };
        var bottomWorld = player.Location with { Z = player.Location.Z - 90.0 };
        if (!TryProject(view, topWorld, viewport, out var topPoint) ||
            !TryProject(view, bottomWorld, viewport, out var bottomPoint))
        {
            bounds = default;
            return false;
        }
        var height = Math.Abs(bottomPoint.Y - topPoint.Y);
        var width = height * 0.55f;
        bounds = new RectangleF(bottomPoint.X - width / 2f, Math.Min(topPoint.Y, bottomPoint.Y), width, height);
        return height > 1f;
    }

    private static bool TryProject(
        EspMemoryReader.ViewInfo view,
        EspMemoryReader.Vec3 world,
        Size viewport,
        out PointF screen)
    {
        screen = default;
        if (viewport.Width <= 0 || viewport.Height <= 0)
            return false;
        const double radians = Math.PI / 180.0;
        var pitch = view.Rotation.Pitch * radians;
        var yaw = view.Rotation.Yaw * radians;
        var yawY = (view.Rotation.Yaw + 89.8) * radians;
        var pitchZ = (view.Rotation.Pitch + 89.8) * radians;
        var axisX = Normalize(new EspMemoryReader.Vec3(Math.Cos(yaw) * Math.Cos(pitch), Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch)));
        var axisY = Normalize(new EspMemoryReader.Vec3(Math.Cos(yawY), Math.Sin(yawY), 0));
        var axisZ = Normalize(new EspMemoryReader.Vec3(Math.Cos(yaw) * Math.Cos(pitchZ), Math.Sin(yaw) * Math.Cos(pitchZ), Math.Sin(pitchZ)));
        var delta = new EspMemoryReader.Vec3(world.X - view.Location.X, world.Y - view.Location.Y, world.Z - view.Location.Z);
        var transformedX = Dot(delta, axisY);
        var transformedY = Dot(delta, axisZ);
        var transformedZ = Dot(delta, axisX);
        if (transformedZ < 1.0)
            return false;
        var tangent = Math.Tan(view.Fov * Math.PI / 360.0);
        if (!double.IsFinite(tangent) || Math.Abs(tangent) < 0.0001)
            return false;
        var focal = (viewport.Width / 2.0) / tangent;
        screen = new PointF(
            (float)(viewport.Width / 2.0 + transformedX * focal / transformedZ),
            (float)(viewport.Height / 2.0 - transformedY * focal / transformedZ));
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    private static EspMemoryReader.Vec3 Normalize(EspMemoryReader.Vec3 value)
    {
        var length = Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
        return length > 0.000001
            ? new EspMemoryReader.Vec3(value.X / length, value.Y / length, value.Z / length)
            : default;
    }

    private static double Dot(EspMemoryReader.Vec3 left, EspMemoryReader.Vec3 right) =>
        left.X * right.X + left.Y * right.Y + left.Z * right.Z;

    private static bool TryGetClientBounds(IntPtr window, out Rectangle bounds)
    {
        bounds = default;
        if (!GetClientRect(window, out var client))
            return false;
        var origin = new NativePoint();
        if (!ClientToScreen(window, ref origin))
            return false;
        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0)
            return false;
        bounds = new Rectangle(origin.X, origin.Y, width, height);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Stop();
            timer.Dispose();
            reader.Dispose();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
}
