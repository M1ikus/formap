using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Pathfinding graph node — railway junction or chain endpoint.
    /// Port z Unity Timetable.PathfindingGraph.Node, używa GraphPoint zamiast Vector2.
    /// </summary>
    public struct GraphNode
    {
        public int Id;
        public GraphPoint Position;
        public List<int> EdgeIds;
    }
}
