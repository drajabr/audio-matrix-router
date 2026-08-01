using System.Globalization;
using Avalonia;
using AppTheme = AudioMatrixRouter.App.Theme;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AudioMatrixRouter.App.Controls;

/// <summary>
/// The whole matrix as ONE custom-drawn control: pinned row/column headers (device cards,
/// glass meters, detached channel chips, MASTER edge bars) and the tile grid, per
/// docs/DESIGN-REFERENCE.md §3.3–§3.5. The top-left LabelSquare×LabelSquare corner is left
/// empty — the shell overlays the real corner controls there.
/// </summary>
public sealed class MatrixControl : Control
{
    private const double PanThreshold = 12;
    private const double WheelScrollStep = 48;

    // ===== theme-derived palette, re-cached whenever AppTheme.Apply bumps Version =====
    private static int s_palVersion = -1;

    private static Typeface FaceRegular;
    private static Typeface FaceBold;
    private static Typeface FaceHeavy;

    private static IBrush CardFill = null!;
    private static IBrush ChipTextBrush = null!;
    private static IBrush EdgeLightBrush = null!;
    private static IBrush EdgeLightOnBrush = null!;
    private static IBrush EdgeShadeBrush = null!;
    private static IBrush TextShadowBrush = null!;
    private static IBrush ReadoutBrush = null!;

    private static IPen LinePen = null!;
    private static IPen LineStrongPen = null!;
    private static IPen OffTilePen = null!;
    private static IPen OnTilePen = null!;
    private static IPen HoverTilePen = null!;
    private static IPen PhaseTilePen = null!;
    private static IPen BlockedPen = null!;
    private static IPen[] GlowPens = null!;        // concentric accent glow rings, inner → outer
    private static double[] GlowInflates = null!;
    private static IPen PhaseGlowPen = null!;
    private static IPen PhaseStripePen = null!;
    private static IPen HatchPenA = null!;
    private static IPen MasterRingPen = null!;
    private static IPen MasterRingInsetPen = null!;
    private static IPen MasterGlowPen = null!;
    private static IPen BadgeGlowPen = null!;
    private static IPen DragTargetPen = null!;

    private static IBrush KeyFaceBrush = null!;
    private static IBrush AccentFaceBrush = null!;
    private static IBrush BadgeFaceBrush = null!;
    private static IBrush MeterFillH = null!;
    private static IBrush MeterFillV = null!;
    private static IBrush BadgeLightBrush = null!;
    private static IBrush BadgeShadeBrush = null!;

