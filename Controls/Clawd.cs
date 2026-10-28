using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RAM.Controls;

public enum ClawdMood { Idle, Working, Happy, Error, Sleeping }

/// <summary>
/// Clawd, the Claude Code mascot, drawn from the terminal glyph art
/// <c> ▐▛███▜▌ / ▝▜█████▛▘ /   ▘▘ ▝▝ </c>: every quadrant block becomes one pixel that is twice as tall as it is
/// wide (terminal cells are 1:2), giving an 18 × 5 sprite.
/// </summary>
public sealed class Clawd : FrameworkElement
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(nameof(Mood), typeof(ClawdMood),
        typeof(Clawd), new FrameworkPropertyMetadata(ClawdMood.Idle, (d, _) => ((Clawd)d).OnMoodChanged()));

    public static readonly DependencyProperty AnimatedProperty = DependencyProperty.Register(nameof(Animated), typeof(bool),
        typeof(Clawd), new FrameworkPropertyMetadata(true, (d, _) => ((Clawd)d).UpdateTimer()));

    public static readonly DependencyProperty BodyBrushProperty = DependencyProperty.Register(nameof(BodyBrush), typeof(Brush),
        typeof(Clawd), new FrameworkPropertyMetadata(Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57))),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EyeBrushProperty = DependencyProperty.Register(nameof(EyeBrush), typeof(Brush),
        typeof(Clawd), new FrameworkPropertyMetadata(Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x13))),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FollowMouseProperty = DependencyProperty.Register(nameof(FollowMouse), typeof(bool),
        typeof(Clawd), new FrameworkPropertyMetadata(true));

    static readonly Brush Shadow = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)));

    readonly DispatcherTimer _timer;
    readonly Random _rng = new();
    DateTime _epoch = DateTime.Now;
    DateTime _moodSince = DateTime.Now;
    double _nextBlink = 2.5;
    double _blinkUntil;
    double _look;
    Window? _window;

    public Clawd()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            // Stop animating while the window is minimized (IsVisible stays true in that state).
            if (Window.GetWindow(this) is { } window && !ReferenceEquals(_window, window))
            {
                _window = window;
                window.StateChanged += (_, _) => UpdateTimer();
            }
            UpdateTimer();
        };
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => UpdateTimer();
    }

    public ClawdMood Mood { get => (ClawdMood)GetValue(MoodProperty); set => SetValue(MoodProperty, value); }
    public bool Animated { get => (bool)GetValue(AnimatedProperty); set => SetValue(AnimatedProperty, value); }
    public Brush BodyBrush { get => (Brush)GetValue(BodyBrushProperty); set => SetValue(BodyBrushProperty, value); }
    public Brush EyeBrush { get => (Brush)GetValue(EyeBrushProperty); set => SetValue(EyeBrushProperty, value); }
    public bool FollowMouse { get => (bool)GetValue(FollowMouseProperty); set => SetValue(FollowMouseProperty, value); }

    /// <summary>Plays a mood for a while, then returns to idle.</summary>
    public void Flash(ClawdMood mood, double seconds = 1.6)
    {
        Mood = mood;
        var back = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        back.Tick += (_, _) =>
        {
            back.Stop();
            if (Mood == mood) Mood = ClawdMood.Idle;
        };
        back.Start();
    }

    static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    void OnMoodChanged()
    {
        _moodSince = DateTime.Now;
        InvalidateVisual();
    }

    void UpdateTimer()
    {
        if (Animated && IsLoaded && IsVisible && _window?.WindowState != WindowState.Minimized) _timer.Start();
        else _timer.Stop();
        InvalidateVisual();
    }

    double Now => (DateTime.Now - _epoch).TotalSeconds;
    double MoodAge => (DateTime.Now - _moodSince).TotalSeconds;

    void Tick()
    {
        double t = Now;
        if (t > _nextBlink)
        {
            _blinkUntil = t + 0.13;
            _nextBlink = t + 2.2 + _rng.NextDouble() * 3.5;
        }

        double target = 0;
        if (Mood == ClawdMood.Working) target = Math.Sin(t * 3.2);
        else if (FollowMouse && IsMouseCaptureWithin == false && ActualWidth > 0)
        {
            var p = Mouse.GetPosition(this);
            double dx = p.X - ActualWidth / 2;
            if (Math.Abs(dx) < 2000 && Math.Abs(p.Y) < 2000) target = Math.Clamp(dx / 160.0, -1, 1);
        }
        _look += (target - _look) * 0.18;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 72 : available.Width;
        double h = double.IsInfinity(available.Height) ? w * 12 / 18 : available.Height;
        return new Size(Math.Min(w, h * 18 / 12), Math.Min(h, w * 12 / 18));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 18 units wide, 12 tall: 2 units of headroom for hops + 5 rows × 2 units + shadow.
        double u = Math.Min(w / 18.0, h / 12.0);
        double ox = (w - 18 * u) / 2;
        double oy = (h - 12 * u) / 2 + 1.4 * u;

        double t = Animated ? Now : 0;
        double age = MoodAge;
        double bob = 0, hop = 0, shake = 0;
        bool armsUp = false, eyesClosed = false, xEyes = false;
        int legPhase = 0;

        switch (Mood)
        {
            case ClawdMood.Idle:
                bob = Math.Sin(t * 2.1) * 0.10 * u;
                break;
            case ClawdMood.Working:
                bob = -Math.Abs(Math.Sin(t * 9)) * 0.32 * u;
                legPhase = (int)(t / 0.14) % 2 == 0 ? 1 : 2;
                break;
            case ClawdMood.Happy:
                armsUp = true;
                hop = age < 1.4 ? Math.Abs(Math.Sin(age * 7.2)) * 1.3 * u : 0;
                break;
            case ClawdMood.Error:
                xEyes = true;
                shake = age < 0.7 ? Math.Sin(age * 46) * 0.45 * u * (1 - age / 0.7) : 0;
                break;
            case ClawdMood.Sleeping:
                eyesClosed = true;
                bob = Math.Sin(t * 1.3) * 0.08 * u;
                break;
        }
        if (Animated && t < _blinkUntil) eyesClosed = true;

        double row = 2 * u;
        double bx = ox + shake;
        double by = oy + bob - hop;
        // Ground shadow shrinks while hopping.
        double shadowScale = 1 - Math.Min(hop / (1.3 * u), 1) * 0.35;
        dc.DrawEllipse(Shadow, null, new Point(ox + 9 * u, oy + 5 * row + 0.55 * u), 7.2 * u * shadowScale, 0.5 * u);

        // Body, arms and legs go into ONE geometry so shared edges never show anti-aliasing seams.
        var shape = new GeometryGroup { FillRule = FillRule.Nonzero };
        void Span(double x, double y, double width, double height) =>
            shape.Children.Add(new RectangleGeometry(new Rect(bx + x * u, by + y * row, width * u, height * row)));

        Span(3, 0, 12, 4);
        if (armsUp)
        {
            Span(1, 1.15, 2, 0.85);
            Span(15, 1.15, 2, 0.85);
        }
        else
        {
            Span(1, 2, 2, 1);
            Span(15, 2, 2, 1);
        }

        // Legs stay planted while the body bobs (they overlap the body bottom so the join stays solid).
        double legTop = oy + 4 * row - hop;
        void Leg(double x, bool lifted)
        {
            double lift = lifted ? 0.45 * row : 0;
            double top = Math.Min(legTop, by + 4 * row) - 0.3 * u;
            shape.Children.Add(new RectangleGeometry(new Rect(ox + shake + x * u, top, u, legTop + row - lift - top)));
        }
        Leg(4, legPhase == 1);
        Leg(6, legPhase == 2);
        Leg(11, legPhase == 1);
        Leg(13, legPhase == 2);
        dc.DrawGeometry(BodyBrush, null, shape);

        // Eyes: the empty quadrants of ▛ and ▜ — lower half of the first text row, i.e. pixel row 1.
        var eye = EyeBrush;
        double look = _look * 0.22 * u;
        foreach (double ex in new[] { 5.0, 12.0 })
        {
            double x = bx + ex * u + look;
            double y = by + row;
            if (xEyes)
            {
                var pen = new Pen(eye, 0.32 * u) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square };
                dc.DrawLine(pen, new Point(x + 0.05 * u, y + 0.35 * u), new Point(x + 0.95 * u, y + 1.65 * u));
                dc.DrawLine(pen, new Point(x + 0.95 * u, y + 0.35 * u), new Point(x + 0.05 * u, y + 1.65 * u));
            }
            else if (eyesClosed)
            {
                dc.DrawRectangle(eye, null, new Rect(x, y + 1.15 * u, u, 0.38 * u));
            }
            else
            {
                dc.DrawRectangle(eye, null, new Rect(x, y, u, row));
            }
        }

        if (Mood == ClawdMood.Sleeping && Animated)
        {
            for (int i = 0; i < 2; i++)
            {
                double phase = (t * 0.45 + i * 0.5) % 1.0;
                var z = new FormattedText("z", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Georgia"), (1.6 + phase * 1.6) * u, BodyBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.PushOpacity(1 - phase);
                dc.DrawText(z, new Point(ox + (15.5 + phase * 2) * u, oy - (0.5 + phase * 2.2) * u - u));
                dc.Pop();
            }
        }
    }
}
