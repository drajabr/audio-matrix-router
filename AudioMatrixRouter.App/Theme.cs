using Avalonia;
using Avalonia.Media;

namespace AudioMatrixRouter.App;

/// <summary>
/// Design tokens — single source of truth. The five preset tables are verbatim ports of
/// App.jsx (BACKGROUND_PRESETS / ACCENT_PRESETS / FONT_PRESETS / FONT_SIZE_PRESETS /
/// UI_SCALE_PRESETS) so the user's persisted theme keys survive the host swap.
/// <see cref="Apply"/> mutates the palette BEFORE first render; controls read the static
/// properties at render time (and re-cache via <see cref="Version"/>).
/// </summary>
public static class Theme
{
    // ===== geometry (the square law lives HERE and nowhere else) =====
    public const double Unit = 54;          // atomic channel square
    public const double Gap = 6;            // visual gap between tiles (reference shows clear gaps)
    public const double MeterPad = 4;       // meter lane padding/gaps (CSS keeps these at 4)
    public const double DeviceTile = Unit * 2 + Gap;   // a 2ch device tile
    public const double ChipShort = 28;     // channel chip strip short side
    public const double BadgeSize = 20;     // MASTER edge bar thickness
    public const double BadgeInset = 24;    // content/meter inset on the badge edge
    public const double RadiusPanel = 8;
    public const double RadiusOverlay = 6;
    public const double RadiusTile = 8;     // rendered tile radius per reference screenshot
    public const double RadiusMicro = 4;
    public const double LabelSquareMin = 140;
    public const double LabelSquareMax = 360;
    public const double LabelSquareDefault = 224;

    // ===== preset tables (verbatim from App.jsx) =====

    private readonly record struct BackgroundPreset(
        string Key, string Bg, string Surface, string Panel, string Border, string Text, string Muted);

    private static readonly BackgroundPreset[] BackgroundPresets =
    {
        new("black", "#090909", "#121212", "#101010", "#2a2a2a", "#ececec", "#9a9a9a"),
        new("charcoal", "#111111", "#1a1a1a", "#161616", "#333333", "#ececec", "#a0a0a0"),
        new("graphite", "#1a1a1a", "#262626", "#202020", "#444444", "#efefef", "#ababab"),
        new("slate", "#252525", "#323232", "#2c2c2c", "#585858", "#f2f2f2", "#b8b8b8"),
        new("stone", "#383838", "#4a4a4a", "#414141", "#676767", "#f5f5f5", "#c4c4c4"),
        new("silver", "#b3b3b3", "#c6c6c6", "#bbbbbb", "#8f8f8f", "#151515", "#3c3c3c"),
        new("white", "#e6e6e6", "#f2f2f2", "#ebebeb", "#bdbdbd", "#121212", "#4c4c4c"),
    };

    private readonly record struct AccentPreset(string Key, string Accent, string AccentHl);

    private static readonly AccentPreset[] AccentPresets =
    {
        new("black", "#0b0b0b", "#6b7280"),
        new("white", "#e5e7eb", "#ffffff"),
        new("slate", "#334155", "#94a3b8"),
        new("cobalt", "#1d4ed8", "#60a5fa"),
        new("ocean", "#0f766e", "#2dd4bf"),
        new("amber", "#b45309", "#f59e0b"),
        new("crimson", "#b91c1c", "#f87171"),
    };

    private readonly record struct FontPreset(string Key, string Family);

    private static readonly FontPreset[] FontPresets =
    {
        new("plus-jakarta", "Plus Jakarta Sans,Segoe UI"),
        new("manrope", "Manrope,Segoe UI"),
        new("sora", "Sora,Segoe UI"),
        new("bahnschrift", "Bahnschrift,Segoe UI"),
        new("segoe-ui", "Segoe UI"),
        new("consolas", "Consolas,Cascadia Mono,monospace"),
        new("system", "Segoe UI"),
    };

    private readonly record struct FontSizePreset(string Key, double SizePx);

    private static readonly FontSizePreset[] FontSizePresets =
    {
        new("1", 14), new("2", 15), new("3", 16), new("4", 17), new("5", 18), new("6", 20), new("7", 22),
    };

    private readonly record struct UiScalePreset(string Key, double Scale);

    private static readonly UiScalePreset[] UiScalePresets =
    {
        new("xxs", 0.78), new("xs", 0.86), new("sm", 0.93),
        new("md", 1.0), new("lg", 1.08), new("xl", 1.16), new("xxl", 1.25),
    };

    // ===== picker options (web .quick-picker-option: swatch + key label) =====
    // Swatch = colored square for background/accent rows; for font/size/scale the
    // "swatch" is just a letter/label (transparent square), exactly like the CSS
    // `option.swatch || option.accent || "transparent"` fallback chain.

