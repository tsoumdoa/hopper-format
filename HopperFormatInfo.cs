using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace HopperFormat
{
    public sealed class HopperFormatInfo : GH_AssemblyInfo
    {
        public override string Name => "HopperFormat";
        public override Bitmap Icon => LoadIcon();
        public override Guid Id => new Guid("67cb28c8-cf29-4b6c-9c1c-030b7687e4c7");
        public override string Description => "Format selected Grasshopper components as a left-to-right dataflow";
        public override string AuthorName => "AEC Tooling";
        public override string AuthorContact => string.Empty;
        public override string AssemblyVersion => GetType().Assembly.GetName().Version.ToString();

        internal static Bitmap LoadIcon()
        {
            using (var stream = typeof(HopperFormatInfo).Assembly.GetManifestResourceStream("HopperFormat.icon.png"))
            {
                if (stream == null) return null;
                using (var bitmap = new Bitmap(stream)) return new Bitmap(bitmap);
            }
        }
    }
}
