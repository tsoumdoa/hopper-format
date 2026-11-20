using System;

namespace HopperFormat.Layout
{
    public static class GroupFootprint
    {
        internal static Padding Margins(Box box, Box footprint) => new Padding(
            Math.Max(0, box.X - footprint.X), Math.Max(0, box.Y - footprint.Y),
            Math.Max(0, footprint.Right - box.Right), Math.Max(0, footprint.Bottom - box.Bottom));

        internal static Box Expand(Box box, Padding reserve) => new Box(
            box.X - reserve.Left, box.Y - reserve.Top,
            box.Width + reserve.Left + reserve.Right, box.Height + reserve.Top + reserve.Bottom);

        // TagSize includes the balloon's pointer strip. The raw box stays intact.
        public static Box IncludeTag(Box box, Vec2 tagSize)
        {
            if (tagSize.X <= 0 || tagSize.Y <= 0) return box;
            return Union(box, new Box(box.X + (box.Width - tagSize.X) / 2,
                box.Y - tagSize.Y, tagSize.X, tagSize.Y));
        }

        public static Box Union(Box a, Box b)
        {
            double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            return new Box(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
        }
    }
}
