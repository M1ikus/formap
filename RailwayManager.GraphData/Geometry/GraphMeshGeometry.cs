using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Geometric feature z OSM (line/polygon/point) — port formap.MeshGeometry do shared library.
    /// Używana jako input dla PathfindingGraph build (Railways layer features).
    ///
    /// formap pre-build: parsuje tile bytes do GraphMeshGeometry list, przekazuje do builder.
    /// Unity nie używa bezpośrednio (Unity ma własny formap.MeshGeometry dla tile rendering),
    /// tylko pośrednio przez serialized init-state.bin.
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
