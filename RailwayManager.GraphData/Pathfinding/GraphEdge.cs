using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Pathfinding graph edge — directed connection between two GraphNodes.
    /// Ported from Unity's Timetable.PathfindingGraph.Edge.
    ///
    /// MVP: Geometry is serialized as null (for all of Poland, 1.24M edges × geometry
    /// list = GC pressure + unnecessary for pure pathfinding). Routes are rendered
    /// as straight node→node lines, or reconstructed on demand from RailwaySegment.
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
        public int TrackIndex; // v5: index into InitState.Tracks (the physical track this edge lies on); -1 = none
        public int StationId;  // v5: index into InitState.Stations (station whose extent this edge is in); -1 = open line
        public Dictionary<string, string>? Metadata; // shared reference, not allocated per Edge
        public List<GraphPoint>? Geometry; // nullable, always null in the MVP
    }
}
