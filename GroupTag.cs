using System;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using HopperFormat.Layout;

namespace HopperFormat
{
    internal static class GroupTag
    {
        internal static Vec2 Measure(GH_Group group)
        {
            if (string.IsNullOrWhiteSpace(group.NickName)) return default;
            var size = GH_FontServer.MeasureString(group.NickName, GH_FontServer.StandardAdjusted);
            return new Vec2(Math.Ceiling(size.Width + 6.0), Math.Ceiling(size.Height + 2.0) + 6);
        }
    }
}
