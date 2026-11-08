using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using HopperFormat.Layout;

namespace HopperFormat
{
    internal static class FormatApplier
    {
        internal static bool Apply(GH_Document document, IReadOnlyList<Move> moves,
            IReadOnlyDictionary<Guid, IGH_DocumentObject> objects, out string status, bool containsGroups = false, bool formatAll = false, GroupMembership.Index membership = null)
        {
            status = formatAll ? "Formatted all groups · 0 moves" : "Already formatted";
            if (moves.Count == 0) return true;
            var movedObjects = moves.Select(move => objects[move.Id]).ToArray();
            var originals = movedObjects.ToDictionary(obj => obj, obj => obj.Attributes.Pivot);
            var groups = (membership ?? new GroupMembership.Index(document)).Ancestors(moves.Select(move => move.Id));
            // Pivot undo actions snapshot their positions in their constructors.
            var record = document.UndoUtil.CreatePivotEvent(formatAll ? "Format all groups" : containsGroups ? "Format groups" : "Format selection", movedObjects);
            try
            {
                foreach (var move in moves)
                    SetPivot(objects[move.Id], new PointF((float)move.Pivot.X, (float)move.Pivot.Y));
                foreach (var group in groups) group.ExpireCaches();
                foreach (var move in moves)
                {
                    var pivot = objects[move.Id].Attributes.Pivot;
                    if (Math.Abs(pivot.X - move.Pivot.X) > 0.01 || Math.Abs(pivot.Y - move.Pivot.Y) > 0.01
                        || !float.IsFinite(pivot.X) || !float.IsFinite(pivot.Y))
                        throw new InvalidOperationException($"Applied pivot mismatch for '{SelectionSnapshot.Label(objects[move.Id])}'");
                }
                if (document.UndoUtil.RecordEvent(record) == Guid.Empty)
                    throw new InvalidOperationException("Grasshopper did not record the format undo event");
            }
            catch (Exception error)
            {
                var failures = new List<string>();
                foreach (var pair in originals)
                    try { SetPivot(pair.Key, pair.Value); }
                    catch (Exception restoreError) { failures.Add($"'{SelectionSnapshot.Label(pair.Key)}': {restoreError.Message}"); }
                foreach (var group in groups)
                    try { group.ExpireCaches(); }
                    catch (Exception cacheError) { failures.Add(cacheError.Message); }
                status = "Format failed: " + error.Message;
                if (failures.Count > 0) status += "; restore failed: " + string.Join("; ", failures);
                Grasshopper.Instances.RedrawCanvas();
                return false;
            }
            status = $"Formatted {(formatAll ? "all groups" : containsGroups ? "groups" : "selection")} · {moves.Count} moves";
            Grasshopper.Instances.RedrawCanvas();
            return true;
        }

        private static void SetPivot(IGH_DocumentObject obj, PointF pivot)
        {
            obj.Attributes.Pivot = pivot;
            obj.Attributes.ExpireLayout();
            obj.Attributes.PerformLayout();
        }
    }
}
