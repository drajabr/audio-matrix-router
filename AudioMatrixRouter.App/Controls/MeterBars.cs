using Avalonia;
using AppTheme = AudioMatrixRouter.App.Theme;
using Avalonia.Controls;
using Avalonia.Media;

namespace AudioMatrixRouter.App.Controls;

/// <summary>
/// N-lane glass meter (docs/DESIGN-REFERENCE.md §3.3/§3.4): one 1fr lane per level with
/// 4px padding and gaps, each bar rounded-4 and filling the whole lane cross-axis, length
/// = level fraction, AppTheme.MeterFill gradient. Horizontal bars grow left→right, vertical
/// bars grow bottom→up. <see cref="SetLevels"/> only sets TARGETS; a RequestAnimationFrame
/// loop eases the displayed levels toward them so 10Hz data reads as 60fps (like the CSS
/// 70ms transition did).
/// </summary>
public sealed class MeterBars : Control
{
    private static int s_palVersion = -1;
    private static IBrush FillH = null!;
    private static IBrush FillV = null!;

    private static void EnsurePalette()
    {
        if (s_palVersion == AppTheme.Version) return;
        s_palVersion = AppTheme.Version;
        FillH = AppTheme.MeterFill(horizontal: true);
        FillV = AppTheme.MeterFill(horizontal: false);
    }

    private double[] _levels = Array.Empty<double>();   // displayed
    private double[] _targets = Array.Empty<double>();  // where the data wants to be
    private bool _animating;

    public bool Horizontal { get; set; } = true;

    /// <summary>0..1 per lane; sets the animation targets and kicks the ease loop.</summary>
    public void SetLevels(IReadOnlyList<double> levels)
    {
        if (levels is null || levels.Count == 0)
        {
            if (_targets.Length == 0 && _levels.Length == 0) return;
            _targets = Array.Empty<double>();
            _levels = Array.Empty<double>();
            InvalidateVisual();
            return;
        }

        if (_targets.Length != levels.Count)
        {
            _targets = new double[levels.Count];
            var old = _levels;
            _levels = new double[levels.Count];
            for (var i = 0; i < _levels.Length && i < old.Length; i++)
                _levels[i] = old[i];
        }
        for (var i = 0; i < levels.Count; i++)
            _targets[i] = Math.Clamp(levels[i], 0, 1);

        StartAnimation();
    }

    private void StartAnimation()
    {
        if (_animating) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            Array.Copy(_targets, _levels, _targets.Length);
            InvalidateVisual();
            return;
        }
        _animating = true;
        top.RequestAnimationFrame(Tick);
    }

    private void Tick(TimeSpan _)
    {
        _animating = false;
        if (_levels.Length != _targets.Length) return;

        var moving = false;
        for (var i = 0; i < _levels.Length; i++)
        {
            var delta = _targets[i] - _levels[i];
            if (Math.Abs(delta) <= 0.004)
            {
                _levels[i] = _targets[i];
            }
            else
            {
                // 70ms-style ease: cur += (target - cur) * 0.35 per frame
                _levels[i] += delta * 0.35;
                moving = true;
            }
        }
        InvalidateVisual();

        if (moving)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is not null)
            {
                _animating = true;
                top.RequestAnimationFrame(Tick);
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        EnsurePalette();
        var n = _levels.Length;
        if (n == 0) return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        const double pad = AppTheme.MeterPad;
        const double gap = AppTheme.MeterPad;

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
