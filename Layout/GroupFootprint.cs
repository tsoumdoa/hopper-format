using System;

namespace HopperFormat.Layout
{
    public static class GroupFootprint
    {
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
