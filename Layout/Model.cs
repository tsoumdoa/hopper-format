using System;
using System.Collections.Generic;

namespace HopperFormat.Layout
{
    public readonly struct Vec2
    {
        public readonly double X, Y;
        public Vec2(double x, double y) { X = x; Y = y; }
    }

    public enum NodeKind { Component, Param, Panel }

    public sealed class Node
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public NodeKind Kind { get; set; } = NodeKind.Component;
        public Vec2 CurrentTop { get; set; }
        public Vec2 PivotOffset { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Widget { get; set; }
        public Vec2[] Inputs { get; set; } = Array.Empty<Vec2>();
        public Vec2[] Outputs { get; set; } = Array.Empty<Vec2>();
    }

    public sealed class Edge
    {
        public Guid From { get; set; }
        public int FromPort { get; set; }
        public Guid To { get; set; }
        public int ToPort { get; set; }
    }

    public sealed class ExternalInbound
    {
        public Guid Node { get; set; }
        public int Port { get; set; }
        public double SourceGripY { get; set; }
    }

    public sealed class ExternalOutbound
    {
        public Guid Node { get; set; }
        public int Port { get; set; }
        public double TargetGripY { get; set; }
    }

    public sealed class Options
    {
        public double GapX { get; set; } = 30;
        public double GapY { get; set; } = 16;
        public double StackGap { get; set; } = 6;
        public bool ReturnConstraint { get; set; } = true;
    }

    public sealed class Snapshot
    {
        public IReadOnlyList<Node> Nodes { get; set; } = Array.Empty<Node>();
        public IReadOnlyList<Edge> Edges { get; set; } = Array.Empty<Edge>();
        public IReadOnlyList<ExternalInbound> Inbound { get; set; } = Array.Empty<ExternalInbound>();
        public IReadOnlyList<ExternalOutbound> Outbound { get; set; } = Array.Empty<ExternalOutbound>();
        public Options Options { get; set; } = new Options();
    }

    public sealed class Move
    {
        public Guid Id { get; set; }
        public Vec2 Pivot { get; set; }
    }

    public sealed class Result
    {
        public bool Success { get; set; }
        public string Diagnostic { get; set; } = string.Empty;
        public Vec2 Anchor { get; set; }
        public IReadOnlyDictionary<Guid, Vec2> Tops { get; set; } = new Dictionary<Guid, Vec2>();
        public int Rounds { get; set; }
        public IReadOnlyList<Move> Moves { get; set; } = Array.Empty<Move>();
        public IReadOnlyList<string> Log { get; set; } = Array.Empty<string>();
        public IReadOnlyDictionary<Guid, int> Ranks { get; set; } = new Dictionary<Guid, int>();
        public int CrossingsBefore { get; set; }
        public int CrossingsAfter { get; set; }
        public bool Unstable { get; set; }
        // Complete long-edge chains in the emitted placement, for lane diagnostics.
        internal IReadOnlyList<Edge> LaneEdges { get; set; } = Array.Empty<Edge>();
    }
}

