using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Pathfinding graph node — railway junction or chain endpoint.
    /// Ported from Unity's Timetable.PathfindingGraph.Node, using GraphPoint instead of Vector2.
    /// </summary>
    public struct GraphNode
    {
        public int Id;
        public GraphPoint Position;
        public List<int> EdgeIds;
    }
}
