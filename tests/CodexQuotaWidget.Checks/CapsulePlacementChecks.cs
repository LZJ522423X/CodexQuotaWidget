using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Pure synthetic monitor geometry; these checks do not change display settings or move real windows.</summary>
public static class CapsulePlacementChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Capsule placement: " + name); count++; }
        bool Near(double a, double b) => Math.Abs(a - b) < .0001;
        var primary = new CapsulePlacementMonitor("PRIMARY", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040));
        var left = new CapsulePlacementMonitor("LEFT", new(-2560, -200, 2560, 1440), new(-2560, -200, 2560, 1400), 1.5, 1.5);
        var upper = new CapsulePlacementMonitor("UPPER", new(0, -2160, 3840, 2160), new(0, -2160, 3840, 2120), 2, 2);
        var monitors = new[] { primary, left, upper };
        var first = CapsulePlacement.Restore(monitors, null, null, null, 400, 56)!.Value;
        Check(first.MonitorDevice == "PRIMARY" && Near(first.CenterX, 960) && Near(first.TopY, 12), "fresh settings choose primary top center");
        Check(Near(first.Width, 400) && Near(first.Height, 56), "100 percent monitor uses DIP dimensions directly");
        var leftResult = CapsulePlacement.Restore(monitors, "left", -1400, -150, 400, 56)!.Value;
        Check(leftResult.MonitorDevice == "LEFT" && Near(leftResult.CenterX, -1400) && Near(leftResult.TopY, -150), "negative coordinates and case-insensitive saved device survive");
        Check(Near(leftResult.Width, 600) && Near(leftResult.Height, 84) && Near(leftResult.ScaleX, 1.5), "mixed DPI uses the target monitor scale");
        var upperResult = CapsulePlacement.Restore(monitors, "UPPER", 1800, -2100, 520, 166)!.Value;
        Check(Near(upperResult.Width, 1040) && Near(upperResult.Height, 332) && upperResult.TopY < 0, "upper display retains negative Y and 200 percent dimensions");
        var removed = CapsulePlacement.Restore(new[] { primary, upper }, "LEFT", -1400, -150, 400, 56)!.Value;
        Check(removed.MonitorDevice == "UPPER" && removed.Left >= 24 && removed.TopY >= -2136
            && removed.Left + removed.Width <= 3816 && removed.TopY + removed.Height <= -64,
            "unplugged saved monitor selects nearest remaining screen and clamps");
        var pointSelection = CapsulePlacement.Restore(monitors, null, -1400, -150, 400, 56)!.Value;
        Check(pointSelection.MonitorDevice == "LEFT", "coordinates select a monitor when no saved device exists");
        var preferred = CapsulePlacement.Restore(monitors, "PRIMARY", -1400, -150, 400, 56)!.Value;
        Check(preferred.MonitorDevice == "PRIMARY", "existing saved device is authoritative after geometry changes");
        var offscreen = CapsulePlacement.Restore(monitors, "PRIMARY", 100000, 100000, 520, 166)!.Value;
        Check(Near(offscreen.Left + offscreen.Width, 1908) && Near(offscreen.TopY + offscreen.Height, 1028), "offscreen stale location stays inside working area and taskbar boundary");
        var leftEdge = CapsulePlacement.Clamp(left, -2550, -500, 520, 166)!.Value;
        Check(Near(leftEdge.Left, -2542) && Near(leftEdge.TopY, -182), "negative monitor edges use DPI-aware margins");
        var collapsed = CapsulePlacement.Clamp(primary, 1860, 30, 400, 56)!.Value;
        var expanded = CapsulePlacement.Clamp(primary, 1860, 30, 520, 166)!.Value;
        Check(collapsed.Left + collapsed.Width <= 1908 && expanded.Left + expanded.Width <= 1908, "expanded geometry is separately clamped near an edge");
        var stableCollapsed = CapsulePlacement.Clamp(primary, 960, 30, 400, 56)!.Value;
        var stableExpanded = CapsulePlacement.Clamp(primary, 960, 30, 520, 166)!.Value;
        Check(Near(stableCollapsed.CenterX, stableExpanded.CenterX) && Near(stableCollapsed.TopY, stableExpanded.TopY), "normal expansion keeps a single physical center/top anchor");
        var invalidPoint = CapsulePlacement.Restore(monitors, null, double.NaN, double.PositiveInfinity, 400, 56)!.Value;
        Check(invalidPoint.MonitorDevice == "PRIMARY" && Near(invalidPoint.CenterX, 960) && Near(invalidPoint.TopY, 12), "nonfinite saved coordinates never propagate");
        var absurdPoint = CapsulePlacement.Restore(monitors, null, double.MaxValue, double.MinValue, 400, 56)!.Value;
        Check(absurdPoint.MonitorDevice == "PRIMARY" && Near(absurdPoint.CenterX, 960), "coordinates outside native integer range reset safely");
        var badScale = primary with { ScaleX = double.NaN, ScaleY = double.PositiveInfinity };
        var badScaleResult = CapsulePlacement.Clamp(badScale, double.NaN, double.NaN, double.NaN, -1, double.NaN)!.Value;
        Check(Near(badScaleResult.ScaleX, 1) && Near(badScaleResult.ScaleY, 1) && Near(badScaleResult.Width, 400) && Near(badScaleResult.Height, 56), "invalid scale and dimensions have finite defaults");
        Check(Near(badScaleResult.CenterX, 960) && Near(badScaleResult.TopY, 12), "invalid clamp coordinates use finite working-area defaults");
        var tiny = new CapsulePlacementMonitor("TINY", new(0, 0, 180, 90), new(0, 0, 180, 60), 2, 2);
        var tinyResult = CapsulePlacement.Clamp(tiny, 90, 15, 520, 166)!.Value;
        Check(tinyResult.Width > 0 && tinyResult.Height > 0 && tinyResult.Left >= 0 && tinyResult.TopY >= 0
            && tinyResult.Left + tinyResult.Width <= 180 && tinyResult.TopY + tinyResult.Height <= 60, "oversized capsule remains visible on very small working area");
        var noWorkArea = CapsulePlacement.Clamp(primary with { WorkArea = new(0, 0, 0, 0) }, 960, 2000, 400, 56)!.Value;
        Check(noWorkArea.TopY + noWorkArea.Height <= 1068, "invalid working area falls back to real monitor bounds");
        var staleWorkArea = CapsulePlacement.Clamp(primary with { WorkArea = new(-50, -50, 2200, 1400) }, 4000, 4000, 400, 56)!.Value;
        Check(staleWorkArea.Left + staleWorkArea.Width <= 1908 && staleWorkArea.TopY + staleWorkArea.Height <= 1068, "working area is intersected with actual bounds");
        var wrongWorkArea = CapsulePlacement.Clamp(primary with { WorkArea = new(5000, 5000, 100, 100) }, 960, 12, 400, 56)!.Value;
        Check(Near(wrongWorkArea.CenterX, 960), "nonoverlapping stale work area cannot relocate to a phantom screen");
        var topTaskbar = primary with { WorkArea = new(40, 40, 1880, 1040) };
        var taskbarResult = CapsulePlacement.Clamp(topTaskbar, 0, 0, 400, 56)!.Value;
        Check(Near(taskbarResult.Left, 52) && Near(taskbarResult.TopY, 52), "left and top taskbars are honored");
        Check(CapsulePlacement.Restore(Array.Empty<CapsulePlacementMonitor>(), null, null, null, 400, 56) is null, "no monitors yields no invented placement");
        Check(CapsulePlacement.Clamp(primary with { Bounds = new(0, 0, 0, 1080) }, 100, 100, 400, 56) is null, "invalid monitor rejects clamp");
        Check(!new CapsulePhysicalRect(double.NaN, 0, 100, 100).IsUsable && !new CapsulePhysicalRect(0, 0, -1, 100).IsUsable, "invalid rectangle is rejected");
        Check(!new CapsulePhysicalRect(int.MaxValue, 0, 100, 100).IsUsable, "native-coordinate overflow is rejected");
        Check(CapsulePlacement.CoversMonitor(primary.Bounds, primary.Bounds), "exact fullscreen physical bounds match");
        Check(!CapsulePlacement.CoversMonitor(primary.WorkArea, primary.Bounds), "ordinary maximized taskbar working area is not fullscreen");
        Check(CapsulePlacement.CoversMonitor(new(-1, -1, 1922, 1082), primary.Bounds), "small native frame rounding is accepted");
        Check(!CapsulePlacement.CoversMonitor(new(-100, -100, 2120, 1280), primary.Bounds), "oversized or multi-screen coverage does not falsely match fullscreen");
        Check(!CapsulePlacement.CoversMonitor(left.Bounds, primary.Bounds), "fullscreen on a different monitor is not classified on the primary");
        Check(!CapsulePlacement.CoversMonitor(primary.Bounds, primary.Bounds, double.NaN), "invalid fullscreen tolerance is rejected");
        var settings = new Settings { CapsuleCenterX = double.NaN, CapsuleTopY = double.NegativeInfinity, CapsuleMonitorDevice = "screen\nsecret" };
        settings.Validate();
        Check(settings.CapsuleCenterX is null && settings.CapsuleTopY is null && settings.CapsuleMonitorDevice is null, "settings discard malformed physical coordinates/device name");
        var validSettings = new Settings { CapsuleCenterX = -1400, CapsuleTopY = -150, CapsuleMonitorDevice = @"\\.\DISPLAY2", Left = 420, Top = 310 };
        validSettings.Validate();
        Check(validSettings.CapsuleCenterX == -1400 && validSettings.CapsuleTopY == -150 && validSettings.CapsuleMonitorDevice == @"\\.\DISPLAY2", "negative capsule settings remain valid");
        Check(validSettings.Left == 420 && validSettings.Top == 310, "capsule persistence never reuses full-window placement");
        Check(CapsulePlacement.GetDockSide(primary, 960, 400) == CapsuleDockSide.None,
            "a normal middle position never docks merely because the desktop or browser is foreground");
        Check(CapsulePlacement.GetDockSide(primary, 960, 874) == CapsuleDockSide.None,
            "a footprint touching the bottom working-area edge has no bottom docking direction");
        Check(CapsulePlacement.GetDockSide(primary, 960, 10) == CapsuleDockSide.Top,
            "a top-center footprint ten DIPs from the real screen edge docks upward");
        Check(CapsulePlacement.GetDockSide(primary, 270, 400) == CapsuleDockSide.Left,
            "an expanded footprint ten DIPs from the left edge docks left");
        Check(CapsulePlacement.GetDockSide(primary, 1650, 400) == CapsuleDockSide.Right,
            "an expanded footprint ten DIPs from the right edge docks right");
        Check(CapsulePlacement.GetDockSide(primary, 270, 10) == CapsuleDockSide.Top,
            "a same-distance top-left corner chooses Top");
        Check(CapsulePlacement.GetDockSide(primary, 1650, 10) == CapsuleDockSide.Top,
            "a same-distance top-right corner chooses Top");
        Check(CapsulePlacement.GetDockSide(primary, 265, 20) == CapsuleDockSide.Left,
            "a nearer left edge wins over a farther top edge at a corner");
        Check(CapsulePlacement.GetDockSide(primary, 1655, 20) == CapsuleDockSide.Right,
            "a nearer right edge wins over a farther top edge at a corner");
        Check(CapsulePlacement.GetDockSide(primary, 960, 24) == CapsuleDockSide.Top,
            "the configured 24-DIP proximity boundary is included");
        Check(CapsulePlacement.GetDockSide(primary, 960, 25) == CapsuleDockSide.None,
            "a position just beyond the proximity threshold stays where it is");
        Check(CapsulePlacement.GetDockSide(left, -1280, -185) == CapsuleDockSide.Top,
            "150-percent DPI on a negative-coordinate monitor uses a 15-pixel ten-DIP top gap");
        Check(CapsulePlacement.GetDockSide(left, -2155, 300) == CapsuleDockSide.Left,
            "150-percent DPI and negative X preserve a left docking direction");
        Check(CapsulePlacement.GetDockSide(left, -405, 300) == CapsuleDockSide.Right,
            "a monitor ending at desktop X zero still recognizes its right edge");
        Check(CapsulePlacement.GetDockSide(upper, 1920, -2140) == CapsuleDockSide.Top,
            "200-percent DPI above the primary uses the target monitor's physical scale");
        Check(CapsulePlacement.GetDockSide(upper, 540, -1500) == CapsuleDockSide.Left,
            "200-percent DPI expands the reserved width before measuring the left edge");
        Check(CapsulePlacement.GetDockSide(upper, 3300, -1500) == CapsuleDockSide.Right,
            "200-percent DPI expands the reserved width before measuring the right edge");
        Check(CapsulePlacement.GetDockSide(upper, 1920, -2112) == CapsuleDockSide.Top
            && CapsulePlacement.GetDockSide(upper, 1920, -2110) == CapsuleDockSide.None,
            "48 physical pixels match a 24-DIP threshold at 200-percent DPI, while 50 do not");
        var asymmetric = primary with { ScaleX = 1.5, ScaleY = 2 };
        Check(CapsulePlacement.GetDockSide(asymmetric, 414, 30) == CapsuleDockSide.Top,
            "different X/Y scales compare DIP distances rather than choosing the smaller raw pixel gap");
        Check(CapsulePlacement.GetDockSide(primary, 210, 400, 400, 56) == CapsuleDockSide.Left,
            "explicit alternate footprint dimensions are respected without modifying placement");
        Check(CapsulePlacement.GetDockSide(primary, 960, 984, 400, 56) == CapsuleDockSide.None,
            "even a small compact footprint near the bottom has no bottom dock");
        Check(CapsulePlacement.GetDockSide(primary, 960, 0, thresholdDip: 0) == CapsuleDockSide.Top,
            "zero proximity requires an exact unobstructed real edge");
        Check(CapsulePlacement.GetDockSide(primary, 960, 1, thresholdDip: 0) == CapsuleDockSide.None,
            "zero proximity does not silently add a dock margin");
        var narrowTopTaskbar = primary with { WorkArea = new(0, 12, 1920, 1028) };
        Check(CapsulePlacement.GetDockSide(narrowTopTaskbar, 960, 12) == CapsuleDockSide.None,
            "a top taskbar blocks docking across it even when its inner edge is close to the real screen edge");
        var narrowLeftTaskbar = primary with { WorkArea = new(12, 0, 1908, 1040) };
        Check(CapsulePlacement.GetDockSide(narrowLeftTaskbar, 272, 400) == CapsuleDockSide.None,
            "a left taskbar's working-area boundary is never treated as a screen dock edge");
        var narrowRightTaskbar = primary with { WorkArea = new(0, 0, 1908, 1040) };
        Check(CapsulePlacement.GetDockSide(narrowRightTaskbar, 1648, 400) == CapsuleDockSide.None,
            "a right taskbar blocks only that physical docking side");
        Check(CapsulePlacement.GetDockSide(narrowTopTaskbar, 270, 100) == CapsuleDockSide.Left,
            "an obstructed top does not disable a separate unobstructed left edge");
        Check(CapsulePlacement.GetDockSide(primary, 0, 400) == CapsuleDockSide.None
            && CapsulePlacement.GetDockSide(primary, 960, -1) == CapsuleDockSide.None,
            "offscreen footprints are rejected instead of being moved or automatically docked");
        Check(CapsulePlacement.GetDockSide(primary, 960, 1000) == CapsuleDockSide.None,
            "the full height must fit so a mostly offscreen footprint is not accepted");
        Check(CapsulePlacement.GetDockSide(primary, double.NaN, 10) == CapsuleDockSide.None
            && CapsulePlacement.GetDockSide(primary, 960, double.PositiveInfinity) == CapsuleDockSide.None,
            "nonfinite saved positions never produce a dock direction");
        Check(CapsulePlacement.GetDockSide(primary, double.MaxValue, 10) == CapsuleDockSide.None,
            "native-coordinate overflow cannot masquerade as a nearby edge");
        Check(CapsulePlacement.GetDockSide(primary, 960, 10, double.NaN, 166) == CapsuleDockSide.None
            && CapsulePlacement.GetDockSide(primary, 960, 10, 520, double.PositiveInfinity) == CapsuleDockSide.None,
            "nonfinite footprint dimensions are rejected");
        Check(CapsulePlacement.GetDockSide(primary, 960, 10, 0, 166) == CapsuleDockSide.None
            && CapsulePlacement.GetDockSide(primary, 960, 10, 520, 0) == CapsuleDockSide.None
            && CapsulePlacement.GetDockSide(primary, 960, 10, 520, -1) == CapsuleDockSide.None,
            "both positive width and height are required");
        Check(CapsulePlacement.GetDockSide(primary, 960, 10, 520, 2000) == CapsuleDockSide.None,
            "an oversized footprint is not resized by the direction-only calculation");
        Check(CapsulePlacement.GetDockSide(primary, 960, 10, thresholdDip: double.NaN) == CapsuleDockSide.None
            && CapsulePlacement.GetDockSide(primary, 960, 10, thresholdDip: double.PositiveInfinity) == CapsuleDockSide.None
            && CapsulePlacement.GetDockSide(primary, 960, 10, thresholdDip: -1) == CapsuleDockSide.None,
            "invalid or negative proximity thresholds are rejected");
        Check(CapsulePlacement.GetDockSide(primary with { ScaleX = double.NaN }, 960, 10) == CapsuleDockSide.None
            && CapsulePlacement.GetDockSide(primary with { ScaleY = 0 }, 960, 10) == CapsuleDockSide.None,
            "unknown target DPI does not silently guess a docking direction");
        Check(CapsulePlacement.GetDockSide(primary with { Bounds = new(0, 0, 0, 1080) }, 960, 10) == CapsuleDockSide.None,
            "invalid monitor bounds produce no dock direction");
        Check(CapsulePlacement.GetDockSide(primary with { WorkArea = new(0, 0, 0, 0) }, 960, 10) == CapsuleDockSide.None
            && CapsulePlacement.GetDockSide(primary with { WorkArea = new(5000, 5000, 100, 100) }, 960, 10) == CapsuleDockSide.None,
            "invalid or disconnected working-area data cannot allow docking across an unknown obstruction");
        return count;
    }
}
