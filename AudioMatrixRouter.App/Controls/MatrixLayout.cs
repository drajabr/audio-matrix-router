using Avalonia;

namespace AudioMatrixRouter.App.Controls;

// ===== shared contract types (the shell codes against these) =====

public sealed class MatrixDeviceInfo
{
    public string Id = "";
    public string Label = "";
    public string SubLabel = "";
    public int Channels = 2;
    public bool IsMaster;
    public bool IsLoopback;
    public float[] Peaks = Array.Empty<float>();   // 0..1 per channel, mutated in place by the shell
}

public sealed class MatrixCell
{
    public bool On;
    public float GainDb;
    public bool PhaseInverted;
}

public sealed class MatrixModel
{
    public List<MatrixDeviceInfo> Inputs = new();
    public List<MatrixDeviceInfo> Outputs = new();
    // key = rowKey + "|" + colKey; rowKey/colKey = "dev:{id}" in device view, "ch:{id}:{n}" in channel view
    public Dictionary<string, MatrixCell> Cells = new();
    public bool ChannelView;
    public bool Locked;
}

public sealed record MatrixCellEvent(string RowKey, string ColKey);
public sealed record MatrixCellGainEvent(string RowKey, string ColKey, double DeltaDb);
public sealed record MatrixHeaderEvent(bool IsInput, string DeviceId);
public sealed record MatrixReorderEvent(bool IsInput, string DeviceId, string TargetDeviceId);
public sealed record MatrixSelectionEvent(string? RowKey, string? ColKey);

// ===== layout math =====

public enum MatrixHitKind { None, Corner, RowHeader, ColHeader, RowChip, ColChip, Tile }

public readonly record struct MatrixHit(
    MatrixHitKind Kind,
    string? DeviceId = null,
    int Channel = -1,
    string? RowKey = null,
    string? ColKey = null)
{
    public static readonly MatrixHit None = new(MatrixHitKind.None);
}

/// <summary>One device on an axis: occupies <see cref="UnitSpan"/> consecutive 54px units.</summary>
public sealed class MatrixAxisEntry
{
    public required MatrixDeviceInfo Device { get; init; }
    public required bool IsInput { get; init; }
    public required int StartUnit { get; init; }
    public required int UnitSpan { get; init; }

    public string DeviceKey => "dev:" + Device.Id;
    public string ChannelKey(int channel) => $"ch:{Device.Id}:{channel}";
}

/// <summary>One tile track on an axis: a whole device (device view) or one channel (channel view).</summary>
public sealed class MatrixTrack
{
    public required string Key { get; init; }
    public required MatrixAxisEntry Entry { get; init; }
    public required int StartUnit { get; init; }
    public required int UnitSpan { get; init; }
}

/// <summary>
/// Pure geometry for the matrix: the square law (every unit is Theme.Unit, gaps Theme.Gap,
/// device tile = channels x channels units) plus arithmetic hit-testing on the unit grid.
/// No Avalonia dependencies beyond Point/Rect primitives.
/// </summary>
public sealed class MatrixLayout
{
    public const double Pitch = Theme.Unit + Theme.Gap;

    private readonly List<MatrixAxisEntry> _rows = new();
    private readonly List<MatrixAxisEntry> _cols = new();
    private readonly List<MatrixTrack> _rowTracks = new();
    private readonly List<MatrixTrack> _colTracks = new();
    private readonly MatrixAxisEntry?[] _rowUnitMap;
    private readonly MatrixAxisEntry?[] _colUnitMap;
    private readonly MatrixTrack?[] _rowTrackMap;
    private readonly MatrixTrack?[] _colTrackMap;

    public MatrixLayout(MatrixModel model, double labelSquare)
    {
        Model = model;
        LabelSquare = labelSquare;

        RowUnits = BuildAxis(model.Inputs, isInput: true, _rows);
        ColUnits = BuildAxis(model.Outputs, isInput: false, _cols);
        _rowUnitMap = BuildUnitMap(_rows, RowUnits);
        _colUnitMap = BuildUnitMap(_cols, ColUnits);

        BuildTracks(model.ChannelView, _rows, _rowTracks);
        BuildTracks(model.ChannelView, _cols, _colTracks);
        _rowTrackMap = BuildTrackMap(_rowTracks, RowUnits);
        _colTrackMap = BuildTrackMap(_colTracks, ColUnits);

        foreach (var t in _rowTracks) RowTrackByKey[t.Key] = t;
        foreach (var t in _colTracks) ColTrackByKey[t.Key] = t;
    }

