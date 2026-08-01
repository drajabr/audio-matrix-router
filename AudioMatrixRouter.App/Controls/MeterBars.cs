using Avalonia;
using AppTheme = AudioMatrixRouter.App.Theme;
using Avalonia.Controls;
using Avalonia.Media;

namespace AudioMatrixRouter.App.Controls;

/// <summary>
/// N-lane glass meter (docs/DESIGN-REFERENCE.md §3.3/§3.4): one 1fr lane per level with
/// 4px padding and gaps, each bar rounded-4 and filling the whole lane cross-axis, length
/// = level fraction, AppTheme.MeterFill gradient. Horizontal bars grow left→right, vertical
/// bars grow bottom→up.
/// </summary>
public sealed class MeterBars : Control
{
    private static readonly IBrush FillH = AppTheme.MeterFill(horizontal: true);
    private static readonly IBrush FillV = AppTheme.MeterFill(horizontal: false);

    private double[] _levels = Array.Empty<double>();

    public bool Horizontal { get; set; } = true;

    /// <summary>0..1 per lane; invalidates.</summary>
    public void SetLevels(IReadOnlyList<double> levels)
    {
        if (levels is null || levels.Count == 0)
        {
            _levels = Array.Empty<double>();
        }
        else
        {
            if (_levels.Length != levels.Count) _levels = new double[levels.Count];
            for (var i = 0; i < levels.Count; i++)
                _levels[i] = Math.Clamp(levels[i], 0, 1);
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var n = _levels.Length;
        if (n == 0) return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        const double pad = AppTheme.Gap;
        const double gap = AppTheme.Gap;

        if (Horizontal)
        {
            var laneH = (h - 2 * pad - gap * (n - 1)) / n;
            if (laneH <= 0) return;
            var maxW = Math.Max(0, w - 2 * pad);
            for (var i = 0; i < n; i++)
            {
                var bw = maxW * _levels[i];
                if (bw <= 0) continue;
                var y = pad + i * (laneH + gap);
                context.DrawRectangle(FillH, null,
                    new RoundedRect(new Rect(pad, y, bw, laneH), AppTheme.RadiusMicro));
            }
        }
        else
        {
            var laneW = (w - 2 * pad - gap * (n - 1)) / n;
            if (laneW <= 0) return;
            var maxH = Math.Max(0, h - 2 * pad);
            for (var i = 0; i < n; i++)
            {
                var bh = maxH * _levels[i];
                if (bh <= 0) continue;
                var x = pad + i * (laneW + gap);
                context.DrawRectangle(FillV, null,
                    new RoundedRect(new Rect(x, h - pad - bh, laneW, bh), AppTheme.RadiusMicro));
            }
        }
    }
}