    public readonly record struct PresetOption(string Key, string SwatchLabel, Color? Swatch, Color? SwatchText);

    private static readonly string[] FontLabels = { "P", "M", "S", "B", "U", "C", "U" }; // web FONT_PRESETS labels
    private static readonly string[] ScaleLabels = { "XXS", "XS", "SM", "MD", "LG", "XL", "XXL" };

    public static IReadOnlyList<PresetOption> BackgroundOptions { get; } =
        BackgroundPresets.Select(p => new PresetOption(
            p.Key, "", Color.Parse(p.Surface), Color.Parse(p.Text))).ToArray();

    public static IReadOnlyList<PresetOption> AccentOptions { get; } =
        AccentPresets.Select(p => new PresetOption(
            p.Key, "", Color.Parse(p.Accent), Color.Parse(p.AccentHl))).ToArray();

    public static IReadOnlyList<PresetOption> FontOptions { get; } =
        FontPresets.Select((p, i) => new PresetOption(p.Key, FontLabels[i], null, null)).ToArray();

    public static IReadOnlyList<PresetOption> FontSizeOptions { get; } =
        FontSizePresets.Select(p => new PresetOption(p.Key, p.Key, null, null)).ToArray();

    public static IReadOnlyList<PresetOption> UiScaleOptions { get; } =
        UiScalePresets.Select((p, i) => new PresetOption(p.Key, ScaleLabels[i], null, null)).ToArray();

    /// <summary>Current font preset's letter (for the header font button).</summary>
    public static string CurrentFontLabel { get; private set; } = "C";
    /// <summary>Current size preset's digit (for the header size button).</summary>
    public static string CurrentSizeLabel { get; private set; } = "5";
    /// <summary>Current scale preset's label (for the header scale button).</summary>
    public static string CurrentScaleLabel { get; private set; } = "MD";

    // Web defaults (App.jsx useState initializers): black background, WHITE accent,
    // consolas font, font-size index 4 (18px), MD scale.
    private const string DefaultBackgroundKey = "black";
    private const string DefaultAccentKey = "white";
    private const string DefaultFontKey = "consolas";
    private const string DefaultFontSizeKey = "5";
    private const string DefaultUiScaleKey = "md";

    // ===== mutable palette state =====

    /// <summary>Bumped on every <see cref="Apply"/> so controls can re-cache pens/brushes.</summary>
    public static int Version { get; private set; }

    private static Color _bg, _surface, _panel, _line, _text, _muted, _accent, _accentHl;
    private static Color _textStrong, _textOnAccent, _lineStrong;
    private static IBrush _bgBrush = null!, _surfaceBrush = null!, _panelBrush = null!,
        _lineBrush = null!, _lineStrongBrush = null!, _textBrush = null!, _textStrongBrush = null!,
        _textOnAccentBrush = null!, _mutedBrush = null!, _accentBrush = null!, _accentHlBrush = null!;
    private static FontFamily _fontFamily = null!;
    private static Typeface _faceRegular, _faceSemiBold, _faceBold, _faceHeavy;
    private static double _fontSize;
    private static double _uiScale = 1.0;

    static Theme() => Apply(null, null, null, null, null);

