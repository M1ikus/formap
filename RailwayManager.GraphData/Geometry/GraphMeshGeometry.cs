using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Geometric feature from OSM (line/polygon/point) — a port of formap.MeshGeometry to the shared library.
    /// Used as input for the PathfindingGraph build (Railways layer features).
    ///
    /// formap pre-build: parses tile bytes into a GraphMeshGeometry list and passes it to the builder.
    /// Unity does not use it directly (Unity has its own formap.MeshGeometry for tile rendering),
    /// only indirectly through the serialized init-state.bin.
    /// </summary>
    public class GraphMeshGeometry
    {
        public GraphBBox BoundingBox;
        public List<GraphPoint> Vertices = new List<GraphPoint>();
        public List<int> Indices = new List<int>();
        public List<int> HoleStarts = new List<int>();
        public List<int> SegmentIds = new List<int>();
        public List<int> JunctionIndices = new List<int>();
        public Dictionary<string, string> Metadata = new Dictionary<string, string>();
    }
}
