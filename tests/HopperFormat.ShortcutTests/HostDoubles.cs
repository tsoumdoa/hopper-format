// Minimal host contracts for testing the adapters without loading Rhino.
// The plugin itself compiles separately against the real Grasshopper package.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Grasshopper.Kernel
{
    internal enum GH_ProcessStep { PreProcess, Process, PostProcess }
    internal enum GH_ParamKind { input, output, floating }
    internal interface IGH_DocumentObject
    {
        Guid InstanceGuid { get; }
        string Name { get; }
        string NickName { get; }
        TestAttributes Attributes { get; }
        void CreateAttributes();
    }
    internal interface IGH_Param : IGH_DocumentObject
    {
        GH_ParamKind Kind { get; }
        int SourceCount { get; }
        List<IGH_Param> Sources { get; }
        List<IGH_Param> Recipients { get; }
    }
    internal interface IGH_Component : IGH_DocumentObject { TestParams Params { get; } }

    internal sealed class TestAttributes
    {
        internal IGH_DocumentObject DocObject;
        internal TestAttributes Top;
        internal TestAttributes GetTopLevel => Top ?? this;
        internal PointF InputGrip, OutputGrip;
        internal RectangleF Bounds;
        internal int Expirations, Layouts;
        internal Func<PointF, PointF> TransformPivot;
        internal Action OnLayout;
        private PointF _pivot;
        internal PointF Pivot { get => _pivot; set => _pivot = TransformPivot == null ? value : TransformPivot(value); }
        internal void ExpireLayout() => Expirations++;
        internal void PerformLayout() { Layouts++; OnLayout?.Invoke(); }
    }

    internal class TestObject : IGH_DocumentObject
    {
        public Guid InstanceGuid { get; } = Guid.NewGuid();
        public string Name { get; set; } = "Object";
        public string NickName { get; set; } = "Object";
        public TestAttributes Attributes { get; protected set; }
        internal void ClearAttributes() => Attributes = null;
        internal SizeF Size = new SizeF(80, 40);
        internal PointF InitialPivot = new PointF(20, 30);
        internal PointF Offset = new PointF(7, 9);
        internal virtual void Layout()
        {
            var pivot = Attributes.Pivot;
            Attributes.Bounds = new RectangleF(pivot.X - Offset.X, pivot.Y - Offset.Y, Size.Width, Size.Height);
            Attributes.InputGrip = new PointF(Attributes.Bounds.Left - 2, Attributes.Bounds.Top + 11);
            Attributes.OutputGrip = new PointF(Attributes.Bounds.Right + 3, Attributes.Bounds.Top + 13);
        }
        public virtual void CreateAttributes()
        {
            Attributes = new TestAttributes { DocObject = this, Pivot = InitialPivot, OnLayout = Layout };
        }
    }

    internal sealed class TestParams
    {
        internal readonly List<IGH_Param> Input = new List<IGH_Param>();
        internal readonly List<IGH_Param> Output = new List<IGH_Param>();
    }

    internal class TestComponent : TestObject, IGH_Component
    {
        public TestParams Params { get; } = new TestParams();
        internal TestComponent(int inputs = 1, int outputs = 1)
        {
            for (int i = 0; i < inputs; i++) Params.Input.Add(new TestParam { Owner = this, Kind = GH_ParamKind.input });
            for (int i = 0; i < outputs; i++) Params.Output.Add(new TestParam { Owner = this, Kind = GH_ParamKind.output });
        }
        public override void CreateAttributes()
        {
            base.CreateAttributes();
            foreach (var param in Params.Input.Concat(Params.Output).Cast<TestParam>())
            {
                param.CreateAttributes();
                param.Attributes.Top = Attributes;
            }
        }
        internal override void Layout()
        {
            base.Layout();
            for (int i = 0; i < Params.Input.Count; i++)
                Params.Input[i].Attributes.InputGrip = new PointF(Attributes.Bounds.Left - 5, Attributes.Bounds.Top + 10 + 15 * i);
            for (int i = 0; i < Params.Output.Count; i++)
                Params.Output[i].Attributes.OutputGrip = new PointF(Attributes.Bounds.Right + 6, Attributes.Bounds.Top + 12 + 15 * i);
        }
    }

    internal class TestParam : TestObject, IGH_Param
    {
        internal TestComponent Owner;
        public GH_ParamKind Kind { get; set; } = GH_ParamKind.floating;
        public int SourceCount => Sources.Count;
        public List<IGH_Param> Sources { get; } = new List<IGH_Param>();
        public List<IGH_Param> Recipients { get; } = new List<IGH_Param>();
        public override void CreateAttributes()
        {
            // A direct call creates floating attributes, even for component ports.
            base.CreateAttributes();
        }
        internal static void Connect(IGH_Param source, IGH_Param target)
        {
            source.Recipients.Add(target);
            target.Sources.Add(source);
        }
    }

    internal sealed class GH_Document
    {
        internal GH_ProcessStep SolutionState = GH_ProcessStep.PostProcess;
        internal bool FailSelection;
        internal readonly List<IGH_DocumentObject> Objects = new List<IGH_DocumentObject>();
        internal readonly List<IGH_DocumentObject> Selection = new List<IGH_DocumentObject>();
        internal readonly TestUndo UndoUtil = new TestUndo();
        internal List<IGH_DocumentObject> SelectedObjects()
        {
            if (FailSelection) throw new InvalidOperationException("injected selection failure");
            foreach (var group in Objects.OfType<Grasshopper.Kernel.Special.GH_Group>()) group.Document = this;
            return Selection.ToList();
        }
    }
    internal sealed class TestUndoRecord
    {
        internal string Name;
        internal Dictionary<IGH_DocumentObject, PointF> Before;
    }
    internal sealed class TestUndo
    {
        internal int Creates;
        internal bool Reject;
        internal Action OnRecord;
        internal readonly List<TestUndoRecord> Records = new List<TestUndoRecord>();
        internal TestUndoRecord CreatePivotEvent(string name, IEnumerable<IGH_DocumentObject> objects)
        {
            Creates++;
            return new TestUndoRecord { Name = name, Before = objects.ToDictionary(obj => obj, obj => obj.Attributes.Pivot) };
        }
        internal Guid RecordEvent(TestUndoRecord record)
        {
            OnRecord?.Invoke();
            if (Reject) return Guid.Empty;
            Records.Add(record);
            return Guid.NewGuid();
        }
    }
}

