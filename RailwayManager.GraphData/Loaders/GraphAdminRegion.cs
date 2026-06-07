using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Administrative boundary from OSM (country or voivodeship). The polygon is held as a
    /// triangle list (vertices + indices); the PIP test iterates over the triangles.
    /// Ported from Unity's Timetable.AdminRegion.
    /// </summary>
    public class GraphAdminRegion
    {
        public string? Name;
        public int AdminLevel;       // 2 = country, 4 = voivodeship
        public string? Iso3166_1;     // e.g. "PL"
        public string? Iso3166_2;     // e.g. "PL-MZ"
        public GraphBBox BoundingBox;

        public List<GraphPoint> Vertices = new List<GraphPoint>();
        public List<int> Indices = new List<int>();

        public bool ContainsPoint(GraphPoint point)
        {
            if (!BoundingBox.Contains(point)) return false;
            for (int i = 0; i + 2 < Indices.Count; i += 3)
            {
                var a = Vertices[Indices[i]];
                var b = Vertices[Indices[i + 1]];
                var c = Vertices[Indices[i + 2]];
                if (PointInTriangle(point, a, b, c)) return true;
            }
            return false;
        }

        private static bool PointInTriangle(GraphPoint p, GraphPoint a, GraphPoint b, GraphPoint c)
        {
            float d1 = Sign(p, a, b);
            float d2 = Sign(p, b, c);
            float d3 = Sign(p, c, a);
            bool hasNeg = (d1 < 0f) || (d2 < 0f) || (d3 < 0f);
            bool hasPos = (d1 > 0f) || (d2 > 0f) || (d3 > 0f);
            return !(hasNeg && hasPos);
        }

        private static float Sign(GraphPoint p1, GraphPoint p2, GraphPoint p3)
            => (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
    }
}
