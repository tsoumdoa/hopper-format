using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Grasshopper.Kernel;
using HopperFormat;

// Compile the actual component against a separate, minimal host so the existing
// session tests can retain their counting owner double and unchanged assertions.
public static class ComponentRegressionTests
{
    public static int Run()
    {
        int failures = 0;
        Check("Component dropped refresh followed by normal solve permits a new refresh", () =>
        {
            var document = new GH_Document();
            var component = new Component();
            component.AddedToDocument(document);
            Assert(document.Scheduled.Count == 1, "setup did not schedule");
            document.Scheduled.Clear(); // Grasshopper dropped this callback.
            component.Solve(new DataAccess());
            component.Session.Status = "Changed after normal solve";
            component.ScheduleOutputRefresh();
            Assert(document.Scheduled.Count == 1, "dropped callback suppressed later refresh");
            document.Scheduled[0](document);
            Assert(component.Expirations == 1, "next callback did not expire outputs");
            component.RemovedFromDocument(document);
        });
        Check("Component stale refresh callback preserves the version guard", () =>
        {
            var document = new GH_Document();
            var component = new Component();
            component.AddedToDocument(document);
            var stale = document.Scheduled[0];
            component.ReleaseSession();
            component.AddedToDocument(document);
            component.Session.Status = "New session";
            stale(document);
            component.ScheduleOutputRefresh();
            Assert(component.Expirations == 0 && document.Scheduled.Count == 2,
                "stale callback expired outputs or cleared the new pending callback");
            document.Scheduled[1](document);
            Assert(component.Expirations == 1, "current version callback did not refresh");
            component.RemovedFromDocument(document);
        });
        Check("Component and assembly icons share one thread-safe cached bitmap", () =>
        {
            var icons = new Bitmap[32];
            Parallel.For(0, icons.Length, i => icons[i] = HopperFormatInfo.LoadIcon());
            Assert(icons[0] != null && Array.TrueForAll(icons, icon => ReferenceEquals(icon, icons[0]))
                && ReferenceEquals(new Component().Image, icons[0])
                && ReferenceEquals(new HopperFormatInfo().Icon, icons[0]), "icon decoded more than once");
        });
        Check("Component menu exposes Format all groups and routes it separately from Format selection", () =>
        {
            var component = new Component(); var document = new GH_Document(); component.AddedToDocument(document);
            using var menu = new ToolStripDropDown(); component.Menu(menu);
            Assert(menu.Items.Count == 2 && menu.Items[0].Text == "Format selection" && menu.Items[1].Text == "Format all groups"
                && menu.Items[1].Enabled, "menu entry missing or disabled");
            menu.Items[1].PerformClick();
            Assert(component.Session.AllRuns == 1 && component.Session.SelectionRuns == 0, "all menu routed to selection");
            menu.Items[0].PerformClick();
            Assert(component.Session.SelectionRuns == 1 && component.Session.AllRuns == 1, "selection menu changed");
            component.RemovedFromDocument(document);
        });
        return failures;

        void Check(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Component : HopperFormatComponent
    {
        internal FormatSession Session => (FormatSession)typeof(HopperFormatComponent)
            .GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(this);
        internal void Solve(IGH_DataAccess data) => SolveInstance(data);
        internal Bitmap Image => Icon;
        internal void Menu(ToolStripDropDown menu) => AppendAdditionalComponentMenuItems(menu);
    }

    private sealed class DataAccess : IGH_DataAccess
    {
        public void GetData<T>(int index, ref T value) { }
        public void SetData(int index, object value) { }
        public void SetDataList(int index, IEnumerable values) { }
    }
}

namespace HopperFormat
{
    internal sealed class FormatSession : IDisposable
    {
        internal string Status = "Select components to format";
        internal int LastMoves => 0;
        internal IEnumerable<string> Log(bool debug) => Array.Empty<string>();
        internal void Guard(string where, Action action) => action();
        internal void Configure(HopperFormatComponent owner, GH_Document document, Layout.Options options) { }
        internal int SelectionRuns, AllRuns;
        internal void Run() => SelectionRuns++;
        internal void RunAll() => AllRuns++;
        public void Dispose() { }
    }

    internal static class DocumentSessionRegistry
    {
        internal static void Configure(HopperFormatComponent component, GH_Document document) => component.ApplySession(document, true, false);
        internal static void Remove(HopperFormatComponent component, GH_Document document) => component.ReleaseSession();
        internal static void Close(GH_Document document) { }
    }
}

namespace Grasshopper.Kernel
{
    public enum GH_ParamAccess { item, list }
    public enum GH_RuntimeMessageLevel { Remark }
    public enum GH_DocumentContext { Unloaded, Close, Loaded }
    public enum GH_ObjectEventType { Enabled }
    public interface IGH_DocumentObject { }
    public sealed class GH_ObjectChangedEventArgs : EventArgs { public GH_ObjectEventType Type; }
    public interface IGH_DataAccess
    {
        void GetData<T>(int index, ref T value);
        void SetData(int index, object value);
        void SetDataList(int index, IEnumerable values);
    }
    public sealed class GH_Document
    {
        public readonly List<Action<GH_Document>> Scheduled = new List<Action<GH_Document>>();
        public void ScheduleSolution(int delay, Action<GH_Document> callback) => Scheduled.Add(callback);
    }
    public abstract class GH_Component
    {
        private GH_Document _document;
        public bool Locked { get; set; }
        public int Expirations { get; private set; }
        public event Action<IGH_DocumentObject, GH_ObjectChangedEventArgs> ObjectChanged { add { } remove { } }
        protected GH_Component(string name, string nickname, string description, string category, string subcategory) { }
        public GH_Document OnPingDocument() => _document;
        public void ExpireSolution(bool recompute) => Expirations++;
        public void AddRuntimeMessage(GH_RuntimeMessageLevel level, string text) { }
        public void ClearRuntimeMessages() { }
        public virtual void AddedToDocument(GH_Document document) => _document = document;
        public virtual void RemovedFromDocument(GH_Document document) => _document = null;
        public virtual void DocumentContextChanged(GH_Document document, GH_DocumentContext context) { }
        protected abstract void RegisterInputParams(GH_InputParamManager manager);
        protected abstract void RegisterOutputParams(GH_OutputParamManager manager);
        protected abstract void SolveInstance(IGH_DataAccess data);
        protected virtual void AppendAdditionalComponentMenuItems(ToolStripDropDown menu) { }
        protected static void Menu_AppendItem(ToolStripDropDown menu, string text, EventHandler handler, bool enabled) =>
            menu.Items.Add(new ToolStripMenuItem(text, null, handler) { Enabled = enabled });
        protected abstract Bitmap Icon { get; }
        public abstract Guid ComponentGuid { get; }
        public sealed class GH_InputParamManager
        {
            public void AddBooleanParameter(string name, string nickname, string description, GH_ParamAccess access, bool value) { }
            public void AddNumberParameter(string name, string nickname, string description, GH_ParamAccess access, double value) { }
        }
        public sealed class GH_OutputParamManager
        {
            public void AddTextParameter(string name, string nickname, string description, GH_ParamAccess access) { }
            public void AddIntegerParameter(string name, string nickname, string description, GH_ParamAccess access) { }
        }
    }
    public abstract class GH_AssemblyInfo
    {
        public abstract string Name { get; }
        public abstract Bitmap Icon { get; }
        public abstract Guid Id { get; }
        public abstract string Description { get; }
        public abstract string AuthorName { get; }
        public abstract string AuthorContact { get; }
        public abstract string AssemblyVersion { get; }
    }
}