    public MatrixModel Model { get; }
    public double LabelSquare { get; }
    public int RowUnits { get; }
    public int ColUnits { get; }

    public IReadOnlyList<MatrixAxisEntry> Rows => _rows;
    public IReadOnlyList<MatrixAxisEntry> Cols => _cols;
    public IReadOnlyList<MatrixTrack> RowTracks => _rowTracks;
    public IReadOnlyList<MatrixTrack> ColTracks => _colTracks;
    public Dictionary<string, MatrixTrack> RowTrackByKey { get; } = new();
    public Dictionary<string, MatrixTrack> ColTrackByKey { get; } = new();

    public double TilesOriginX => LabelSquare + Theme.Gap;
    public double TilesOriginY => LabelSquare + Theme.Gap;
    public double TileAreaWidth => SpanSize(ColUnits);
    public double TileAreaHeight => SpanSize(RowUnits);
    public double ContentWidth => TilesOriginX + TileAreaWidth;
    public double ContentHeight => TilesOriginY + TileAreaHeight;

    public Rect CornerRect => new(0, 0, LabelSquare, LabelSquare);

    public static double UnitPos(int unit) => unit * Pitch;
    public static double SpanSize(int units) => units <= 0 ? 0 : units * Theme.Unit + (units - 1) * Theme.Gap;

    /// <summary>"dev:x" → x, "ch:x:n" → x (device ids may themselves contain ':').</summary>
    public static string DeviceIdOfKey(string key)
    {
        if (key.StartsWith("dev:", StringComparison.Ordinal)) return key[4..];
        if (key.StartsWith("ch:", StringComparison.Ordinal))
        {
            var last = key.LastIndexOf(':');
            if (last > 3) return key[3..last];
        }
        return key;
    }

    // ===== rects (control coordinates, given the current scroll offsets) =====

    // Cards start 1px inside the control edge: a 1px stroke centered on y=0 gets its
    // top half clipped by the control bounds, which read as a "cut" top border.
    public Rect ColCardRect(MatrixAxisEntry e, double scrollX) =>
        new(TilesOriginX + UnitPos(e.StartUnit) - scrollX, 1,
            SpanSize(e.UnitSpan), LabelSquare - Theme.Gap - Theme.ChipShort - 1);

    public Rect ColChipRect(MatrixAxisEntry e, int channel, double scrollX) =>
        new(TilesOriginX + UnitPos(e.StartUnit + channel) - scrollX, LabelSquare - Theme.ChipShort,
            Theme.Unit, Theme.ChipShort);

    // Same 1px inset on the left edge (see ColCardRect).
    public Rect RowCardRect(MatrixAxisEntry e, double scrollY) =>
        new(1, TilesOriginY + UnitPos(e.StartUnit) - scrollY,
            LabelSquare - Theme.Gap - Theme.ChipShort - 1, SpanSize(e.UnitSpan));

    public Rect RowChipRect(MatrixAxisEntry e, int channel, double scrollY) =>
        new(LabelSquare - Theme.ChipShort, TilesOriginY + UnitPos(e.StartUnit + channel) - scrollY,
            Theme.ChipShort, Theme.Unit);

    public Rect TileRect(MatrixTrack row, MatrixTrack col, double scrollX, double scrollY) =>
        new(TilesOriginX + UnitPos(col.StartUnit) - scrollX,
            TilesOriginY + UnitPos(row.StartUnit) - scrollY,
            SpanSize(col.UnitSpan), SpanSize(row.UnitSpan));

    // ===== hit testing (integer arithmetic on the unit grid) =====

