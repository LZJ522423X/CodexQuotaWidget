namespace CodexQuotaWidget.Core.ControlCenter;

/// <summary>Monitor geometry and saved capsule positions are physical desktop pixels, including negative coordinates.</summary>
public readonly record struct CapsulePhysicalRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public double CenterX => Left + Width / 2;
    public bool IsUsable => double.IsFinite(Left) && double.IsFinite(Top)
        && double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0
        && double.IsFinite(Right) && double.IsFinite(Bottom)
        && Left >= int.MinValue && Top >= int.MinValue && Right <= int.MaxValue && Bottom <= int.MaxValue;

    public bool Contains(double x, double y) => IsUsable && double.IsFinite(x) && double.IsFinite(y)
        && x >= Left && x < Right && y >= Top && y < Bottom;
}

public sealed record CapsulePlacementMonitor(string DeviceName, CapsulePhysicalRect Bounds,
    CapsulePhysicalRect WorkArea, double ScaleX = 1, double ScaleY = 1);

public readonly record struct CapsulePlacementResult(string MonitorDevice, double CenterX, double TopY,
    double Left, double Width, double Height, double ScaleX, double ScaleY);

public enum CapsuleDockSide { None, Top, Left, Right }

/// <summary>Pure placement calculations: no Windows calls, configuration writes, focus changes, or screen assumptions.</summary>
public static class CapsulePlacement
{
    /// <summary>
    /// Keep the saved device if present; otherwise use the screen containing/nearest the saved point.
    /// A missing or invalid position defaults to the first monitor (the UI supplies primary first).
    /// </summary>
    public static CapsulePlacementResult? Restore(IReadOnlyList<CapsulePlacementMonitor> monitors,
        string? preferredDevice, double? centerX, double? topY, double widthDip, double heightDip,
        double marginDip = 12)
    {
        var available = monitors.Where(m => m.Bounds.IsUsable).ToArray();
        if (available.Length == 0) return null;
        var monitor = string.IsNullOrWhiteSpace(preferredDevice) ? null
            : available.FirstOrDefault(m => string.Equals(m.DeviceName, preferredDevice, StringComparison.OrdinalIgnoreCase));
        var validPoint = centerX is { } x && IsPhysicalCoordinate(x) && topY is { } y && IsPhysicalCoordinate(y);
        if (monitor is null && validPoint)
        {
            monitor = available.FirstOrDefault(m => m.Bounds.Contains(centerX!.Value, topY!.Value))
                ?? available.OrderBy(m => DistanceTo(m.Bounds, centerX!.Value, topY!.Value)).First();
        }
        monitor ??= available[0];
        var area = WorkingArea(monitor);
        var sy = Scale(monitor.ScaleY);
        return Clamp(monitor, validPoint ? centerX!.Value : area.CenterX,
            validPoint ? topY!.Value : area.Top + SafeMargin(marginDip) * sy,
            widthDip, heightDip, marginDip);
    }

    /// <summary>Size is in WPF DIPs; the returned rectangle is always physical pixels inside this monitor's working area.</summary>
    public static CapsulePlacementResult? Clamp(CapsulePlacementMonitor monitor, double centerX,
        double topY, double widthDip, double heightDip, double marginDip = 12)
    {
        if (!monitor.Bounds.IsUsable) return null;
        var area = WorkingArea(monitor);
        var sx = Scale(monitor.ScaleX);
        var sy = Scale(monitor.ScaleY);
        var margin = SafeMargin(marginDip);
        // Small monitors/windows must remain visible even when a desired capsule is wider than the screen.
        var mx = Math.Min(margin * sx, Math.Max(0, (area.Width - 1) / 2));
        var my = Math.Min(margin * sy, Math.Max(0, (area.Height - 1) / 2));
        var width = Math.Min(Size(widthDip, 400) * sx, area.Width - mx * 2);
        var height = Math.Min(Size(heightDip, 56) * sy, area.Height - my * 2);
        var desiredCenter = IsPhysicalCoordinate(centerX) ? centerX : area.CenterX;
        var desiredTop = IsPhysicalCoordinate(topY) ? topY : area.Top + my;
        var left = Math.Clamp(desiredCenter - width / 2, area.Left + mx, area.Right - mx - width);
        var top = Math.Clamp(desiredTop, area.Top + my, area.Bottom - my - height);
        return new(monitor.DeviceName, left + width / 2, top, left, width, height, sx, sy);
    }

