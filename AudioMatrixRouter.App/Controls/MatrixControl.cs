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

    private static readonly FontFamily MonoFamily = new("Consolas,Courier New,monospace");
    private static readonly Typeface FaceRegular = new(MonoFamily);
    private static readonly Typeface FaceBold = new(MonoFamily, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface FaceHeavy = new(MonoFamily, FontStyle.Normal, FontWeight.ExtraBold);

    // cached brushes/pens (per DESIGN-REFERENCE mixes)
    private static readonly IBrush CardFill = new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Panel, 0.92));
    private static readonly IBrush ChipTextBrush = new SolidColorBrush(AppTheme.Mix(AppTheme.AccentHl, AppTheme.Text, 0.76));
    private static readonly IBrush EdgeLightBrush = new SolidColorBrush(AppTheme.WithAlpha(Colors.White, 0.10));
    private static readonly IBrush EdgeShadeBrush = new SolidColorBrush(AppTheme.WithAlpha(Colors.Black, 0.38));
    private static readonly IBrush TextShadowBrush = new SolidColorBrush(AppTheme.WithAlpha(Colors.Black, 0.55));

    private static readonly IPen LinePen = new Pen(AppTheme.LineBrush);
    private static readonly IPen LineStrongPen = new Pen(AppTheme.LineStrongBrush);
    private static readonly IPen OffTilePen = new Pen(AppTheme.LineStrongBrush);
    private static readonly IPen OnTilePen = new Pen(new SolidColorBrush(AppTheme.Mix(AppTheme.Accent, Colors.White, 0.84)), 1);
    private static readonly IPen HoverTilePen = new Pen(new SolidColorBrush(AppTheme.Mix(AppTheme.AccentHl, AppTheme.Line, 0.58)), 1.25);
    private static readonly IPen PhaseTilePen = new Pen(new SolidColorBrush(AppTheme.Mix(AppTheme.Phase, Colors.White, 0.72)), 1);
    private static readonly IPen BlockedPen = new Pen(AppTheme.LineStrongBrush, 1, new DashStyle(new double[] { 3, 3 }, 0));
    private static readonly IPen GlowInnerPen = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.45)), 2.5);
    private static readonly IPen GlowOuterPen = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.14)), 4);
    private static readonly IPen PhaseGlowPen = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Phase, 0.30)), 2.5);
    private static readonly IPen PhaseStripePen = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Phase, 0.18)), 4);
    private static readonly IPen HatchPenA = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Mix(AppTheme.Surface, Colors.Black, 0.55), 0.85)), 4);
    private static readonly IPen MasterRingPen = new Pen(new SolidColorBrush(AppTheme.Mix(AppTheme.Accent, Colors.White, 0.78)), 1);
    private static readonly IPen MasterRingInsetPen = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.45)), 1);
    private static readonly IPen MasterGlowPen = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.22)), 4);
    private static readonly IPen BadgeGlowPen = new Pen(new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.35)), 2);
    private static readonly IPen DragTargetPen = new Pen(new SolidColorBrush(AppTheme.Mix(AppTheme.AccentHl, AppTheme.Line, 0.70)), 1.5);

    private static readonly IBrush KeyFaceBrush = AppTheme.KeyFace();
    private static readonly IBrush AccentFaceBrush = AppTheme.AccentFace();
    private static readonly IBrush MeterFillH = AppTheme.MeterFill(horizontal: true);
    private static readonly IBrush MeterFillV = AppTheme.MeterFill(horizontal: false);

    private MatrixModel? _model;
    private MatrixLayout? _layout;
    private double _labelSquare = 224;

    private double _scrollX;
    private double _scrollY;

    private string? _hoverRowKey;
    private string? _hoverColKey;

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

    public event EventHandler<MatrixCellEvent>? CellToggled;
    public event EventHandler<MatrixCellGainEvent>? CellGainDelta;
    public event EventHandler<MatrixCellEvent>? CellPhaseToggled;
    public event EventHandler<MatrixCellEvent>? CellGainReset;
    public event EventHandler<MatrixHeaderEvent>? MasterRequested;
    public event EventHandler<MatrixReorderEvent>? ReorderRequested;
    public event EventHandler<MatrixSelectionEvent>? SelectionChanged;

    /// <summary>Cheap invalidate — peaks arrays are mutated in place by the shell.</summary>
    public void RefreshPeaks() => InvalidateVisual();

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
        var maxX = Math.Max(0, l.ContentWidth - Bounds.Width);
        var maxY = Math.Max(0, l.ContentHeight - Bounds.Height);
        _scrollX = Math.Clamp(_scrollX, 0, maxX);
        _scrollY = Math.Clamp(_scrollY, 0, maxY);
    }

    // ===================================================================== rendering

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        // transparent fill so the whole control is pointer-hit-testable
        context.DrawRectangle(Brushes.Transparent, null, new Rect(size));

        var m = _model;
        if (m is null) return;
        var l = EnsureLayout()!;
        ClampScroll(l);
        ComputeActiveDevices(m);

        var tileClip = new Rect(
            l.TilesOriginX, l.TilesOriginY,
            Math.Max(0, size.Width - l.TilesOriginX), Math.Max(0, size.Height - l.TilesOriginY));
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
                DrawTile(ctx, new Rect(x, y, w, h), rt, ct, cell, hasSel, selRow, selCol);
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

        var opacity = blocked ? 0.5 : on ? 1.0 : 0.62;
        if (hasSel && !isHover && !onPath)
            opacity = on ? 0.8 : blocked ? 0.4 : 0.45;

        var pen = blocked ? BlockedPen
            : isHover ? HoverTilePen
            : phase ? PhaseTilePen
            : on ? OnTilePen
            : OffTilePen;

        var rr = new RoundedRect(rect, AppTheme.RadiusTile);

        using (ctx.PushOpacity(opacity))
        {
            if (on)
            {
                // approximate the accent glow with two layered semi-transparent strokes
                ctx.DrawRectangle(null, GlowOuterPen, new RoundedRect(rect.Inflate(4), AppTheme.RadiusTile + 4));
                ctx.DrawRectangle(null, GlowInnerPen, new RoundedRect(rect.Inflate(1.5), AppTheme.RadiusTile + 1.5));
            }
            if (phase)
                ctx.DrawRectangle(null, PhaseGlowPen, new RoundedRect(rect.Inflate(1.5), AppTheme.RadiusTile + 1.5));

            ctx.DrawRectangle(on ? AccentFaceBrush : KeyFaceBrush, pen, rr);

            // --fx-edge: 1px light on top, 1px shade on the bottom (inset, corner-clear)
            if (rect.Width > 12 && rect.Height > 10)
            {
                ctx.DrawRectangle(EdgeLightBrush, null, new Rect(rect.X + 4, rect.Y + 1, rect.Width - 8, 1));
                ctx.DrawRectangle(EdgeShadeBrush, null, new Rect(rect.X + 4, rect.Bottom - 2, rect.Width - 8, 1));
            }

            if (blocked)
                DrawDiagonalStripes(ctx, rr, HatchPenA, 8);
            else if (phase)
                DrawDiagonalStripes(ctx, rr, PhaseStripePen, 9);

            if (on && cell is not null && Math.Abs(cell.GainDb) >= 0.5)
            {
                var text = FormatGain(cell.GainDb);
                var ft = Ft(text, FaceBold, 9, AppTheme.TextOnAccentBrush);
                var origin = new Point(rect.Center.X - ft.Width / 2, rect.Center.Y - ft.Height / 2);
                var shadow = Ft(text, FaceBold, 9, TextShadowBrush);
                ctx.DrawText(shadow, origin + new Vector(0, 1));
                ctx.DrawText(ft, origin);
            }
        }
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

    private static string FormatGain(double gainDb) =>
        (gainDb >= 0 ? "+" : "") + gainDb.ToString("0.#", CultureInfo.InvariantCulture) + "dB";

    // --------------------------------------------------------------------- column headers

    private void DrawColHeader(DrawingContext ctx, MatrixLayout l, MatrixAxisEntry e, double viewWidth)
    {
        var card = l.ColCardRect(e, _scrollX);
        if (card.Right < l.TilesOriginX || card.X > viewWidth) return;

        var master = e.Device.IsMaster;
        var active = master || _activeOutputs.Contains(e.Device.Id);
        var isDragSource = _headerDrag is { IsInput: false } ds && ds.DeviceId == e.Device.Id;
        var isDragTarget = _headerDrag is { IsInput: false } dt && dt.DeviceId != e.Device.Id && _dragTargetId == e.Device.Id;

        var opacity = active ? 1.0 : 0.62;
        if (isDragSource) opacity = Math.Min(opacity, 0.7);

        using (ctx.PushOpacity(opacity))
        {
            if (master)
                ctx.DrawRectangle(null, MasterGlowPen, new RoundedRect(card.Inflate(2), AppTheme.RadiusPanel + 2));

            var borderPen = isDragTarget ? DragTargetPen : master ? MasterRingPen : LinePen;
            var cardRR = new RoundedRect(card, AppTheme.RadiusPanel);
            ctx.DrawRectangle(CardFill, borderPen, cardRR);
            if (master)
                ctx.DrawRectangle(null, MasterRingInsetPen, new RoundedRect(card.Deflate(2), AppTheme.RadiusPanel - 2));

            using (ctx.PushClip(cardRR))
            {
                // meters: one vertical glass bar per channel, bottom-aligned; inset 24px at the
                // bottom when the MASTER badge occupies that edge
                var n = Math.Max(1, e.Device.Channels);
                var meterBottom = card.Bottom - (master ? AppTheme.BadgeInset : AppTheme.Gap);
                var meterTop = card.Y + AppTheme.Gap;
                var laneW = (card.Width - 2 * AppTheme.Gap - AppTheme.Gap * (n - 1)) / n;
                for (var c = 0; c < n && laneW > 1; c++)
                {
                    var level = PeakOf(e.Device, c);
                    if (level <= 0) continue;
                    var bh = Math.Max(0, (meterBottom - meterTop) * level);
                    var bx = card.X + AppTheme.Gap + c * (laneW + AppTheme.Gap);
                    ctx.DrawRectangle(MeterFillV, null,
                        new RoundedRect(new Rect(bx, meterBottom - bh, laneW, bh), AppTheme.RadiusMicro));
                }

                // rotated name + sub-label (reads bottom-up), anchored bottom-left, over the meters
                var textBottom = card.Bottom - (master ? AppTheme.BadgeInset : 0) - 8;
                var maxLen = Math.Max(8, textBottom - card.Y - 8);
                var name = Ft(e.Device.Label, FaceBold, 13, AppTheme.TextStrongBrush, maxLen);
                var tx = card.X + 8;
                using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(tx, textBottom)))
                {
                    ctx.DrawText(name, default);
                }
                if (!string.IsNullOrEmpty(e.Device.SubLabel))
                {
                    var sub = Ft(e.Device.SubLabel, FaceRegular, 9, AppTheme.MutedBrush, maxLen);
                    using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2) *
                                             Matrix.CreateTranslation(tx + name.Height + 6, textBottom)))
                    {
                        ctx.DrawText(sub, default);
                    }
                }

                if (master)
                {
                    // MASTER edge bar on the BOTTOM edge: bottom corners rounded, top square
                    var badge = new Rect(card.X, card.Bottom - AppTheme.BadgeSize, card.Width, AppTheme.BadgeSize);
                    DrawMasterBadge(ctx, badge, vertical: false,
                        RoundRect(badge, 0, 0, AppTheme.RadiusPanel, AppTheme.RadiusPanel));
                }
            }

            // detached channel chips BELOW the card — one per channel unit
            for (var c = 0; c < Math.Max(1, e.Device.Channels); c++)
                DrawChip(ctx, l.ColChipRect(e, c, _scrollX), ChipLabel(e.Device.Channels, c));
        }
    }

    // --------------------------------------------------------------------- row headers

    private void DrawRowHeader(DrawingContext ctx, MatrixLayout l, MatrixAxisEntry e, double viewHeight)
    {
        var card = l.RowCardRect(e, _scrollY);
        if (card.Bottom < l.TilesOriginY || card.Y > viewHeight) return;

        var master = e.Device.IsMaster;
        var active = master || _activeInputs.Contains(e.Device.Id);
        var isDragSource = _headerDrag is { IsInput: true } ds && ds.DeviceId == e.Device.Id;
        var isDragTarget = _headerDrag is { IsInput: true } dt && dt.DeviceId != e.Device.Id && _dragTargetId == e.Device.Id;

        var opacity = active ? 1.0 : 0.62;
        if (isDragSource) opacity = Math.Min(opacity, 0.7);

        using (ctx.PushOpacity(opacity))
        {
            if (master)
                ctx.DrawRectangle(null, MasterGlowPen, new RoundedRect(card.Inflate(2), AppTheme.RadiusPanel + 2));

            var borderPen = isDragTarget ? DragTargetPen : master ? MasterRingPen : LinePen;
            var cardRR = new RoundedRect(card, AppTheme.RadiusPanel);
            ctx.DrawRectangle(CardFill, borderPen, cardRR);
            if (master)
                ctx.DrawRectangle(null, MasterRingInsetPen, new RoundedRect(card.Deflate(2), AppTheme.RadiusPanel - 2));

            using (ctx.PushClip(cardRR))
            {
                // meters: one horizontal glass bar per channel, each filling its full tile lane
                // height (minus 4px pad/gaps); left inset 24px when the MASTER bar sits there
                var n = Math.Max(1, e.Device.Channels);
                var meterLeft = card.X + (master ? AppTheme.BadgeInset : AppTheme.Gap);
                var meterRight = card.Right - AppTheme.Gap;
                var laneH = (card.Height - 2 * AppTheme.Gap - AppTheme.Gap * (n - 1)) / n;
                for (var c = 0; c < n && laneH > 1; c++)
                {
                    var level = PeakOf(e.Device, c);
                    if (level <= 0) continue;
                    var bw = Math.Max(0, (meterRight - meterLeft) * level);
                    var by = card.Y + AppTheme.Gap + c * (laneH + AppTheme.Gap);
                    ctx.DrawRectangle(MeterFillH, null,
                        new RoundedRect(new Rect(meterLeft, by, bw, laneH), AppTheme.RadiusMicro));
                }

                // name + sub anchored TOP-LEFT, floating over the meters
                var textLeft = card.X + (master ? AppTheme.BadgeInset : 0) + 8;
                var maxW = Math.Max(8, card.Right - textLeft - 8);
                var name = Ft(e.Device.Label, FaceBold, 13, AppTheme.TextStrongBrush, maxW);
                ctx.DrawText(name, new Point(textLeft, card.Y + 8));
                if (!string.IsNullOrEmpty(e.Device.SubLabel))
                {
                    var sub = Ft(e.Device.SubLabel, FaceRegular, 9, AppTheme.MutedBrush, maxW);
                    ctx.DrawText(sub, new Point(textLeft, card.Y + 8 + name.Height + 6));
                }

                if (master)
                {
                    // MASTER edge bar on the LEFT edge: left corners rounded, right square
                    var badge = new Rect(card.X, card.Y, AppTheme.BadgeSize, card.Height);
                    DrawMasterBadge(ctx, badge, vertical: true,
                        RoundRect(badge, AppTheme.RadiusPanel, 0, 0, AppTheme.RadiusPanel));
                }
            }

            // detached channel chips at the RIGHT of the card — one per channel unit
            for (var c = 0; c < Math.Max(1, e.Device.Channels); c++)
                DrawChip(ctx, l.RowChipRect(e, c, _scrollY), ChipLabel(e.Device.Channels, c));
        }
    }

    // --------------------------------------------------------------------- shared header pieces

    private static void DrawMasterBadge(DrawingContext ctx, Rect badge, bool vertical, RoundedRect rr)
    {
        ctx.DrawRectangle(null, BadgeGlowPen, rr);
        ctx.DrawRectangle(AccentFaceBrush, null, rr);
        // inner top light / bottom shade
        ctx.DrawRectangle(new SolidColorBrush(AppTheme.WithAlpha(Colors.White, 0.34)), null,
            new Rect(badge.X + 3, badge.Y + 1, badge.Width - 6, 1));
        ctx.DrawRectangle(new SolidColorBrush(AppTheme.WithAlpha(Colors.Black, 0.24)), null,
            new Rect(badge.X + 3, badge.Bottom - 2, badge.Width - 6, 1));

        var ft = Ft("MASTER", FaceHeavy, 8.5, AppTheme.TextOnAccentBrush);
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
        var ft = Ft(label, FaceHeavy, 9, ChipTextBrush);
        ctx.DrawText(ft, new Point(rect.Center.X - ft.Width / 2, rect.Center.Y - ft.Height / 2));
    }

    private static string ChipLabel(int channels, int c) =>
        channels <= 1 ? "M" : channels == 2 ? (c == 0 ? "L" : "R") : (c + 1).ToString(CultureInfo.InvariantCulture);

    private static double PeakOf(MatrixDeviceInfo d, int channel) =>
        d.Peaks.Length > channel ? Math.Clamp(d.Peaks[channel], 0f, 1f) : 0;

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
                _scrollX = _panStartScrollX - delta.X;
                _scrollY = _panStartScrollY - delta.Y;
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

        UpdateHover(hit);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var m = _model;
        var l = _layout;

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

        // scroll the tile region; headers stay pinned by construction (they only translate
        // along their own axis)
        _scrollX -= e.Delta.X * WheelScrollStep;
        _scrollY -= e.Delta.Y * WheelScrollStep;
        ClampScroll(l);
        InvalidateVisual();
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
