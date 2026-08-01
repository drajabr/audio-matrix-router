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
/// LED strip, floating value (+ optional small caption above / unit suffix). Wheel = ±Step,
/// vertical drag = PxPerStep px per Step, middle-click = DefaultValue.
/// </summary>
public sealed class DrumControl : Control
{
    private const double RibPeriod = 12;

    // ===== theme-derived palette, re-cached whenever AppTheme.Apply bumps Version =====
    private static int s_palVersion = -1;

    private static Typeface FaceHeavy;
    private static Typeface FaceSemiBold;

    private static IBrush HousingFill = null!;
    private static IPen HousingPen = null!;
    private static IPen OuterGlowPen = null!;

    private static IBrush DrumBase = null!;
    private static IBrush RibGroove = null!;
    private static IBrush RibCrest = null!;
    private static IBrush RibBody = null!;
    private static IBrush RibSlope = null!;
    private static IBrush RibGap = null!;

    private static IBrush BarrelOverlay = null!;
    private static IBrush TopShadow = null!;
    private static IBrush BottomShadow = null!;

    private static IBrush LedFill = null!;
    private static IPen LedGlowInner = null!;
    private static IPen LedGlowOuter = null!;

    private static IBrush ValueShadowBrush = null!;
    private static IBrush CaptionBrush = null!;
    private static IBrush SuffixBrush = null!;