namespace Grasshopper.Kernel.Special
{
    internal sealed class GH_Panel : Grasshopper.Kernel.TestParam { }

    internal sealed class GH_Group : Grasshopper.Kernel.TestObject
    {
        internal readonly List<Guid> ObjectIDs = new List<Guid>();
        internal int CacheExpirations;
        internal Grasshopper.Kernel.GH_Document Document;
        internal float PadLeft = 10, PadTop = 20, PadRight = 12, PadBottom = 14;
        internal override void Layout()
        {
            if (Document == null) { base.Layout(); return; }
            var members = Document.Objects.Where(o => ObjectIDs.Contains(o.InstanceGuid) && o.Attributes != null).ToArray();
            if (members.Length == 0) { Attributes.Bounds = RectangleF.Empty; return; }
            var bounds = members.Select(o => o.Attributes.Bounds).Aggregate(RectangleF.Union);
            Attributes.Bounds = RectangleF.FromLTRB(bounds.Left - PadLeft, bounds.Top - PadTop,
                bounds.Right + PadRight, bounds.Bottom + PadBottom);
        }
        internal void ExpireCaches() => CacheExpirations++;
    }
}

namespace Grasshopper
{
    internal static class Instances
    {
        internal static int Redraws;
        internal static Grasshopper.GUI.Canvas.GH_Canvas ActiveCanvas;
        internal static event Action<Grasshopper.GUI.Canvas.GH_Canvas> CanvasCreated;
        internal static event Action<Grasshopper.GUI.Canvas.GH_Canvas> CanvasDestroyed;
        internal static void ChangeCanvas(Grasshopper.GUI.Canvas.GH_Canvas canvas)
        {
            var previous = ActiveCanvas;
            ActiveCanvas = canvas;
            if (previous != null) CanvasDestroyed?.Invoke(previous);
            if (canvas != null) CanvasCreated?.Invoke(canvas);
        }
        internal static void RedrawCanvas() => Redraws++;
    }
}

namespace HopperFormat
{
    internal sealed class HopperFormatComponent : Grasshopper.Kernel.TestComponent
    {
        internal bool SessionEligible = true;
        internal int OutputRefreshes;
        internal void ScheduleOutputRefresh() => OutputRefreshes++;
    }
}

namespace Grasshopper.GUI.Canvas
{
    internal sealed class GH_CanvasDocumentChangedEventArgs : EventArgs { }
    internal sealed class GH_Canvas : System.Windows.Forms.Control
    {
        internal static System.Windows.Forms.Keys NavigationPanLeft = System.Windows.Forms.Keys.None;
        internal static System.Windows.Forms.Keys NavigationPanRight = System.Windows.Forms.Keys.None;
        internal static System.Windows.Forms.Keys NavigationPanUp = System.Windows.Forms.Keys.None;
        internal static System.Windows.Forms.Keys NavigationPanDown = System.Windows.Forms.Keys.None;
        internal static System.Windows.Forms.Keys NavigationZoomIn = System.Windows.Forms.Keys.None;
        internal static System.Windows.Forms.Keys NavigationZoomOut = System.Windows.Forms.Keys.None;
        internal bool ModifiersEnabled = true, IsActiveInteraction, IsActiveWidget, IsActiveObject;
        internal Graphics Graphics;
        internal int Invalidations;
        protected override void OnInvalidated(System.Windows.Forms.InvalidateEventArgs e)
        {
            Invalidations++;
            base.OnInvalidated(e);
        }
        internal event Action<GH_Canvas> CanvasPostPaintOverlay;
        internal event EventHandler<GH_CanvasDocumentChangedEventArgs> DocumentChanged;
        private Grasshopper.Kernel.GH_Document _document;
        internal Grasshopper.Kernel.GH_Document Document
        {
            get => _document;
            set { _document = value; DocumentChanged?.Invoke(this, new GH_CanvasDocumentChangedEventArgs()); }
        }
        internal void PaintOverlay(Graphics graphics)
        {
            Graphics = graphics;
            try { CanvasPostPaintOverlay?.Invoke(this); }
            finally { Graphics = null; }
        }
    }
}
