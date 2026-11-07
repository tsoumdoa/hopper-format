using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Grasshopper.Kernel;

namespace HopperFormat
{
    // Weak keys are a backstop for documents that disappear without a lifecycle
    // notification. Normal remove/unload paths remove the session immediately.
    // TODO: A live canvas still roots a manager through its event handlers if neither
    // Close nor Unloaded fires. Simply detaching on DocumentChanged would break
    // switching back without a component solve; a future safeguard needs a way to
    // reattach on return without retaining the inactive document through the canvas.
    internal static class DocumentSessionRegistry
    {
        private static readonly ConditionalWeakTable<GH_Document, Session> Sessions =
            new ConditionalWeakTable<GH_Document, Session>();

        internal static void Configure(HopperFormatComponent component, GH_Document document)
        {
            var session = Sessions.GetValue(document, _ => new Session());
            if (!session.Components.Contains(component)) session.Components.Add(component);
            session.Configure(document);
        }

        internal static void Remove(HopperFormatComponent component, GH_Document document)
        {
            if (!Sessions.TryGetValue(document, out var session)) return;
            session.Components.Remove(component);
            component.ReleaseSession();
            if (session.Components.Count == 0) Sessions.Remove(document);
            else session.Configure(document);
        }

        internal static void Close(GH_Document document)
        {
            if (!Sessions.TryGetValue(document, out var session)) return;
            Sessions.Remove(document);
            foreach (var component in session.Components.ToArray()) component.ReleaseSession();
            session.Components.Clear();
            session.Owner = null;
        }

        private sealed class Session
        {
            internal readonly List<HopperFormatComponent> Components = new List<HopperFormatComponent>();
            internal HopperFormatComponent Owner;

            internal void Configure(GH_Document document)
            {
                // The first eligible registration keeps control until it leaves or
                // is disabled. A later solve or re-enable cannot preempt it.
                if (Owner == null || !Components.Contains(Owner) || !Owner.SessionEligible)
                    Owner = Components.FirstOrDefault(component => component.SessionEligible);

                // Unwire the previous owner before the new one can attach.
                foreach (var component in Components.Where(component => component != Owner).ToArray())
                    component.ApplySession(document, false, Owner != null);
                Owner?.ApplySession(document, true, false);
            }
        }
    }
}
