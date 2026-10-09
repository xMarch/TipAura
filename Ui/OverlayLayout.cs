// Positions of the floating windows, ported from yaaft's OverlayData. A placement's X/Y are an offset
// from one of nine anchors (row-major 3x3) of a reference rectangle: the hooked window's client area,
// or the primary work area while no hook is active. The anchor aligns the matching point of the
// overlay with that of the reference, so a top-right overlay at offset (0, 0) sits in its corner.
internal static class OverlayLayout
{
    internal static Vector2i AnchorOrigin(int anchor, Vector2i size, PixelRect bounds)
    {
        int column = anchor % 3, row = anchor / 3;
        // Use the same integer rounding in both forward and inverse conversions.
        return new Vector2i(bounds.Left + (int)Math.Floor((bounds.Width - size.X) * column / 2.0),
            bounds.Top + (int)Math.Floor((bounds.Height - size.Y) * row / 2.0));
    }

    private static Vector2i Size(OverlayPlacement placement) => new(placement.Width, placement.Height);

    // Screen position, kept inside the work area the overlay overlaps most.
    internal static Vector2i Position(OverlayPlacement placement, OverlayPlacement defaults, PixelRect bounds,
        IReadOnlyList<PixelRect> workAreas)
    {
        var origin = AnchorOrigin(placement.Anchor, Size(placement), bounds);
        long x = (long)origin.X + (placement.X ?? defaults.DefaultOffset.X);
        long y = (long)origin.Y + (placement.Y ?? defaults.DefaultOffset.Y);
        if (workAreas.Count == 0) return new Vector2i((int)x, (int)y);
        PixelRect best = workAreas[0];
        long largest = -1;
        foreach (var area in workAreas)
        {
            long overlap = Math.Max(0, Math.Min(x + placement.Width, area.Right) - Math.Max(x, area.Left))
                * Math.Max(0, Math.Min(y + placement.Height, area.Bottom) - Math.Max(y, area.Top));
            if (overlap > largest) { largest = overlap; best = area; }
        }
        if (largest == 0)
            best = workAreas.OrderByDescending(area =>
                Math.Max(0, Math.Min(bounds.Right, area.Right) - Math.Max(bounds.Left, area.Left))
                * (long)Math.Max(0, Math.Min(bounds.Bottom, area.Bottom) - Math.Max(bounds.Top, area.Top))).First();
        return new Vector2i(
            (int)Math.Clamp(x, best.Left, Math.Max(best.Left, (long)best.Right - placement.Width)),
            (int)Math.Clamp(y, best.Top, Math.Max(best.Top, (long)best.Bottom - placement.Height)));
    }

    // Stores a screen position (after a drag or resize) as the offset from the current anchor.
    internal static OverlayPlacement RememberPosition(OverlayPlacement placement, PixelRect bounds, Vector2i position)
    {
        var origin = AnchorOrigin(placement.Anchor, Size(placement), bounds);
        return placement with { X = position.X - origin.X, Y = position.Y - origin.Y };
    }

    // Switching anchors keeps the overlay where it is.
    internal static OverlayPlacement ChangeAnchor(OverlayPlacement placement, PixelRect bounds, int anchor, Vector2i position) =>
        RememberPosition(placement with { Anchor = anchor }, bounds, position);

    // Settings written before anchors existed hold absolute screen coordinates and no anchor. They become
    // offsets from the top-left of the primary work area, which is the reference while no hook is active,
    // so the overlay stays where it was.
    internal static OverlayPlacement MigrateLegacy(OverlayPlacement placement, OverlayPlacement defaults, PixelRect primaryWork)
    {
        if (placement.Anchor >= 0) return placement;
        if (placement.X is not int x || placement.Y is not int y) return placement with { Anchor = defaults.Anchor, X = null, Y = null };
        return placement with { Anchor = 0, X = x - primaryWork.Left, Y = y - primaryWork.Top };
    }
}
