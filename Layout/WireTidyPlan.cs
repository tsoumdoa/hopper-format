using System;
using System.Collections.Generic;
using System.Linq;

namespace HopperFormat.Layout
{
    internal sealed class WireTidyPlan
    {
        internal sealed class Satellite
        {
            internal Guid Owner;
            internal NonNodeMember Member;
            internal Box Bounds;
        }

        internal Dictionary<Guid, Box> Boxes;
        internal List<Box> Obstacles;
        internal readonly List<Satellite> Satellites = new List<Satellite>();
        private readonly Dictionary<Guid, Guid> owners = new Dictionary<Guid, Guid>();
        private readonly Dictionary<Guid, double> innerDx = new Dictionary<Guid, double>();
        private readonly Dictionary<Guid, double> innerDy = new Dictionary<Guid, double>();
        private readonly List<WireX> forward = new List<WireX>();
        private Dictionary<Guid, Box> initial;
        internal bool HasBounds => forward.Count > 0;

        internal static WireTidyPlan Create(FormatAllSnapshot snapshot, (Guid Id, Result Result)[] solved,
            Dictionary<Guid, Dictionary<Guid, NonNodeMember>> members, Dictionary<Guid, Box> boxes, List<Box> obstacles,
            IReadOnlyDictionary<Guid, Guid> aliases, IReadOnlyDictionary<Guid, Guid[]> nodeIds)
        {
            var plan = new WireTidyPlan { Boxes = new Dictionary<Guid, Box>(boxes), Obstacles = obstacles.ToList() };
            foreach (var root in solved.OrderBy(s => s.Id))
                foreach (var p in root.Result.Tops)
                    if (!plan.owners.ContainsKey(p.Key))
                    {
                        plan.owners.Add(p.Key, root.Id);
                        plan.innerDx.Add(p.Key, p.Value.X - members[root.Id][p.Key].Bounds.X);
                        plan.innerDy.Add(p.Key, p.Value.Y - members[root.Id][p.Key].Bounds.Y);
                    }
            // A label-only alias's additional physical members follow its node
            // during composition even though its root has no independent solve.
            foreach (var alias in aliases.OrderBy(p => p.Key))
            {
                var anchor = nodeIds[alias.Key][0];
                if (!plan.owners.TryGetValue(anchor, out var owner)) continue;
                foreach (var member in members[alias.Key].Values.Where(n => !plan.owners.ContainsKey(n.Id)))
                {
                    plan.owners.Add(member.Id, owner);
                    plan.innerDx.Add(member.Id, LayoutSolver.Round(plan.innerDx[anchor]));
                    plan.innerDy.Add(member.Id, LayoutSolver.Round(plan.innerDy[anchor]));
                }
            }
            // Membership for satellite classification includes fixed roots. A
            // neighbour outside this map (including another loose object) disqualifies.
            var membership = members.SelectMany(p => p.Value.Keys.Select(id => (Id: id, Owner: aliases.TryGetValue(p.Key, out var owner) ? owner : p.Key)))
                .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.Select(p => p.Owner).Distinct().ToArray());
            foreach (var loose in snapshot.LooseObjects.OrderBy(n => n.Id))
            {
                var touching = snapshot.Wires.Where(w => w.From == loose.Id || w.To == loose.Id).ToArray();
                var neighbours = touching.Select(w => w.From == loose.Id ? w.To : w.From).Distinct().ToArray();
                if (neighbours.Length == 0 || neighbours.Any(id => !membership.ContainsKey(id))) continue;
                var roots = neighbours.SelectMany(id => membership[id]).Distinct().ToArray();
                if (roots.Length != 1 || !boxes.ContainsKey(roots[0])) continue;
                var protect = touching.Where(w => Graph.Finite(w.OutputGripX) && Graph.Finite(w.InputGripX)
                    && w.OutputGripX <= w.InputGripX).ToArray();
                var desired = protect.Select(w => plan.innerDx.GetValueOrDefault(w.From == loose.Id ? w.To : w.From)).OrderBy(x => x).ToArray();
                double dx = Median(desired);
                (double Lower, double Upper) Window(bool clearance)
                {
                    double lower = double.NegativeInfinity, upper = double.PositiveInfinity;
                    foreach (var w in protect)
                    {
                        double margin = clearance && w.InputGripX - w.OutputGripX >= 20 ? 20 : 0;
                        if (w.From == loose.Id) upper = Math.Min(upper, Math.Floor(w.InputGripX + plan.innerDx.GetValueOrDefault(w.To) - margin - w.OutputGripX));
                        else lower = Math.Max(lower, Math.Ceiling(w.OutputGripX + plan.innerDx.GetValueOrDefault(w.From) + margin - w.InputGripX));
                    }
                    return (lower, upper);
                }
                var window = Window(true);
                if (window.Lower > window.Upper) window = Window(false);
                double lower = window.Lower, upper = window.Upper;
                if (lower > upper) continue; // not representable by a rigid satellite
                dx = Math.Max(lower, Math.Min(upper, LayoutSolver.Round(dx)));
                var desiredY = protect.Select(w => plan.innerDy.GetValueOrDefault(w.From == loose.Id ? w.To : w.From)).OrderBy(y => y).ToArray();
                double preferredY = LayoutSolver.Round(Median(desiredY));
                var b = FormatAllSolver.Translate(loose.Bounds, dx, preferredY);
                // Place the satellite once relative to its owner before rigid tidy.
                // It then participates in the owner's footprint and follows every
                // row push, so downstream groups see its final position.
                var avoid = new[] { boxes[roots[0]] }.Concat(plan.Satellites.Select(s => s.Bounds)).ToArray();
                var candidates = new[] { 0.0 }.Concat(avoid.SelectMany(p => new[] {
                    Math.Floor(p.Y - b.Bottom), Math.Ceiling(p.Bottom - b.Y) })).OrderBy(Math.Abs).ThenBy(y => y);
                double dy = candidates.First(y => avoid.All(p => !Overlaps(FormatAllSolver.Translate(b, 0, y), p)));
                b = FormatAllSolver.Translate(b, 0, dy);
                plan.Satellites.Add(new Satellite { Owner = roots[0], Member = loose, Bounds = b });
                plan.Obstacles.Remove(loose.Bounds);
                plan.owners.Add(loose.Id, roots[0]); plan.innerDx.Add(loose.Id, dx);
                var group = plan.Boxes[roots[0]];
                double x = Math.Min(group.X, b.X), y0 = Math.Min(group.Y, b.Y);
                plan.Boxes[roots[0]] = new Box(x, y0, Math.Max(group.Right, b.Right) - x, Math.Max(group.Bottom, b.Bottom) - y0);
            }
            plan.initial = new Dictionary<Guid, Box>(plan.Boxes);
            foreach (var w in snapshot.Wires)
            {
                plan.owners.TryGetValue(w.From, out var from); plan.owners.TryGetValue(w.To, out var to);
                if (from == to || !Graph.Finite(w.OutputGripX) || !Graph.Finite(w.InputGripX) || w.OutputGripX > w.InputGripX) continue;
                plan.forward.Add(w);
            }
            return plan;
        }

        internal IEnumerable<HorizontalConstraints.Bound> Bounds(Dictionary<Guid, Box> current, bool clearance)
        {
            foreach (var w in forward)
            {
                owners.TryGetValue(w.From, out var from); owners.TryGetValue(w.To, out var to);
                innerDx.TryGetValue(w.From, out double fromDx); innerDx.TryGetValue(w.To, out double toDx);
                if (from != Guid.Empty) fromDx += current[from].X - initial[from].X;
                if (to != Guid.Empty) toDx += current[to].X - initial[to].X;
                double margin = clearance && w.InputGripX - w.OutputGripX >= 20 ? 20 : 0;
                yield return new HorizontalConstraints.Bound(from, to,
                    w.OutputGripX + fromDx + margin - w.InputGripX - toDx);
            }
        }

        private static bool Overlaps(Box a, Box b) => a.Right > b.X && b.Right > a.X && a.Bottom > b.Y && b.Bottom > a.Y;
        private static double Median(double[] sorted) => sorted.Length == 0 ? 0
            : Math.Max(sorted[(sorted.Length - 1) / 2], Math.Min(0, sorted[sorted.Length / 2]));
    }
}
