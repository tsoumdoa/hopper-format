using System;
using System.Drawing;
using System.Windows.Forms;
using Grasshopper.Kernel;
using HopperFormat.Layout;

namespace HopperFormat
{
    public class HopperFormatComponent : GH_Component
    {
        private readonly FormatSession _session = new FormatSession();
        private bool _enabled = true, _debug, _contextUnloaded, _active, _inactive;
        private bool _refreshingOutputs, _outputRefreshPending;
        private int _outputRefreshVersion;
        private GH_Document _sessionDocument;
        private Options _options = new Options();
        private Tuple<string, int, string> _lastOutputValues;

        internal bool SessionEligible => _enabled && !Locked && !_contextUnloaded;

        public HopperFormatComponent() : base("Hopper Format", "HopperFormat",
            "Format selected components and floating parameters as a left-to-right dataflow. Tap F twice or use Format selection in the menu.",
            "Params", "Util")
        {
            ObjectChanged += OnComponentChanged;
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Enabled", "Enabled", "Enable format selection and the FF shortcut.", GH_ParamAccess.item, true);
            pManager.AddNumberParameter("Gap X", "Gap X", "Horizontal gap between columns in canvas units.", GH_ParamAccess.item, 30);
            pManager.AddNumberParameter("Gap Y", "Gap Y", "Vertical gap between nodes in canvas units.", GH_ParamAccess.item, 16);
            pManager.AddNumberParameter("Stack Gap", "Stack Gap", "Gap between widgets feeding the same consumer.", GH_ParamAccess.item, 6);
            pManager.AddBooleanParameter("Debug", "Debug", "Include solver details in Log. Skipped objects and errors are always reported.", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Status", "Status", "Current formatter status.", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Moves", "Moves", "Number of objects moved by the last run.", GH_ParamAccess.item);
            pManager.AddTextParameter("Log", "Log", "Last run diagnostics and skipped objects; solver details when Debug is enabled.", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA) => _session.Guard("SolveInstance", () =>
        {
            _outputRefreshPending = false;
            double gapX = 30, gapY = 16, stackGap = 6;
            DA.GetData(0, ref _enabled);
            DA.GetData(1, ref gapX);
            DA.GetData(2, ref gapY);
            DA.GetData(3, ref stackGap);
            DA.GetData(4, ref _debug);
            _options = new Options { GapX = gapX, GapY = gapY, StackGap = stackGap };
            var document = OnPingDocument();
            if (document == null)
            {
                DA.SetData(0, "No document");
                DA.SetData(1, 0);
                DA.SetDataList(2, Array.Empty<string>());
                return;
            }
            ConfigureSession(document);
            if (_inactive) AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Inactive: another HopperFormat component controls this document");
            DA.SetData(0, OutputStatus);
            DA.SetData(1, _active ? _session.LastMoves : 0);
            DA.SetDataList(2, _session.Log(_debug));
            _lastOutputValues = CaptureOutputValues();
        });

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            _session.Guard("AddedToDocument", () =>
            {
                _contextUnloaded = false;
                ConfigureSession(document);
            });
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            _session.Guard("RemovedFromDocument", () =>
            {
                if (_sessionDocument != null) DocumentSessionRegistry.Remove(this, _sessionDocument);
                ReleaseSession();
            });
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            base.DocumentContextChanged(document, context);
            _session.Guard("DocumentContextChanged", () =>
            {
                if (context == GH_DocumentContext.Unloaded || context == GH_DocumentContext.Close)
                {
                    _contextUnloaded = true;
                    DocumentSessionRegistry.Close(document);
                    ReleaseSession();
                }
                else if (context == GH_DocumentContext.Loaded)
                {
                    _contextUnloaded = false;
                    ConfigureSession(document);
                }
            });
        }

        private void OnComponentChanged(IGH_DocumentObject sender, GH_ObjectChangedEventArgs e) =>
            _session.Guard("Component.ObjectChanged", () =>
            {
                if (e.Type == GH_ObjectEventType.Enabled && _sessionDocument != null) ConfigureSession(_sessionDocument);
            });

        private void ConfigureSession(GH_Document document)
        {
            if (_contextUnloaded) return;
            if (_sessionDocument != null && !ReferenceEquals(_sessionDocument, document))
                DocumentSessionRegistry.Remove(this, _sessionDocument);
            _contextUnloaded = false;
            _sessionDocument = document;
            DocumentSessionRegistry.Configure(this, document);
        }

        internal void ApplySession(GH_Document document, bool active, bool anotherOwner)
        {
            bool inactive = SessionEligible && !active && anotherOwner;
            bool changed = _active != active || _inactive != inactive;
            _active = active;
            _inactive = inactive;
            if (active) _session.Configure(this, document, _options);
            else _session.Dispose();
            if (changed)
            {
                ClearRuntimeMessages();
                if (inactive) AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Inactive: another HopperFormat component controls this document");
                ScheduleOutputRefresh();
            }
        }

        internal void ReleaseSession()
        {
            _outputRefreshVersion++;
            _outputRefreshPending = false;
            _lastOutputValues = null;
            _sessionDocument = null;
            _contextUnloaded = true;
            _active = _inactive = false;
            _session.Dispose();
            ClearRuntimeMessages();
        }

        internal void ScheduleOutputRefresh()
        {
            try
            {
                var document = OnPingDocument();
                if (document == null || Locked || _contextUnloaded || _refreshingOutputs) return;
                if (_outputRefreshPending || Equals(_lastOutputValues, CaptureOutputValues())) return;
                _outputRefreshPending = true;
                int version = _outputRefreshVersion;
                // Queue output expiration; never recompute synchronously in a host event or solution.
                document.ScheduleSolution(1, scheduledDocument =>
                {
                    if (version != _outputRefreshVersion) return;
                    _refreshingOutputs = true;
                    try
                    {
                        _session.Guard("Scheduled output refresh", () =>
                        {
                            if (!Locked && !_contextUnloaded && ReferenceEquals(scheduledDocument, OnPingDocument())
                                && !Equals(_lastOutputValues, CaptureOutputValues()))
                            {
                                ExpireSolution(false);
                                _lastOutputValues = CaptureOutputValues();
                            }
                        });
                    }
                    finally { _refreshingOutputs = false; _outputRefreshPending = false; }
                });
            }
            catch (Exception error)
            {
                _outputRefreshPending = false;
                // Guard reports the error without repeatedly trying this failed scheduler.
                _refreshingOutputs = true;
                try { _session.Guard("ScheduleOutputRefresh", () => throw error); }
                finally { _refreshingOutputs = false; }
            }
        }

        private string OutputStatus => _inactive ? "Inactive (another HopperFormat is active)" : _active ? _session.Status : "Off";
        private Tuple<string, int, string> CaptureOutputValues() => Tuple.Create(OutputStatus,
            _active ? _session.LastMoves : 0, string.Join("\n", _session.Log(_debug)));

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            _session.Guard("Component menu", () =>
                Menu_AppendItem(menu, "Format selection", (_, _) => _session.Run(), _active && SessionEligible));
        }

        protected override Bitmap Icon => HopperFormatInfo.LoadIcon();
        public override Guid ComponentGuid => new Guid("cdb3fa1d-69b4-4dcb-b3c0-42e10b2df366");
    }
}