    /// <summary>
    /// Choose a nearby real screen edge from the saved expanded footprint. Distances use
    /// that monitor's DIPs; the working area only protects taskbars, never becomes a dock edge.
    /// Does not move, clamp, or change the normal position. There is deliberately no bottom dock.
    /// </summary>
    public static CapsuleDockSide GetDockSide(CapsulePlacementMonitor monitor, double centerX, double topY,
        double widthDip = 520, double heightDip = 166, double thresholdDip = 24)
    {
        if (!monitor.Bounds.IsUsable || !monitor.WorkArea.IsUsable
            || !IsPhysicalCoordinate(centerX) || !IsPhysicalCoordinate(topY)
            || !double.IsFinite(widthDip) || widthDip <= 0 || !double.IsFinite(heightDip) || heightDip <= 0
            || !double.IsFinite(thresholdDip) || thresholdDip < 0
            || !double.IsFinite(monitor.ScaleX) || monitor.ScaleX is < .5 or > 8
            || !double.IsFinite(monitor.ScaleY) || monitor.ScaleY is < .5 or > 8)
            return CapsuleDockSide.None;
        var bounds = monitor.Bounds;
        var work = monitor.WorkArea;
        if (work.Right <= bounds.Left || work.Left >= bounds.Right || work.Bottom <= bounds.Top || work.Top >= bounds.Bottom)
            return CapsuleDockSide.None;
        var footprint = new CapsulePhysicalRect(centerX - widthDip * monitor.ScaleX / 2,
            topY, widthDip * monitor.ScaleX, heightDip * monitor.ScaleY);
        var area = WorkingArea(monitor);
        const double epsilon = .000001;
        if (!footprint.IsUsable || footprint.Left < area.Left - epsilon || footprint.Top < area.Top - epsilon
            || footprint.Right > area.Right + epsilon || footprint.Bottom > area.Bottom + epsilon)
            return CapsuleDockSide.None;
        var side = CapsuleDockSide.None;
        var nearest = double.PositiveInfinity;
        void Consider(CapsuleDockSide candidate, double distance, bool unobstructed)
        {
            if (unobstructed && distance >= -epsilon && distance <= thresholdDip + epsilon
                && distance < nearest - epsilon)
            {
                nearest = distance;
                side = candidate;
            }
        }
        // Top wins equal-distance corners. A sidebar/top taskbar obstructs that actual
        // screen edge even if its inner working-area boundary is within the threshold.
        Consider(CapsuleDockSide.Top, (footprint.Top - bounds.Top) / monitor.ScaleY, area.Top <= bounds.Top + epsilon);
        Consider(CapsuleDockSide.Left, (footprint.Left - bounds.Left) / monitor.ScaleX, area.Left <= bounds.Left + epsilon);
        Consider(CapsuleDockSide.Right, (bounds.Right - footprint.Right) / monitor.ScaleX, area.Right >= bounds.Right - epsilon);
        return side;
    }

    /// <summary>A normal maximized work-area rectangle does not cover a monitor with a visible taskbar.</summary>
    public static bool CoversMonitor(CapsulePhysicalRect window, CapsulePhysicalRect monitor, double tolerancePx = 2)
    {
        if (!window.IsUsable || !monitor.IsUsable || !double.IsFinite(tolerancePx)) return false;
        var tolerance = Math.Clamp(tolerancePx, 0, 8);
        return Math.Abs(window.Left - monitor.Left) <= tolerance && Math.Abs(window.Top - monitor.Top) <= tolerance
            && Math.Abs(window.Right - monitor.Right) <= tolerance && Math.Abs(window.Bottom - monitor.Bottom) <= tolerance;
    }

    static CapsulePhysicalRect WorkingArea(CapsulePlacementMonitor monitor)
    {
        if (!monitor.WorkArea.IsUsable) return monitor.Bounds;
        var left = Math.Max(monitor.Bounds.Left, monitor.WorkArea.Left);
        var top = Math.Max(monitor.Bounds.Top, monitor.WorkArea.Top);
        var right = Math.Min(monitor.Bounds.Right, monitor.WorkArea.Right);
        var bottom = Math.Min(monitor.Bounds.Bottom, monitor.WorkArea.Bottom);
        return right > left && bottom > top ? new(left, top, right - left, bottom - top) : monitor.Bounds;
    }

    static double DistanceTo(CapsulePhysicalRect rect, double x, double y)
    {
        var dx = x < rect.Left ? rect.Left - x : x > rect.Right ? x - rect.Right : 0;
        var dy = y < rect.Top ? rect.Top - y : y > rect.Bottom ? y - rect.Bottom : 0;
        return dx * dx + dy * dy;
    }

    static bool IsPhysicalCoordinate(double value) => double.IsFinite(value) && value >= int.MinValue && value <= int.MaxValue;
    static double Scale(double value) => double.IsFinite(value) && value is >= .5 and <= 8 ? value : 1;
    static double Size(double value, double fallback) => double.IsFinite(value) && value > 0 ? Math.Clamp(value, 1, 8192) : fallback;
    static double SafeMargin(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 128) : 12;
}