    private static void EnsurePalette()
    {
        if (s_palVersion == AppTheme.Version) return;
        s_palVersion = AppTheme.Version;

        FaceHeavy = AppTheme.FaceHeavy;
        FaceSemiBold = AppTheme.FaceSemiBold;

        HousingFill = new SolidColorBrush(Color.Parse("#050505"));
        HousingPen = new Pen(new SolidColorBrush(AppTheme.Mix(AppTheme.Line, Colors.Black, 0.5)), 1);
        OuterGlowPen = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.14)), 2);

        // rib colors verbatim from the CSS repeating gradient:
        //   groove rgba(0,0,0,.90) · crest surface+10% white · body surface+32% black
        //   slope rgba(0,0,0,.70) · gap rgba(0,0,0,.90)
        DrumBase = new SolidColorBrush(AppTheme.Mix(AppTheme.Surface, Colors.Black, 0.68));
        RibGroove = new SolidColorBrush(AppTheme.WithAlpha(Colors.Black, 0.90));
        RibCrest = new SolidColorBrush(AppTheme.Mix(AppTheme.Surface, Colors.White, 0.90));
        RibBody = new SolidColorBrush(AppTheme.Mix(AppTheme.Surface, Colors.Black, 0.68));
        RibSlope = new SolidColorBrush(AppTheme.WithAlpha(Colors.Black, 0.70));
        RibGap = new SolidColorBrush(AppTheme.WithAlpha(Colors.Black, 0.90));

        BarrelOverlay = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(AppTheme.WithAlpha(Colors.Black, 0.80), 0),
                new GradientStop(AppTheme.WithAlpha(Colors.Black, 0.22), 0.28),
                new GradientStop(AppTheme.WithAlpha(Colors.White, 0.07), 0.5),
                new GradientStop(AppTheme.WithAlpha(Colors.Black, 0.22), 0.72),
                new GradientStop(AppTheme.WithAlpha(Colors.Black, 0.80), 1),
            }
        };

        TopShadow = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(AppTheme.WithAlpha(Colors.Black, 0.85), 0),
                new GradientStop(AppTheme.WithAlpha(Colors.Black, 0), 1),
            }
        };

        BottomShadow = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(AppTheme.WithAlpha(Colors.Black, 0.85), 0),
                new GradientStop(AppTheme.WithAlpha(Colors.Black, 0), 1),
            }
        };

        // Subtle backlight strip, not a beacon — stroke "glow" reads far stronger than
        // the CSS blur it mimics, so run well below the CSS alphas.
        LedFill = new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Mix(AppTheme.AccentHl, Colors.White, 0.90), 0.50));
        LedGlowInner = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.10)), 1.5);
        LedGlowOuter = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.04)), 3);

        ValueShadowBrush = new SolidColorBrush(AppTheme.WithAlpha(Colors.Black, 0.85));
        CaptionBrush = new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Muted, 0.9));
        SuffixBrush = new SolidColorBrush(AppTheme.WithAlpha(AppTheme.TextStrong, 0.72));
    }

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

    /// <summary>Tiny caps label ABOVE the value (e.g. "IN"); null = value only.</summary>
    public string? Caption { get; set; }

    /// <summary>Small unit suffix drawn after the value at reduced size (e.g. "dB").</summary>
    public string? Suffix { get; set; }

    /// <summary>Outer accent glow ring — only the master gain wheel has it in the design.</summary>
    public bool ShowGlow { get; set; }

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
        EnsurePalette();
        var r = new Rect(Bounds.Size);
        if (r.Width < 4 || r.Height < 4) return;

        var housing = r.Deflate(0.5);
        var rr = new RoundedRect(housing, AppTheme.RadiusOverlay);

        // Recessed housing. Outer accent glow ONLY when opted in — in the CSS just the
        // master gain wheel has it; buffer drums are plain keys (with a light accent
        // like white, an unconditional ring read as a weird halo on every drum).
        if (ShowGlow)
        {
            context.DrawRectangle(null, OuterGlowPen, new RoundedRect(housing.Inflate(1.5), AppTheme.RadiusOverlay + 1.5));
        }
        context.DrawRectangle(HousingFill, HousingPen, rr);

        var inner = housing.Deflate(1);
        using (context.PushClip(new RoundedRect(inner, AppTheme.RadiusOverlay - 1)))
        {
            // the ribbed drum itself is inset 7px top/bottom inside the housing (CSS inset: 7px 0)
            var drum = new Rect(inner.X, inner.Y + 7, inner.Width, Math.Max(0, inner.Height - 14));
            context.DrawRectangle(DrumBase, null, drum);

            // ribbed drum texture, 12px per rib: 0–1 groove · 1–3 crest · 3–9 body · 9–11 slope · 11–12 gap
            var off = _texOffset % RibPeriod;
            if (off < 0) off += RibPeriod;
            using (context.PushClip(drum))
            {
                for (var y = drum.Top - RibPeriod + off; y < drum.Bottom; y += RibPeriod)
                {
                    context.DrawRectangle(RibGroove, null, new Rect(drum.X, y + 0, drum.Width, 1));
                    context.DrawRectangle(RibCrest, null, new Rect(drum.X, y + 1, drum.Width, 2));
                    context.DrawRectangle(RibBody, null, new Rect(drum.X, y + 3, drum.Width, 6));
                    context.DrawRectangle(RibSlope, null, new Rect(drum.X, y + 9, drum.Width, 2));
                    context.DrawRectangle(RibGap, null, new Rect(drum.X, y + 11, drum.Width, 1));
                }

                // barrel curvature (dark edges → light center)
                context.DrawRectangle(BarrelOverlay, null, drum);
            }

            // deep top/bottom inset shadows over the whole housing interior
            var shadowH = Math.Min(14, inner.Height / 3);
            context.DrawRectangle(TopShadow, null, new Rect(inner.X, inner.Y, inner.Width, shadowH));
            context.DrawRectangle(BottomShadow, null, new Rect(inner.X, inner.Bottom - shadowH, inner.Width, shadowH));

            // subtle accent LED strip near the bottom: 2px tall, 12% side insets, pill
            var ledInset = inner.Width * 0.12;
            var led = new Rect(inner.X + ledInset, inner.Bottom - 5, inner.Width - 2 * ledInset, 2);
            context.DrawRectangle(null, LedGlowOuter, new RoundedRect(led.Inflate(3), 4));
            context.DrawRectangle(null, LedGlowInner, new RoundedRect(led.Inflate(1.5), 2.5));
            context.DrawRectangle(LedFill, null, new RoundedRect(led, 1));

            DrawValue(context, inner);
        }
    }

    private void DrawValue(DrawingContext context, Rect inner)
    {
        var text = (ValueFormatter ?? DefaultFormat)(_value);
        // value: lg, weight 800, text-strong (CSS .corner-gain-wheel-value)
        var value = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            FaceHeavy, AppTheme.FsLg, AppTheme.TextStrongBrush);
        var shadow = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            FaceHeavy, AppTheme.FsLg, ValueShadowBrush);

        FormattedText? suffix = null;
        if (!string.IsNullOrEmpty(Suffix))
        {
            // small unit after the value (CSS .corner-gain-wheel-unit: 2xs, w600, 72%)
            suffix = new FormattedText(Suffix!, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                FaceSemiBold, AppTheme.Fs2xs, SuffixBrush);
        }

        var groupW = value.Width + (suffix is null ? 0 : 2 + suffix.Width);
        var cx = inner.Center.X;
        var cy = inner.Center.Y;

        FormattedText? caption = null;
        if (!string.IsNullOrEmpty(Caption))
        {
            // tiny caps caption ABOVE the value (CSS .buffer-readout-label: w800)
            caption = new FormattedText(Caption!.ToUpperInvariant(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, FaceHeavy, AppTheme.Fs2xs * 0.92, CaptionBrush);
        }

        var totalH = value.Height + (caption is null ? 0 : caption.Height + 2);
        var top = cy - totalH / 2;

        if (caption is not null)
        {
            context.DrawText(caption, new Point(cx - caption.Width / 2, top));
            top += caption.Height + 2;
        }

        var vx = cx - groupW / 2;
        context.DrawText(shadow, new Point(vx, top + 1.5));
        context.DrawText(value, new Point(vx, top));
        if (suffix is not null)
        {
            // baseline-align the small unit with the big value
            var sy = top + (value.Height - suffix.Height) - value.Height * 0.06;
            context.DrawText(suffix, new Point(vx + value.Width + 2, sy));
        }
    }

    private static string DefaultFormat(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