    public MatrixHit HitTest(Point p, double scrollX, double scrollY)
    {
        if (p.X < 0 || p.Y < 0) return MatrixHit.None;

        var inLeft = p.X < TilesOriginX;
        var inTop = p.Y < TilesOriginY;

        if (inLeft && inTop)
            return p.X < LabelSquare && p.Y < LabelSquare ? new MatrixHit(MatrixHitKind.Corner) : MatrixHit.None;

        if (inTop)
        {
            var e = EntryAtPx(_colUnitMap, p.X - TilesOriginX + scrollX, out var unit, out var inUnit);
            if (e is null) return MatrixHit.None;
            if (p.Y >= LabelSquare - Theme.ChipShort && p.Y < LabelSquare)
                return inUnit
                    ? new MatrixHit(MatrixHitKind.ColChip, e.Device.Id, unit - e.StartUnit)
                    : MatrixHit.None;
            if (p.Y < LabelSquare - Theme.ChipShort - Theme.Gap)
                return new MatrixHit(MatrixHitKind.ColHeader, e.Device.Id);
            return MatrixHit.None; // gap between card and chip strip
        }

        if (inLeft)
        {
            var e = EntryAtPx(_rowUnitMap, p.Y - TilesOriginY + scrollY, out var unit, out var inUnit);
            if (e is null) return MatrixHit.None;
            if (p.X >= LabelSquare - Theme.ChipShort && p.X < LabelSquare)
                return inUnit
                    ? new MatrixHit(MatrixHitKind.RowChip, e.Device.Id, unit - e.StartUnit)
                    : MatrixHit.None;
            if (p.X < LabelSquare - Theme.ChipShort - Theme.Gap)
                return new MatrixHit(MatrixHitKind.RowHeader, e.Device.Id);
            return MatrixHit.None;
        }

        var rowTrack = TrackAtPx(_rowTrackMap, p.Y - TilesOriginY + scrollY);
        var colTrack = TrackAtPx(_colTrackMap, p.X - TilesOriginX + scrollX);
        if (rowTrack is null || colTrack is null) return MatrixHit.None;
        return new MatrixHit(MatrixHitKind.Tile, null, -1, rowTrack.Key, colTrack.Key);
    }

    // ===== internals =====

    private static int BuildAxis(List<MatrixDeviceInfo> devices, bool isInput, List<MatrixAxisEntry> target)
    {
        var unit = 0;
        foreach (var d in devices)
        {
            var span = Math.Max(1, d.Channels);
            target.Add(new MatrixAxisEntry { Device = d, IsInput = isInput, StartUnit = unit, UnitSpan = span });
            unit += span;
        }
        return unit;
    }

    private static MatrixAxisEntry?[] BuildUnitMap(List<MatrixAxisEntry> entries, int units)
    {
        var map = new MatrixAxisEntry?[units];
        foreach (var e in entries)
            for (var u = 0; u < e.UnitSpan; u++)
                map[e.StartUnit + u] = e;
        return map;
    }

    private static void BuildTracks(bool channelView, List<MatrixAxisEntry> entries, List<MatrixTrack> target)
    {
        foreach (var e in entries)
        {
            if (channelView)
            {
                for (var c = 0; c < e.UnitSpan; c++)
                    target.Add(new MatrixTrack { Key = e.ChannelKey(c), Entry = e, StartUnit = e.StartUnit + c, UnitSpan = 1 });
            }
            else
            {
                target.Add(new MatrixTrack { Key = e.DeviceKey, Entry = e, StartUnit = e.StartUnit, UnitSpan = e.UnitSpan });
            }
        }
    }

    private static MatrixTrack?[] BuildTrackMap(List<MatrixTrack> tracks, int units)
    {
        var map = new MatrixTrack?[units];
        foreach (var t in tracks)
            for (var u = 0; u < t.UnitSpan; u++)
                map[t.StartUnit + u] = t;
        return map;
    }

    private static MatrixAxisEntry? EntryAtPx(MatrixAxisEntry?[] unitMap, double px, out int unit, out bool inUnit)
    {
        unit = -1;
        inUnit = false;
        if (px < 0) return null;
        var u = (int)(px / Pitch);
        if (u >= unitMap.Length) return null;
        var e = unitMap[u];
        if (e is null) return null;
        var off = px - u * Pitch;
        if (off < Theme.Unit)
        {
            unit = u;
            inUnit = true;
            return e;
        }
        // point sits in the 4px gap after unit u — still on the same card if the next unit belongs to it
        if (u + 1 < unitMap.Length && ReferenceEquals(unitMap[u + 1], e))
        {
            unit = u;
            return e;
        }
        return null;
    }

    private static MatrixTrack? TrackAtPx(MatrixTrack?[] trackMap, double px)
    {
        if (px < 0) return null;
        var u = (int)(px / Pitch);
        if (u >= trackMap.Length) return null;
        var t = trackMap[u];
        if (t is null) return null;
        var off = px - u * Pitch;
        if (off < Theme.Unit) return t;
        // in a gap: only inside the tile when the gap is interior to a multi-unit tile (device view)
        if (u + 1 < trackMap.Length && ReferenceEquals(trackMap[u + 1], t)) return t;
        return null;
    }
}
