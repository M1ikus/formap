using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Pathfinding graph edge — directed connection między dwoma GraphNode.
    /// Port z Unity Timetable.PathfindingGraph.Edge.
    ///
    /// MVP: Geometry serializowana jako null (na pełnej Polsce 1.24M edges × geometry
    /// list = GC pressure + niepotrzebne dla pure pathfinding). Trasy renderowane
    /// jako proste linie node→node, lub on-demand reconstruct z RailwaySegment.
    /// </summary>
    public struct GraphEdge
    {
        public int Id;
        public int FromNodeId;
        public int ToNodeId;
        public int SegmentId;
        public float LengthM;
        public int MaxSpeedKmh;
        public bool IsOsmForward;
        public Dictionary<string, string>? Metadata; // shared reference, nie alloc per Edge
        public List<GraphPoint>? Geometry; // nullable, MVP zawsze null
    }
}
