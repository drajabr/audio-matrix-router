using Avalonia;
using Avalonia.Media;

namespace AudioMatrixRouter.App;

/// <summary>
/// Design tokens — single source of truth, values verbatim from docs/DESIGN-REFERENCE.md §2
/// (default `black` background preset). Preset switching later rebuilds these via a
/// ThemeService; for now they are the fixed contract every control draws against.
/// </summary>
public static class Theme
{
    // ===== geometry (the square law lives HERE and nowhere else) =====
    public const double Unit = 54;          // atomic channel square
    public const double Gap = 4;
    public const double DeviceTile = Unit * 2 + Gap;   // 112 — a 2ch device tile
    public const double ChipShort = 28;     // channel chip strip short side
    public const double BadgeSize = 20;     // MASTER edge bar thickness
    public const double BadgeInset = 24;    // content/meter inset on the badge edge
    public const double RadiusPanel = 8;
    public const double RadiusOverlay = 6;
    public const double RadiusTile = 5;
    public const double RadiusMicro = 4;
    public const double LabelSquareMin = 140;
    public const double LabelSquareMax = 360;
    public const double LabelSquareDefault = 224;

    // ===== colors =====
    public static readonly Color Bg = Color.Parse("#101113");
    public static readonly Color Surface = Color.Parse("#1A1D22");
    public static readonly Color Panel = Color.Parse("#16181D");
    public static readonly Color Line = Color.Parse("#39404D");
    public static readonly Color Text = Color.Parse("#DBE0E8");
    public static readonly Color Muted = Color.Parse("#9AA4B2");
    public static readonly Color Accent = Color.Parse("#2DD4BF");
    public static readonly Color AccentHl = Color.Parse("#77F0DF");
    public static readonly Color Phase = Color.Parse("#8B5CF6");
    public static readonly Color Danger = Color.Parse("#EF4444");

    /// <summary>CSS color-mix(in srgb, a w%, b (100-w)%) equivalent.</summary>
    public static Color Mix(Color a, Color b, double weightA)
    {
        var wa = Math.Clamp(weightA, 0, 1);
        var wb = 1 - wa;
        return Color.FromArgb(
            (byte)Math.Round(a.A * wa + b.A * wb),
            (byte)Math.Round(a.R * wa + b.R * wb),
            (byte)Math.Round(a.G * wa + b.G * wb),
            (byte)Math.Round(a.B * wa + b.B * wb));
    }

    public static Color WithAlpha(Color c, double alpha) =>
        Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B);

    // ===== derived tokens (mirror the CSS derivations) =====
    public static readonly Color TextStrong = Mix(Text, Colors.White, 0.92);
    public static readonly Color TextOnAccent = Mix(Bg, Colors.Black, 0.82);
    public static readonly Color LineStrong = Mix(Line, Colors.White, 0.86);

    // ===== brushes =====
    public static readonly IBrush BgBrush = new SolidColorBrush(Bg);
    public static readonly IBrush SurfaceBrush = new SolidColorBrush(Surface);
    public static readonly IBrush PanelBrush = new SolidColorBrush(Panel);
    public static readonly IBrush LineBrush = new SolidColorBrush(Line);
    public static readonly IBrush LineStrongBrush = new SolidColorBrush(LineStrong);
    public static readonly IBrush TextBrush = new SolidColorBrush(Text);
    public static readonly IBrush TextStrongBrush = new SolidColorBrush(TextStrong);
    public static readonly IBrush TextOnAccentBrush = new SolidColorBrush(TextOnAccent);
    public static readonly IBrush MutedBrush = new SolidColorBrush(Muted);
    public static readonly IBrush AccentBrush = new SolidColorBrush(Accent);
    public static readonly IBrush AccentHlBrush = new SolidColorBrush(AccentHl);

    /// <summary>Key face: the raised-button gradient (surface +7% white → surface −16% black).</summary>
    public static IBrush KeyFace(double lightTop = 0.07, double darkBottom = 0.16) =>
        new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Mix(Surface, Colors.White, 1 - lightTop), 0),
                new GradientStop(Mix(Surface, Colors.Black, 1 - darkBottom), 1),
            }
        };

    /// <summary>Lit accent key face (active tiles, MASTER badges).</summary>
    public static IBrush AccentFace() =>
        new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Mix(AccentHl, Colors.White, 0.72), 0),
                new GradientStop(Mix(Accent, Colors.Black, 0.86), 1),
            }
        };

    /// <summary>Glass meter gradient: accent 22% → accent-hl 32% alpha, along the bar axis.</summary>
    public static IBrush MeterFill(bool horizontal) =>
        new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, horizontal ? 0 : 1, RelativeUnit.Relative),
            EndPoint = horizontal
                ? new RelativePoint(1, 0, RelativeUnit.Relative)
                : new RelativePoint(0, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(WithAlpha(Accent, 0.22), 0),
                new GradientStop(WithAlpha(AccentHl, 0.32), 1),
            }
        };
}