    private static void EnsurePalette()
    {
        if (s_palVersion == AppTheme.Version) return;
        s_palVersion = AppTheme.Version;

        FaceRegular = AppTheme.FaceRegular;
        FaceBold = AppTheme.FaceBold;
        FaceHeavy = AppTheme.FaceHeavy;

        CardFill = new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Panel, 0.92));
        ChipTextBrush = new SolidColorBrush(AppTheme.Mix(AppTheme.AccentHl, AppTheme.Text, 0.76));
        EdgeLightBrush = new SolidColorBrush(AppTheme.WithAlpha(Colors.White, 0.10));
        EdgeLightOnBrush = new SolidColorBrush(AppTheme.WithAlpha(Colors.White, 0.18));
        EdgeShadeBrush = new SolidColorBrush(AppTheme.WithAlpha(Colors.Black, 0.28));
        TextShadowBrush = new SolidColorBrush(AppTheme.WithAlpha(Colors.Black, 0.35));
        // CSS .tile-gain-readout: color-mix(text-on-accent 86%, white 14%)
        ReadoutBrush = new SolidColorBrush(AppTheme.Mix(AppTheme.TextOnAccent, Colors.White, 0.86));

        LinePen = new Pen(AppTheme.LineBrush);
        LineStrongPen = new Pen(AppTheme.LineStrongBrush);
        OffTilePen = new Pen(AppTheme.LineStrongBrush);
        OnTilePen = new Pen(new SolidColorBrush(AppTheme.Mix(AppTheme.Accent, Colors.White, 0.84)), 1);
        HoverTilePen = new Pen(new SolidColorBrush(AppTheme.Mix(AppTheme.AccentHl, AppTheme.Line, 0.58)), 1.25);
        PhaseTilePen = new Pen(new SolidColorBrush(AppTheme.Mix(AppTheme.Phase, Colors.White, 0.72)), 1);
        BlockedPen = new Pen(AppTheme.LineStrongBrush, 1, new DashStyle(new double[] { 3, 3 }, 0));

        // soft accent glow: CSS `0 0 10px accent 45%` + `0 12px 20px accent 14%` approximated
        // with concentric strokes of decreasing alpha (a blurred-looking falloff)
        // Toned down (was 2x this): strokes read stronger than the CSS blur they mimic.
        GlowPens = new IPen[]
        {
            new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.20)), 2),
            new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.11)), 3),
            new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.055)), 4),
            new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.025)), 5),
        };
        GlowInflates = new[] { 1.0, 3.0, 5.5, 8.5 };

        PhaseGlowPen = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Phase, 0.30)), 2.5);
        PhaseStripePen = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Phase, 0.18)), 4);
        HatchPenA = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Mix(AppTheme.Surface, Colors.Black, 0.55), 0.85)), 4);
        MasterRingPen = new Pen(new SolidColorBrush(AppTheme.Mix(AppTheme.Accent, Colors.White, 0.78)), 1);
        MasterRingInsetPen = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.45)), 1);
        // Subtle: with light accents (white preset) fat rings read as weird halos around
        // the cards — the CSS master treatment is a bright border + faint ring, not a glow.
        MasterGlowPen = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.10)), 2.5);
        BadgeGlowPen = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.16)), 1.5);
        DragTargetPen = new Pen(new SolidColorBrush(AppTheme.Mix(AppTheme.AccentHl, AppTheme.Line, 0.70)), 1.5);

        KeyFaceBrush = AppTheme.KeyFace();
        AccentFaceBrush = AppTheme.AccentFace();
        BadgeFaceBrush = AppTheme.BadgeFace();
        MeterFillH = AppTheme.MeterFill(horizontal: true);
        MeterFillV = AppTheme.MeterFill(horizontal: false);
        BadgeLightBrush = new SolidColorBrush(AppTheme.WithAlpha(Colors.White, 0.34));
        BadgeShadeBrush = new SolidColorBrush(AppTheme.WithAlpha(Colors.Black, 0.24));
    }

    private MatrixModel? _model;
    private MatrixLayout? _layout;
    private double _labelSquare = 224;

    // smooth scrolling: wheel moves the target, an RAF loop eases current toward it
    private double _scrollX;
    private double _scrollY;
    private double _targetScrollX;
    private double _targetScrollY;
    private bool _scrollAnimating;

    // meter interpolation: model Peaks are targets, these display values ease toward them
    private readonly Dictionary<string, float[]> _meterDisplay = new();
    private bool _meterAnimating;

    private string? _hoverRowKey;
    private string? _hoverColKey;
    private double _bottomInset;

    // per-cell toggle animation: key -> eased 0..1 progress toward the on state
    private readonly Dictionary<string, double> _tileAnim = new();
    private bool _tileAnimating;

    // label-square resize drag (web grid-resize-handles: both axes write ONE value)
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeAll);
    private const double ResizeGrip = 5;
    private bool _resizingSquare;
    private bool _resizeAxisX;
    private Point _resizeStart;
    private double _resizeStartSquare;

    // pan-scroll drag state (tile region)
    private bool _panActive;
    private bool _panning;
    private bool _suppressClick;
    private Point _pressPos;
    private double _panStartScrollX;
    private double _panStartScrollY;

    // header reorder drag state
    private (bool IsInput, string DeviceId)? _headerDrag;
    private string? _dragTargetId;

    private readonly HashSet<string> _activeInputs = new();
    private readonly HashSet<string> _activeOutputs = new();

    public MatrixModel? Model
    {
        get => _model;
        set
        {
            _model = value;
            _layout = value is null ? null : new MatrixLayout(value, _labelSquare);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>Extra scrollable room below the content, so rows can be scrolled
    /// clear of the dock that floats over the bottom of the matrix.</summary>
    public double BottomInset
    {
        get => _bottomInset;
        set
        {
            if (Math.Abs(_bottomInset - value) < 0.5) return;
            _bottomInset = value;
            InvalidateVisual();
        }
    }

    /// <summary>Label column width == header row height (the corner stays square).</summary>
    public double LabelSquare
    {
        get => _labelSquare;
        set
        {
            _labelSquare = value;
            _layout = _model is null ? null : new MatrixLayout(_model, _labelSquare);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>Raised live while dragging the label-square resize handles (the value
    /// is the shared column-width == header-height, clamped to the theme limits).</summary>
    public event EventHandler<double>? LabelSquareChanged;

    public event EventHandler<MatrixCellEvent>? CellToggled;
    public event EventHandler<MatrixCellGainEvent>? CellGainDelta;
    public event EventHandler<MatrixCellEvent>? CellPhaseToggled;
    public event EventHandler<MatrixCellEvent>? CellGainReset;
    public event EventHandler<MatrixHeaderEvent>? MasterRequested;
    public event EventHandler<MatrixReorderEvent>? ReorderRequested;
    public event EventHandler<MatrixSelectionEvent>? SelectionChanged;

    /// <summary>Peaks arrays (the targets) were mutated in place by the shell — ease toward them.</summary>
    public void RefreshPeaks() => StartMeterAnimation();

    // ===================================================================== layout / measure

    protected override Size MeasureOverride(Size availableSize)
    {
        var l = EnsureLayout();
        if (l is null) return default;
        var w = l.ContentWidth;
        var h = l.ContentHeight;
        if (!double.IsInfinity(availableSize.Width)) w = Math.Min(w, availableSize.Width);
        if (!double.IsInfinity(availableSize.Height)) h = Math.Min(h, availableSize.Height);
        return new Size(w, h);
    }

    private MatrixLayout? EnsureLayout()
    {
        if (_model is null) return _layout = null;
        // rebuild each time it is asked for around a render/measure pass: the shell mutates
        // the model in place (view toggle, device lists), and this is cheap at matrix scale.
        return _layout = new MatrixLayout(_model, _labelSquare);
    }

    private void ClampScroll(MatrixLayout l)
    {
        // BottomInset reserves scroll room for the floating dock: at full scroll the
        // last row clears it, as if a line above the dock items bounded the matrix.
        var maxX = Math.Max(0, l.ContentWidth - Bounds.Width);
        var maxY = Math.Max(0, l.ContentHeight + _bottomInset - Bounds.Height);
        _scrollX = Math.Clamp(_scrollX, 0, maxX);
        _scrollY = Math.Clamp(_scrollY, 0, maxY);
        _targetScrollX = Math.Clamp(_targetScrollX, 0, maxX);
        _targetScrollY = Math.Clamp(_targetScrollY, 0, maxY);
    }

    // ===================================================================== animation loops

    private void StartScrollAnimation()
    {
        if (_scrollAnimating) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            _scrollX = _targetScrollX;
            _scrollY = _targetScrollY;
            InvalidateVisual();
            return;
        }
        _scrollAnimating = true;
        top.RequestAnimationFrame(ScrollTick);
    }

    private void ScrollTick(TimeSpan _)
    {
        _scrollAnimating = false;
        var dx = _targetScrollX - _scrollX;
        var dy = _targetScrollY - _scrollY;
        if (Math.Abs(dx) <= 0.5 && Math.Abs(dy) <= 0.5)
        {
            _scrollX = _targetScrollX;
            _scrollY = _targetScrollY;
            InvalidateVisual();
            return;
        }
        // the web's easing: offset += (target - offset) * 0.22 per frame
        _scrollX += dx * 0.22;
        _scrollY += dy * 0.22;
        InvalidateVisual();
        var top = TopLevel.GetTopLevel(this);
        if (top is not null)
        {
            _scrollAnimating = true;
            top.RequestAnimationFrame(ScrollTick);
        }
        else
        {
            _scrollX = _targetScrollX;
            _scrollY = _targetScrollY;
        }
    }

    private float[] DisplayPeaks(MatrixDeviceInfo d)
    {
        if (!_meterDisplay.TryGetValue(d.Id, out var arr) || arr.Length != d.Peaks.Length)
        {
            arr = (float[])d.Peaks.Clone();
            _meterDisplay[d.Id] = arr;
        }
        return arr;
    }

    private void StartMeterAnimation()
    {
        if (_meterAnimating) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            SnapMeters();
            InvalidateVisual();
            return;
        }
        _meterAnimating = true;
        top.RequestAnimationFrame(MeterTick);
    }

    private void SnapMeters()
    {
        var m = _model;
        if (m is null) return;
        foreach (var d in m.Inputs) Array.Copy(d.Peaks, DisplayPeaks(d), d.Peaks.Length);
        foreach (var d in m.Outputs) Array.Copy(d.Peaks, DisplayPeaks(d), d.Peaks.Length);
    }

    private void MeterTick(TimeSpan _)
    {
        _meterAnimating = false;
        var m = _model;
        if (m is null) return;

        var moving = false;
        moving |= EaseDevicePeaks(m.Inputs);
        moving |= EaseDevicePeaks(m.Outputs);
        InvalidateVisual();

        if (moving)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is not null)
            {
                _meterAnimating = true;
                top.RequestAnimationFrame(MeterTick);
            }
        }
    }

    private bool EaseDevicePeaks(List<MatrixDeviceInfo> devices)
    {
        var moving = false;
        foreach (var d in devices)
        {
            var cur = DisplayPeaks(d);
            for (var i = 0; i < cur.Length; i++)
            {
                var delta = d.Peaks[i] - cur[i];
                if (Math.Abs(delta) <= 0.004f)
                {
                    cur[i] = d.Peaks[i];
                }
                else
                {
                    // 70ms-style ease: cur += (target - cur) * 0.35 per frame
                    cur[i] += delta * 0.35f;
                    moving = true;
                }
            }
        }
        return moving;
    }

    // ===================================================================== rendering

    /// <summary>Snap a rect to whole pixels: round positions AND end edges so adjacent
    /// rects never leave 1px seams (size = round(end) - round(start)).</summary>
    private static Rect Snap(Rect r)
    {
        var x = Math.Round(r.X);
        var y = Math.Round(r.Y);
        return new Rect(x, y, Math.Round(r.Right) - x, Math.Round(r.Bottom) - y);
    }

    public override void Render(DrawingContext context)
    {
        EnsurePalette();
        var size = Bounds.Size;
        // transparent fill so the whole control is pointer-hit-testable
        context.DrawRectangle(Brushes.Transparent, null, new Rect(size));

        var m = _model;
        if (m is null) return;
        var l = EnsureLayout()!;
        ClampScroll(l);
        ComputeActiveDevices(m);

        // The clip guards the header strips on the left/top only; on the right/bottom
        // it extends past the control so the ON-tile glow (up to ~9px of soft rings)
        // fades out naturally into the surrounding margin instead of being cut flat
        // at the viewport edge.
        const double GlowBleed = 10;
        var tileClip = new Rect(
            l.TilesOriginX, l.TilesOriginY,
            Math.Max(0, size.Width - l.TilesOriginX + GlowBleed),
            Math.Max(0, size.Height - l.TilesOriginY + GlowBleed));
        if (tileClip.Width > 0 && tileClip.Height > 0)
        {
            using (context.PushClip(tileClip))
            {
                DrawTiles(context, l, m, tileClip);
            }
        }

        var colClip = new Rect(l.TilesOriginX, 0, Math.Max(0, size.Width - l.TilesOriginX), _labelSquare);
        if (colClip.Width > 0)
        {
            using (context.PushClip(colClip))
            {
                foreach (var e in l.Cols)
                    DrawColHeader(context, l, e, size.Width);
            }
        }

        var rowClip = new Rect(0, l.TilesOriginY, _labelSquare, Math.Max(0, size.Height - l.TilesOriginY));
        if (rowClip.Height > 0)
        {
            using (context.PushClip(rowClip))
            {
                foreach (var e in l.Rows)
                    DrawRowHeader(context, l, e, size.Height);
            }
        }
        // the corner (0,0,LabelSquare,LabelSquare) is intentionally left empty for the shell overlay
    }

    private void ComputeActiveDevices(MatrixModel m)
    {
        _activeInputs.Clear();
        _activeOutputs.Clear();
        foreach (var kv in m.Cells)
        {
            if (!kv.Value.On) continue;
            var sep = kv.Key.IndexOf('|');
            if (sep <= 0) continue;
            _activeInputs.Add(MatrixLayout.DeviceIdOfKey(kv.Key[..sep]));
            _activeOutputs.Add(MatrixLayout.DeviceIdOfKey(kv.Key[(sep + 1)..]));
        }
    }

    private static bool IsBlocked(MatrixTrack row, MatrixTrack col) =>
        row.Entry.Device.IsLoopback && row.Entry.Device.Id == "loop:" + col.Entry.Device.Id;

    // --------------------------------------------------------------------- tiles

    private void DrawTiles(DrawingContext ctx, MatrixLayout l, MatrixModel m, Rect clip)
    {
        var hasSel = _hoverRowKey is not null && _hoverColKey is not null;
        MatrixTrack? selRow = null, selCol = null;
        if (hasSel)
        {
            l.RowTrackByKey.TryGetValue(_hoverRowKey!, out selRow);
            l.ColTrackByKey.TryGetValue(_hoverColKey!, out selCol);
            hasSel = selRow is not null && selCol is not null;
        }

        foreach (var rt in l.RowTracks)
        {
            var y = l.TilesOriginY + MatrixLayout.UnitPos(rt.StartUnit) - _scrollY;
            var h = MatrixLayout.SpanSize(rt.UnitSpan);
            if (y + h < clip.Y || y > clip.Bottom) continue;

            foreach (var ct in l.ColTracks)
            {
                var x = l.TilesOriginX + MatrixLayout.UnitPos(ct.StartUnit) - _scrollX;
                var w = MatrixLayout.SpanSize(ct.UnitSpan);
                if (x + w < clip.X || x > clip.Right) continue;

                m.Cells.TryGetValue(rt.Key + "|" + ct.Key, out var cell);
                DrawTile(ctx, Snap(new Rect(x, y, w, h)), rt, ct, cell, hasSel, selRow, selCol);
            }
        }
    }

    private void DrawTile(DrawingContext ctx, Rect rect, MatrixTrack rt, MatrixTrack ct, MatrixCell? cell,
        bool hasSel, MatrixTrack? selRow, MatrixTrack? selCol)
    {
        var blocked = IsBlocked(rt, ct);
        var on = !blocked && cell?.On == true;
        var phase = !blocked && cell?.PhaseInverted == true;
        var isHover = hasSel && rt.Key == selRow!.Key && ct.Key == selCol!.Key;
        var onPath = hasSel && !isHover &&
                     ((rt.Key == selRow!.Key && ct.StartUnit < selCol!.StartUnit) ||
                      (ct.Key == selCol!.Key && rt.StartUnit < selRow!.StartUnit));

        // Toggle animation: t eases 0→1 as the cell turns on, so the accent face,
        // its glow and the readout fade in instead of snapping.
        var t = blocked ? 0 : TileProgress(rt.Key + "|" + ct.Key, on);

        // CSS: base off 0.62, on 1, blocked 0.5; with a selection, path cells 0.9 and
        // everything else dims hard (.28 off / .38 on) — interpolated across t
        var offOpacity = blocked ? 0.5 : 0.62;
        var onOpacity = 1.0;
        if (hasSel && !isHover)
        {
            offOpacity = onPath ? 0.9 : 0.28;
            onOpacity = onPath ? 0.9 : 0.38;
        }
        var opacity = offOpacity + (onOpacity - offOpacity) * t;

        var basePen = blocked ? BlockedPen
            : isHover ? HoverTilePen
            : phase ? PhaseTilePen
            : OffTilePen;
        var litPen = isHover ? HoverTilePen : phase ? PhaseTilePen : OnTilePen;

        var rr = new RoundedRect(rect, AppTheme.RadiusTile);

        using (ctx.PushOpacity(opacity))
        {
            if (t > 0.01)
            {
                // soft outer glow: concentric rounded strokes with decreasing alpha
                using (ctx.PushOpacity(t))
                {
                    for (var g = GlowPens.Length - 1; g >= 0; g--)
                    {
                        var inf = GlowInflates[g];
                        ctx.DrawRectangle(null, GlowPens[g], new RoundedRect(rect.Inflate(inf), AppTheme.RadiusTile + inf));
                    }
                }
            }
            if (phase)
                ctx.DrawRectangle(null, PhaseGlowPen, new RoundedRect(rect.Inflate(1.5), AppTheme.RadiusTile + 1.5));

            ctx.DrawRectangle(KeyFaceBrush, basePen, rr);
            if (t > 0.01)
            {
                using (ctx.PushOpacity(t))
                {
                    ctx.DrawRectangle(AccentFaceBrush, litPen, rr);
                }
            }

            // --fx-edge: 1px light on top, 1px shade on the bottom (inset, corner-clear)
            if (rect.Width > 16 && rect.Height > 12)
            {
                var edge = new Rect(rect.X + AppTheme.RadiusTile, rect.Y + 1, rect.Width - 2 * AppTheme.RadiusTile, 1);
                ctx.DrawRectangle(EdgeLightBrush, null, edge);
                if (t > 0.01)
                {
                    using (ctx.PushOpacity(t)) { ctx.DrawRectangle(EdgeLightOnBrush, null, edge); }
                }
                // Overlap the border stroke (0.25px past its inner edge): meeting it
                // exactly still left an anti-aliased hairline between shade and border.
                ctx.DrawRectangle(EdgeShadeBrush, null,
                    new Rect(rect.X + AppTheme.RadiusTile, rect.Bottom - 1.75, rect.Width - 2 * AppTheme.RadiusTile, 1.5));
            }

            if (blocked)
                DrawDiagonalStripes(ctx, rr, HatchPenA, 8);
            else if (phase)
                DrawDiagonalStripes(ctx, rr, PhaseStripePen, 9);

            if (t > 0.01 && cell is not null && Math.Abs(cell.GainDb) >= 0.5)
            {
                // small bold centered readout, dark-on-accent, e.g. "+3.5dB"
                var text = FormatGain(cell.GainDb);
                var ft = Ft(text, FaceBold, AppTheme.Fs2xs, ReadoutBrush);
                var origin = new Point(rect.Center.X - ft.Width / 2, rect.Center.Y - ft.Height / 2);
                var shadow = Ft(text, FaceBold, AppTheme.Fs2xs, TextShadowBrush);
                using (ctx.PushOpacity(t))
                {
                    ctx.DrawText(shadow, origin + new Vector(0, 1));
                    ctx.DrawText(ft, origin);
                }
            }
        }
    }

    /// <summary>Current 0..1 toggle progress for a cell, starting the ease loop when
    /// it has not reached its target yet. First sight of a cell snaps (no intro fade).</summary>
    private double TileProgress(string key, bool on)
    {
        var target = on ? 1.0 : 0.0;
        if (!_tileAnim.TryGetValue(key, out var cur))
        {
            _tileAnim[key] = target;
            return target;
        }
        if (Math.Abs(cur - target) > 0.005) StartTileAnimation();
        return cur;
    }

    private void StartTileAnimation()
    {
        if (_tileAnimating) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        _tileAnimating = true;
        top.RequestAnimationFrame(TileTick);
    }

    private void TileTick(TimeSpan _)
    {
        _tileAnimating = false;
        var m = _model;
        if (m is null) return;

        var moving = false;
        foreach (var key in _tileAnim.Keys.ToList())
        {
            var target = m.Cells.TryGetValue(key, out var c) && c.On ? 1.0 : 0.0;
            var cur = _tileAnim[key];
            var delta = target - cur;
            if (Math.Abs(delta) <= 0.01)
            {
                if (target <= 0 && !m.Cells.ContainsKey(key)) _tileAnim.Remove(key);
                else _tileAnim[key] = target;
                continue;
            }
            _tileAnim[key] = cur + delta * 0.3; // ~120ms to settle at 60fps
            moving = true;
        }

        InvalidateVisual();
        if (moving) StartTileAnimation();
    }

    private static void DrawDiagonalStripes(DrawingContext ctx, RoundedRect rr, IPen pen, double step)
    {
        var rect = rr.Rect;
        using (ctx.PushClip(rr))
        {
            // 135° stripes: lines running from bottom-left toward top-right
            for (var t = -rect.Height; t < rect.Width; t += step)
            {
                ctx.DrawLine(pen,
                    new Point(rect.X + t, rect.Bottom),
                    new Point(rect.X + t + rect.Height, rect.Y));
            }
        }
    }

    // Always one decimal ("+1.0dB", not "+1dB") so the readout width doesn't jump
    // while wheeling through 0.5dB steps.
    private static string FormatGain(double gainDb) =>
        (gainDb >= 0 ? "+" : "") + gainDb.ToString("0.0", CultureInfo.InvariantCulture) + "dB";

    // --------------------------------------------------------------------- column headers

    private void DrawColHeader(DrawingContext ctx, MatrixLayout l, MatrixAxisEntry e, double viewWidth)
    {
        var card = Snap(l.ColCardRect(e, _scrollX));
        if (card.Right < l.TilesOriginX || card.X > viewWidth) return;

        var master = e.Device.IsMaster;
        var active = master || _activeOutputs.Contains(e.Device.Id);
        var isDragSource = _headerDrag is { IsInput: false } ds && ds.DeviceId == e.Device.Id;
        var isDragTarget = _headerDrag is { IsInput: false } dt && dt.DeviceId != e.Device.Id && _dragTargetId == e.Device.Id;

        var opacity = active ? 1.0 : 0.62;
        if (isDragSource) opacity = Math.Min(opacity, 0.7);

        using (ctx.PushOpacity(opacity))
        {
            // Master = ONE bright border (plus the badge). The old outer glow ring +
            // inner inset ring read as tram-lines/halos with light accent presets.
            var borderPen = isDragTarget ? DragTargetPen : master ? MasterRingPen : LinePen;
            var cardRR = new RoundedRect(card, AppTheme.RadiusPanel);
            ctx.DrawRectangle(CardFill, borderPen, cardRR);

            using (ctx.PushClip(cardRR))
            {
                // meters: one vertical glass bar per channel, bottom-aligned; inset 24px at the
                // bottom when the MASTER badge occupies that edge
                var n = Math.Max(1, e.Device.Channels);
                var peaks = DisplayPeaks(e.Device);
                var meterBottom = card.Bottom - (master ? AppTheme.BadgeInset : AppTheme.MeterPad);
                var meterTop = card.Y + AppTheme.MeterPad;
                var laneW = (card.Width - 2 * AppTheme.MeterPad - AppTheme.MeterPad * (n - 1)) / n;
                for (var c = 0; c < n && laneW > 1; c++)
                {
                    var level = PeakOf(peaks, c);
                    if (level <= 0) continue;
                    var bh = Math.Max(0, (meterBottom - meterTop) * level);
                    var bx = card.X + AppTheme.MeterPad + c * (laneW + AppTheme.MeterPad);
                    ctx.DrawRectangle(MeterFillV, null,
                        new RoundedRect(new Rect(bx, meterBottom - bh, laneW, bh), AppTheme.RadiusMicro));
                }

                // rotated name + sub-label (reads bottom-up), anchored bottom-left, over the meters
                var textBottom = card.Bottom - (master ? AppTheme.BadgeInset : 0) - 8;
                var maxLen = Math.Max(8, textBottom - card.Y - 8);
                var name = Ft(e.Device.Label, FaceBold, AppTheme.FsSm, AppTheme.TextStrongBrush, maxLen);
                var tx = card.X + 8;
                using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(tx, textBottom)))
                {
                    ctx.DrawText(name, default);
                }
                if (!string.IsNullOrEmpty(e.Device.SubLabel))
                {
                    var sub = Ft(e.Device.SubLabel, FaceRegular, AppTheme.Fs2xs, AppTheme.MutedBrush, maxLen);
                    using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2) *
                                             Matrix.CreateTranslation(tx + name.Height + 6, textBottom)))
                    {
                        ctx.DrawText(sub, default);
                    }
                }

                if (master)
                {
                    // MASTER edge bar on the BOTTOM edge: bottom corners rounded, top square
                    // Overdraw 1px past the card edges — the rounded clip trims it flush,
                    // killing the anti-aliased hairline gap against the card border.
                    var badge = new Rect(card.X - 1, card.Bottom - AppTheme.BadgeSize, card.Width + 2, AppTheme.BadgeSize + 1);
                    DrawMasterBadge(ctx, badge, vertical: false,
                        RoundRect(badge, 0, 0, AppTheme.RadiusPanel, AppTheme.RadiusPanel));
                }
            }

            // detached channel chips BELOW the card — one per channel unit
            for (var c = 0; c < Math.Max(1, e.Device.Channels); c++)
                DrawChip(ctx, Snap(l.ColChipRect(e, c, _scrollX)), ChipLabel(e.Device.Channels, c));
        }
    }

    // --------------------------------------------------------------------- row headers

    private void DrawRowHeader(DrawingContext ctx, MatrixLayout l, MatrixAxisEntry e, double viewHeight)
    {
        var card = Snap(l.RowCardRect(e, _scrollY));
        if (card.Bottom < l.TilesOriginY || card.Y > viewHeight) return;

        var master = e.Device.IsMaster;
        var active = master || _activeInputs.Contains(e.Device.Id);
        var isDragSource = _headerDrag is { IsInput: true } ds && ds.DeviceId == e.Device.Id;
        var isDragTarget = _headerDrag is { IsInput: true } dt && dt.DeviceId != e.Device.Id && _dragTargetId == e.Device.Id;

        var opacity = active ? 1.0 : 0.62;
        if (isDragSource) opacity = Math.Min(opacity, 0.7);

        using (ctx.PushOpacity(opacity))
        {
            // Single bright border for master (see col-header comment).
            var borderPen = isDragTarget ? DragTargetPen : master ? MasterRingPen : LinePen;
            var cardRR = new RoundedRect(card, AppTheme.RadiusPanel);
            ctx.DrawRectangle(CardFill, borderPen, cardRR);

            using (ctx.PushClip(cardRR))
            {
                // meters: one horizontal glass bar per channel, each filling its full tile lane
                // height (minus 4px pad/gaps); left inset 24px when the MASTER bar sits there
                var n = Math.Max(1, e.Device.Channels);
                var peaks = DisplayPeaks(e.Device);
                var meterLeft = card.X + (master ? AppTheme.BadgeInset : AppTheme.MeterPad);
                var meterRight = card.Right - AppTheme.MeterPad;
                var laneH = (card.Height - 2 * AppTheme.MeterPad - AppTheme.MeterPad * (n - 1)) / n;
                for (var c = 0; c < n && laneH > 1; c++)
                {
                    var level = PeakOf(peaks, c);
                    if (level <= 0) continue;
                    var bw = Math.Max(0, (meterRight - meterLeft) * level);
                    var by = card.Y + AppTheme.MeterPad + c * (laneH + AppTheme.MeterPad);
                    ctx.DrawRectangle(MeterFillH, null,
                        new RoundedRect(new Rect(meterLeft, by, bw, laneH), AppTheme.RadiusMicro));
                }

                // name + sub anchored TOP-LEFT, floating over the meters
                var textLeft = card.X + (master ? AppTheme.BadgeInset : 0) + 8;
                var maxW = Math.Max(8, card.Right - textLeft - 8);
                var name = Ft(e.Device.Label, FaceBold, AppTheme.FsSm, AppTheme.TextStrongBrush, maxW);
                ctx.DrawText(name, new Point(textLeft, card.Y + 8));
                if (!string.IsNullOrEmpty(e.Device.SubLabel))
                {
                    var sub = Ft(e.Device.SubLabel, FaceRegular, AppTheme.Fs2xs, AppTheme.MutedBrush, maxW);
                    ctx.DrawText(sub, new Point(textLeft, card.Y + 8 + name.Height + 6));
                }

                if (master)
                {
                    // MASTER edge bar on the LEFT edge: left corners rounded, right square
                    // Overdraw 1px past the card edges (see col badge comment).
                    var badge = new Rect(card.X - 1, card.Y - 1, AppTheme.BadgeSize + 1, card.Height + 2);
                    DrawMasterBadge(ctx, badge, vertical: true,
                        RoundRect(badge, AppTheme.RadiusPanel, 0, 0, AppTheme.RadiusPanel));
                }
            }

            // detached channel chips at the RIGHT of the card — one per channel unit
            for (var c = 0; c < Math.Max(1, e.Device.Channels); c++)
                DrawChip(ctx, Snap(l.RowChipRect(e, c, _scrollY)), ChipLabel(e.Device.Channels, c));
        }
    }

    // --------------------------------------------------------------------- shared header pieces

    private static void DrawMasterBadge(DrawingContext ctx, Rect badge, bool vertical, RoundedRect rr)
    {
        ctx.DrawRectangle(null, BadgeGlowPen, rr);
        ctx.DrawRectangle(BadgeFaceBrush, null, rr);
        // inner top light / bottom shade
        ctx.DrawRectangle(BadgeLightBrush, null, new Rect(badge.X + 3, badge.Y + 1, badge.Width - 6, 1));
        ctx.DrawRectangle(BadgeShadeBrush, null, new Rect(badge.X + 3, badge.Bottom - 2, badge.Width - 6, 1));

        var ft = Ft("MASTER", FaceHeavy, Math.Min(AppTheme.Fs2xs, 11), AppTheme.TextOnAccentBrush);
        if (vertical)
        {
            // rotated bottom-up, centered in the bar
            var tx = badge.Center.X - ft.Height / 2;
            var ty = badge.Center.Y + ft.Width / 2;
            using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(tx, ty)))
            {
                ctx.DrawText(ft, default);
            }
        }
        else
        {
            ctx.DrawText(ft, new Point(badge.Center.X - ft.Width / 2, badge.Center.Y - ft.Height / 2));
        }
    }

    private static void DrawChip(DrawingContext ctx, Rect rect, string label)
    {
        var rr = new RoundedRect(rect, AppTheme.RadiusMicro);
        ctx.DrawRectangle(KeyFaceBrush, LineStrongPen, rr);
        ctx.DrawRectangle(EdgeLightBrush, null, new Rect(rect.X + 3, rect.Y + 1, rect.Width - 6, 1));
        ctx.DrawRectangle(EdgeShadeBrush, null, new Rect(rect.X + 3, rect.Bottom - 2, rect.Width - 6, 1));
        var ft = Ft(label, FaceHeavy, AppTheme.Fs2xs, ChipTextBrush);
        ctx.DrawText(ft, new Point(rect.Center.X - ft.Width / 2, rect.Center.Y - ft.Height / 2));
    }

    private static string ChipLabel(int channels, int c) =>
        channels <= 1 ? "M" : channels == 2 ? (c == 0 ? "L" : "R") : (c + 1).ToString(CultureInfo.InvariantCulture);

    private static double PeakOf(float[] peaks, int channel) =>
        peaks.Length > channel ? Math.Clamp(peaks[channel], 0f, 1f) : 0;

    private static RoundedRect RoundRect(Rect r, double tl, double tr, double br, double bl) =>
        new(r, new Vector(tl, tl), new Vector(tr, tr), new Vector(br, br), new Vector(bl, bl));

    private static FormattedText Ft(string text, Typeface tf, double size, IBrush brush, double maxWidth = 0)
    {
        var ft = new FormattedText(text ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, brush);
        if (maxWidth > 0)
        {
            ft.MaxTextWidth = maxWidth;
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        return ft;
    }

    // ===================================================================== interaction

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var m = _model;
        var l = _layout;
        if (m is null || l is null) return;

        var pt = e.GetCurrentPoint(this);
        var p = pt.Position;
        var hit = l.HitTest(p, _scrollX, _scrollY);
        _pressPos = p;
        _suppressClick = false;

        // label-square resize grips: the seams at x == LabelSquare and y == LabelSquare
        // (both axes write the one shared value, web parity)
        if (pt.Properties.IsLeftButtonPressed)
        {
            var nearX = Math.Abs(p.X - _labelSquare) <= ResizeGrip;
            var nearY = Math.Abs(p.Y - _labelSquare) <= ResizeGrip;
            if (nearX || nearY)
            {
                _resizingSquare = true;
                _resizeAxisX = nearX;
                _resizeStart = p;
                _resizeStartSquare = _labelSquare;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        if (pt.Properties.IsMiddleButtonPressed)
        {
            if (hit.Kind == MatrixHitKind.Tile && !m.Locked &&
                m.Cells.TryGetValue(hit.RowKey + "|" + hit.ColKey, out var cell) && cell.On)
            {
                CellGainReset?.Invoke(this, new MatrixCellEvent(hit.RowKey!, hit.ColKey!));
                InvalidateVisual();
            }
            e.Handled = true;
            return;
        }

        if (pt.Properties.IsRightButtonPressed)
        {
            if (hit.Kind == MatrixHitKind.Tile && !m.Locked)
            {
                var row = l.RowTrackByKey.GetValueOrDefault(hit.RowKey!);
                var col = l.ColTrackByKey.GetValueOrDefault(hit.ColKey!);
                if (row is not null && col is not null && !IsBlocked(row, col))
                {
                    CellPhaseToggled?.Invoke(this, new MatrixCellEvent(hit.RowKey!, hit.ColKey!));
                    InvalidateVisual();
                }
            }
            e.Handled = true;
            return;
        }

        if (!pt.Properties.IsLeftButtonPressed) return;

        if (hit.Kind is MatrixHitKind.RowHeader or MatrixHitKind.ColHeader)
        {
            var isInput = hit.Kind == MatrixHitKind.RowHeader;
            if (e.ClickCount == 2)
            {
                MasterRequested?.Invoke(this, new MatrixHeaderEvent(isInput, hit.DeviceId!));
                e.Handled = true;
                return;
            }
            if (!m.Locked)
            {
                _headerDrag = (isInput, hit.DeviceId!);
                _dragTargetId = hit.DeviceId;
                e.Pointer.Capture(this);
                e.Handled = true;
            }
            return;
        }

        if (p.X >= l.TilesOriginX && p.Y >= l.TilesOriginY)
        {
            // tile region: potential toggle-on-release, or pan-scroll once movement exceeds 12px
            _panActive = true;
            _panning = false;
            _panStartScrollX = _scrollX;
            _panStartScrollY = _scrollY;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var m = _model;
        var l = _layout;
        if (m is null || l is null) return;

        var p = e.GetCurrentPoint(this).Position;

        if (_resizingSquare)
        {
            var d = _resizeAxisX ? p.X - _resizeStart.X : p.Y - _resizeStart.Y;
            var next = Math.Clamp(_resizeStartSquare + d, AppTheme.LabelSquareMin, AppTheme.LabelSquareMax);
            if (Math.Abs(next - _labelSquare) >= 0.5)
            {
                LabelSquare = next;
                LabelSquareChanged?.Invoke(this, next);
            }
            return;
        }

        if (!_panActive && _headerDrag is null)
            Cursor = (Math.Abs(p.X - _labelSquare) <= ResizeGrip || Math.Abs(p.Y - _labelSquare) <= ResizeGrip)
                ? ResizeCursor : null;

        if (_panActive)
        {
            var delta = p - _pressPos;
            if (!_panning && (Math.Abs(delta.X) > PanThreshold || Math.Abs(delta.Y) > PanThreshold))
            {
                _panning = true;
                _suppressClick = true;
            }
            if (_panning)
            {
                // direct manipulation: current AND target track the pointer 1:1
                _scrollX = _panStartScrollX - delta.X;
                _scrollY = _panStartScrollY - delta.Y;
                _targetScrollX = _scrollX;
                _targetScrollY = _scrollY;
                ClampScroll(l);
                InvalidateVisual();
                UpdateHover(MatrixHit.None);
                return;
            }
        }

        var hit = l.HitTest(p, _scrollX, _scrollY);

        if (_headerDrag is { } hd)
        {
            var overKind = hd.IsInput ? MatrixHitKind.RowHeader : MatrixHitKind.ColHeader;
            if (hit.Kind == overKind && hit.DeviceId != _dragTargetId)
            {
                _dragTargetId = hit.DeviceId;
                InvalidateVisual();
            }
        }

        // Sticky hover: crossing the gaps between tiles must NOT drop the selection
        // (it flickered every 6px of travel). While the pointer stays inside the tile
        // area, only landing on a DIFFERENT tile changes the hover; headers/corner or
        // leaving the area still clear it.
        if (hit.Kind == MatrixHitKind.None && _hoverRowKey is not null &&
            p.X >= l.TilesOriginX && p.Y >= l.TilesOriginY &&
            p.X <= l.TilesOriginX + l.TileAreaWidth - _scrollX &&
            p.Y <= l.TilesOriginY + l.TileAreaHeight - _scrollY)
        {
            return;
        }

        UpdateHover(hit);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var m = _model;
        var l = _layout;

        if (_resizingSquare)
        {
            _resizingSquare = false;
            e.Pointer.Capture(null);
            return;
        }

        if (_headerDrag is { } hd)
        {
            _headerDrag = null;
            var target = _dragTargetId;
            _dragTargetId = null;
            e.Pointer.Capture(null);
            InvalidateVisual();
            if (target is not null && target != hd.DeviceId)
                ReorderRequested?.Invoke(this, new MatrixReorderEvent(hd.IsInput, hd.DeviceId, target));
            return;
        }

        if (_panActive)
        {
            _panActive = false;
            var wasPanning = _panning;
            _panning = false;
            e.Pointer.Capture(null);

            if (!wasPanning && !_suppressClick &&
                e.InitialPressMouseButton == MouseButton.Left &&
                m is not null && l is not null && !m.Locked)
            {
                var hit = l.HitTest(e.GetCurrentPoint(this).Position, _scrollX, _scrollY);
                if (hit.Kind == MatrixHitKind.Tile)
                {
                    var row = l.RowTrackByKey.GetValueOrDefault(hit.RowKey!);
                    var col = l.ColTrackByKey.GetValueOrDefault(hit.ColKey!);
                    if (row is not null && col is not null && !IsBlocked(row, col))
                    {
                        CellToggled?.Invoke(this, new MatrixCellEvent(hit.RowKey!, hit.ColKey!));
                        InvalidateVisual();
                    }
                }
            }
            _suppressClick = false;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var m = _model;
        var l = _layout;
        if (m is null || l is null) return;

        var p = e.GetCurrentPoint(this).Position;
        var hit = l.HitTest(p, _scrollX, _scrollY);

        if (hit.Kind == MatrixHitKind.Tile && !m.Locked &&
            m.Cells.TryGetValue(hit.RowKey + "|" + hit.ColKey, out var cell) && cell.On &&
            Math.Abs(e.Delta.Y) > 0)
        {
            CellGainDelta?.Invoke(this,
                new MatrixCellGainEvent(hit.RowKey!, hit.ColKey!, e.Delta.Y > 0 ? 0.5 : -0.5));
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // scroll the tile region smoothly: the wheel moves the TARGET, the RAF loop eases
        // the visible offset toward it; headers stay pinned by construction
        _targetScrollX -= e.Delta.X * WheelScrollStep;
        _targetScrollY -= e.Delta.Y * WheelScrollStep;
        ClampScroll(l);
        StartScrollAnimation();
        UpdateHover(l.HitTest(p, _scrollX, _scrollY));
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        UpdateHover(MatrixHit.None);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _resizingSquare = false;
        _panActive = false;
        _panning = false;
        _headerDrag = null;
        _dragTargetId = null;
        InvalidateVisual();
    }

    private void UpdateHover(MatrixHit hit)
    {
        var row = hit.Kind == MatrixHitKind.Tile ? hit.RowKey : null;
        var col = hit.Kind == MatrixHitKind.Tile ? hit.ColKey : null;
        if (row == _hoverRowKey && col == _hoverColKey) return;
        _hoverRowKey = row;
        _hoverColKey = col;
        SelectionChanged?.Invoke(this, new MatrixSelectionEvent(row, col));
        InvalidateVisual();
    }
}
