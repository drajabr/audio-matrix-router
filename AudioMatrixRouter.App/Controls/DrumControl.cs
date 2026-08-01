using System.Globalization;
using Avalonia;
using AppTheme = AudioMatrixRouter.App.Theme;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AudioMatrixRouter.App.Controls;

/// <summary>
/// The signature ribbed drum control (gain wheel / buffer drums) per
/// docs/DESIGN-REFERENCE.md §3.2: recessed near-black housing with deep inset shadows,
/// 12px ribbed drum texture scrolled by interaction, barrel curvature overlay, 2px accent
/// LED strip, floating value + caption. Wheel = ±Step, vertical drag = PxPerStep px per
/// Step, middle-click = DefaultValue.
/// </summary>
public sealed class DrumControl : Control
{
    private const double RibPeriod = 12;

    private static readonly FontFamily MonoFamily = new("Consolas,Courier New,monospace");
    private static readonly Typeface FaceHeavy = new(MonoFamily, FontStyle.Normal, FontWeight.ExtraBold);
    private static readonly Typeface FaceRegular = new(MonoFamily);

    private static readonly Color HousingColor = Color.Parse("#050505");
    private static readonly IBrush HousingFill = new SolidColorBrush(HousingColor);
    private static readonly IPen HousingPen = new Pen(new SolidColorBrush(AppTheme.Mix(AppTheme.Line, Colors.Black, 0.5)), 1);
    private static readonly IPen OuterGlowPen = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.28)), 2);

    private static readonly IBrush DrumBase = new SolidColorBrush(AppTheme.Mix(AppTheme.Surface, Colors.Black, 0.62));
    private static readonly IBrush RibGroove = new SolidColorBrush(AppTheme.WithAlpha(Colors.Black, 0.85));
    private static readonly IBrush RibCrest = new SolidColorBrush(AppTheme.WithAlpha(Colors.White, 0.10));
    private static readonly IBrush RibBody = new SolidColorBrush(AppTheme.WithAlpha(Colors.White, 0.045));
    private static readonly IBrush RibSlope = new SolidColorBrush(AppTheme.WithAlpha(Colors.Black, 0.45));
    private static readonly IBrush RibGap = new SolidColorBrush(AppTheme.WithAlpha(Colors.Black, 0.65));

    private static readonly IBrush BarrelOverlay = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(AppTheme.WithAlpha(Colors.Black, 0.80), 0),
            new GradientStop(AppTheme.WithAlpha(Colors.White, 0.07), 0.5),
            new GradientStop(AppTheme.WithAlpha(Colors.Black, 0.80), 1),
        }
    };

    private static readonly IBrush TopShadow = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(AppTheme.WithAlpha(Colors.Black, 0.85), 0),
            new GradientStop(AppTheme.WithAlpha(Colors.Black, 0), 1),
        }
    };

    private static readonly IBrush BottomShadow = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(AppTheme.WithAlpha(Colors.Black, 0.85), 0),
            new GradientStop(AppTheme.WithAlpha(Colors.Black, 0), 1),
        }
    };

    private static readonly IBrush LedFill = new SolidColorBrush(AppTheme.WithAlpha(AppTheme.AccentHl, 0.90));
    private static readonly IPen LedGlowInner = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.35)), 2);
    private static readonly IPen LedGlowOuter = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.15)), 4);

    private static readonly IBrush ValueShadowBrush = new SolidColorBrush(AppTheme.WithAlpha(Colors.Black, 0.85));
    private static readonly IBrush CaptionBrush = new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Muted, 0.72));

    private double _value;
    private double _texOffset;

    private bool _dragging;
    private double _dragStartY;
    private double _dragStartValue;

    public double Value
    {
        get => _value;
        set
        {
            if (Math.Abs(value - _value) < 1e-12) return;
            _value = value;
            InvalidateVisual();
        }
    }

    public double Minimum { get; set; }
    public double Maximum { get; set; }
    public double Step { get; set; } = 1;
    public double DefaultValue { get; set; }
    public double PxPerStep { get; set; } = 10;

    /// <summary>Small label above the value (e.g. "IN"); null = value only.</summary>
    public string? Caption { get; set; }

    /// <summary>Default: v => v.ToString("0.#").</summary>
    public Func<double, string>? ValueFormatter { get; set; }

    /// <summary>Raised after each wheel step / drag step / middle-click reset.</summary>
    public event EventHandler<double>? ValueCommitted;

    // ===================================================================== interaction

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Math.Abs(e.Delta.Y) > 0)
        {
            ApplyValue(_value + (e.Delta.Y > 0 ? Step : -Step));
            e.Handled = true;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);

        if (pt.Properties.IsMiddleButtonPressed)
        {
            ApplyValue(DefaultValue);
            e.Handled = true;
            return;
        }

        if (pt.Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            _dragStartY = pt.Position.Y;
            _dragStartValue = _value;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        // drag up = increase; PxPerStep pixels of travel per Step
        var dy = _dragStartY - e.GetCurrentPoint(this).Position.Y;
        var steps = Math.Truncate(dy / Math.Max(1, PxPerStep));
        ApplyValue(_dragStartValue + steps * Step);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    private void ApplyValue(double target)
    {
        if (Maximum > Minimum) target = Math.Clamp(target, Minimum, Maximum);
        if (Math.Abs(target - _value) < 1e-9) return;

        // the drum texture rolls with the value
        var step = Math.Abs(Step) < 1e-9 ? 1 : Step;
        _texOffset -= (target - _value) / step * Math.Max(6, PxPerStep);

        _value = target;
        InvalidateVisual();
        ValueCommitted?.Invoke(this, _value);
    }

    // ===================================================================== rendering

    public override void Render(DrawingContext context)
    {
        var r = new Rect(Bounds.Size);
        if (r.Width < 4 || r.Height < 4) return;

        var housing = r.Deflate(0.5);
        var rr = new RoundedRect(housing, AppTheme.RadiusOverlay);

        // recessed housing + idle accent glow around it
        context.DrawRectangle(null, OuterGlowPen, new RoundedRect(housing.Inflate(1.5), AppTheme.RadiusOverlay + 1.5));
        context.DrawRectangle(HousingFill, HousingPen, rr);

        var inner = housing.Deflate(1);
        using (context.PushClip(new RoundedRect(inner, AppTheme.RadiusOverlay - 1)))
        {
            context.DrawRectangle(DrumBase, null, inner);

            // ribbed drum texture, 12px per rib: 0–1 groove · 1–3 crest · 3–9 body · 9–11 slope · 11–12 gap
            var off = _texOffset % RibPeriod;
            if (off < 0) off += RibPeriod;
            for (var y = inner.Top - RibPeriod + off; y < inner.Bottom; y += RibPeriod)
            {
                context.DrawRectangle(RibGroove, null, new Rect(inner.X, y + 0, inner.Width, 1));
                context.DrawRectangle(RibCrest, null, new Rect(inner.X, y + 1, inner.Width, 2));
                context.DrawRectangle(RibBody, null, new Rect(inner.X, y + 3, inner.Width, 6));
                context.DrawRectangle(RibSlope, null, new Rect(inner.X, y + 9, inner.Width, 2));
                context.DrawRectangle(RibGap, null, new Rect(inner.X, y + 11, inner.Width, 1));
            }

            // barrel curvature (dark edges → light center) + deep top/bottom inset shadows
            context.DrawRectangle(BarrelOverlay, null, inner);
            var shadowH = Math.Min(14, inner.Height / 3);
            context.DrawRectangle(TopShadow, null, new Rect(inner.X, inner.Y, inner.Width, shadowH));
            context.DrawRectangle(BottomShadow, null, new Rect(inner.X, inner.Bottom - shadowH, inner.Width, shadowH));

            // accent LED strip near the bottom: 2px tall, 12% side insets, pill, glowing
            var ledInset = inner.Width * 0.12;
            var led = new Rect(inner.X + ledInset, inner.Bottom - 6, inner.Width - 2 * ledInset, 2);
            context.DrawRectangle(null, LedGlowOuter, new RoundedRect(led.Inflate(3), 4));
            context.DrawRectangle(null, LedGlowInner, new RoundedRect(led.Inflate(1.5), 2.5));
            context.DrawRectangle(LedFill, null, new RoundedRect(led, 1));

            DrawValue(context, inner);
        }
    }

    private void DrawValue(DrawingContext context, Rect inner)
    {
        var text = (ValueFormatter ?? DefaultFormat)(_value);
        var value = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            FaceHeavy, 15, AppTheme.TextStrongBrush);
        var shadow = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            FaceHeavy, 15, ValueShadowBrush);

        var cx = inner.Center.X;
        var cy = inner.Center.Y;

        FormattedText? caption = null;
        if (!string.IsNullOrEmpty(Caption))
        {
            caption = new FormattedText(Caption!.ToUpperInvariant(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, FaceRegular, 8, CaptionBrush);
            // caption + value stacked, centered as a block
            var total = caption.Height + 1 + value.Height;
            var top = cy - total / 2;
            context.DrawText(caption, new Point(cx - caption.Width / 2, top));
            var vy = top + caption.Height + 1;
            context.DrawText(shadow, new Point(cx - value.Width / 2, vy + 1.5));
            context.DrawText(value, new Point(cx - value.Width / 2, vy));
        }
        else
        {
            var vy = cy - value.Height / 2;
            context.DrawText(shadow, new Point(cx - value.Width / 2, vy + 1.5));
            context.DrawText(value, new Point(cx - value.Width / 2, vy));
        }
    }

    private static string DefaultFormat(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