    /// <summary>
    /// Rebuild the whole palette from the user's persisted preset keys. Unknown/missing
    /// keys fall back to the web defaults (black background, white accent, consolas, 18px, MD).
    /// Call BEFORE the first render.
    /// </summary>
    public static void Apply(string? backgroundKey, string? accentKey, string? fontKey,
        string? fontSizeKey, string? uiScaleKey)
    {
        var bp = Find(BackgroundPresets, p => p.Key, backgroundKey, DefaultBackgroundKey);
        var ap = Find(AccentPresets, p => p.Key, accentKey, DefaultAccentKey);
        var fp = Find(FontPresets, p => p.Key, fontKey, DefaultFontKey);
        var sp = Find(FontSizePresets, p => p.Key, fontSizeKey, DefaultFontSizeKey);
        var up = Find(UiScalePresets, p => p.Key, uiScaleKey, DefaultUiScaleKey);

        _bg = Color.Parse(bp.Bg);
        _surface = Color.Parse(bp.Surface);
        _panel = Color.Parse(bp.Panel);
        _line = Color.Parse(bp.Border);
        _text = Color.Parse(bp.Text);
        _muted = Color.Parse(bp.Muted);
        _accent = Color.Parse(ap.Accent);
        _accentHl = Color.Parse(ap.AccentHl);

        // derived tokens (mirror the CSS color-mix derivations)
        _textStrong = Mix(_text, Colors.White, 0.92);
        _textOnAccent = Mix(_bg, Colors.Black, 0.82);
        _lineStrong = Mix(_line, Colors.White, 0.86);

        _bgBrush = new SolidColorBrush(_bg);
        _surfaceBrush = new SolidColorBrush(_surface);
        _panelBrush = new SolidColorBrush(_panel);
        _lineBrush = new SolidColorBrush(_line);
        _lineStrongBrush = new SolidColorBrush(_lineStrong);
        _textBrush = new SolidColorBrush(_text);
        _textStrongBrush = new SolidColorBrush(_textStrong);
        _textOnAccentBrush = new SolidColorBrush(_textOnAccent);
        _mutedBrush = new SolidColorBrush(_muted);
        _accentBrush = new SolidColorBrush(_accent);
        _accentHlBrush = new SolidColorBrush(_accentHl);

        _fontFamily = new FontFamily(fp.Family);
        _faceRegular = new Typeface(_fontFamily);
        _faceSemiBold = new Typeface(_fontFamily, FontStyle.Normal, FontWeight.SemiBold);
        _faceBold = new Typeface(_fontFamily, FontStyle.Normal, FontWeight.Bold);
        _faceHeavy = new Typeface(_fontFamily, FontStyle.Normal, FontWeight.ExtraBold);
        _fontSize = sp.SizePx;

        CurrentFontLabel = FontLabels[Math.Max(0, Array.IndexOf(FontPresets, fp))];
        CurrentSizeLabel = sp.Key;
        CurrentScaleLabel = ScaleLabels[Math.Max(0, Array.IndexOf(UiScalePresets, up))];
        _uiScale = up.Scale;

        Version++;
    }

    private static T Find<T>(T[] presets, Func<T, string> keyOf, string? key, string fallbackKey)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            foreach (var p in presets)
                if (string.Equals(keyOf(p), key, StringComparison.OrdinalIgnoreCase))
                    return p;
        }
        foreach (var p in presets)
            if (keyOf(p) == fallbackKey)
                return p;
        return presets[0];
    }

    // ===== colors =====
    public static Color Bg => _bg;
    public static Color Surface => _surface;
    public static Color Panel => _panel;
    public static Color Line => _line;
    public static Color Text => _text;
    public static Color Muted => _muted;
    public static Color Accent => _accent;
    public static Color AccentHl => _accentHl;
    public static readonly Color Phase = Color.Parse("#8B5CF6");
    public static readonly Color Danger = Color.Parse("#EF4444");

    // ===== derived tokens =====
    public static Color TextStrong => _textStrong;
    public static Color TextOnAccent => _textOnAccent;
    public static Color LineStrong => _lineStrong;

    // ===== brushes =====
    public static IBrush BgBrush => _bgBrush;
    public static IBrush SurfaceBrush => _surfaceBrush;
    public static IBrush PanelBrush => _panelBrush;
    public static IBrush LineBrush => _lineBrush;
    public static IBrush LineStrongBrush => _lineStrongBrush;
    public static IBrush TextBrush => _textBrush;
    public static IBrush TextStrongBrush => _textStrongBrush;
    public static IBrush TextOnAccentBrush => _textOnAccentBrush;
    public static IBrush MutedBrush => _mutedBrush;
    public static IBrush AccentBrush => _accentBrush;
    public static IBrush AccentHlBrush => _accentHlBrush;

    // ===== typography =====
    public static FontFamily FontFamily => _fontFamily;
    public static Typeface FaceRegular => _faceRegular;
    public static Typeface FaceSemiBold => _faceSemiBold;
    public static Typeface FaceBold => _faceBold;
    public static Typeface FaceHeavy => _faceHeavy;

    /// <summary>Base font size in px (the CSS --font-size).</summary>
    public static double FontSize => _fontSize;
    public static double Fs2xs => _fontSize * 0.62;
    public static double FsXs => _fontSize * 0.72;
    public static double FsSm => _fontSize * 0.82;
    public static double FsMd => _fontSize * 0.92;
    public static double FsLg => _fontSize * 1.08;
    public static double FsXl => _fontSize * 1.22;

    /// <summary>Whole-UI zoom factor (the CSS uiScale preset).</summary>
    public static double UiScale => _uiScale;

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

    /// <summary>Lit accent key face (active tiles: accent-hl 72% + white → accent 86% + black).</summary>
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

    /// <summary>MASTER badge face (CSS .detail-master-badge: accent-hl 84% + white → accent 92% + black).</summary>
    public static IBrush BadgeFace() =>
        new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Mix(AccentHl, Colors.White, 0.84), 0),
                new GradientStop(Mix(Accent, Colors.Black, 0.92), 1),
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
