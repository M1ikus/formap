using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Builder for StationPlatforms from the Platforms layer (railway=platform). Centroid + snap
    /// to the nearest graph node (mapping to a station). Ported from Unity's PlatformLoader.
    /// </summary>
    public static class GraphPlatformBuilder
    {
        public static List<GraphStationPlatform> Build(
            List<GraphMeshGeometry> features,
            GraphPathfindingGraph pathGraph,
            float maxStationDistanceM = 500f)
        {
            var result = new List<GraphStationPlatform>();
            if (features == null || features.Count == 0 || pathGraph == null) return result;

            int nextId = 1;
            int unmatched = 0;
            var seen = new HashSet<string>();

            foreach (var feature in features)
            {
                if (feature?.Vertices == null || feature.Vertices.Count == 0) continue;

                var centroid = ComputeCentroid(feature.Vertices);
                string key = $"{centroid.X:F0}|{centroid.Y:F0}";
                if (!seen.Add(key)) continue;

                int nearestNode = pathGraph.FindNearestNode(centroid, maxStationDistanceM);
                if (nearestNode < 0) { unmatched++; continue; }

                feature.Metadata.TryGetValue("ref", out var platformName);
                feature.Metadata.TryGetValue("railway:track_ref", out var trackRef);

                float lengthM = ComputePerimeter(feature.Vertices) * 0.5f;

                result.Add(new GraphStationPlatform
                {
                    PlatformId = nextId++,
                    StationNodeId = nearestNode,
                    Position = centroid,
                    PlatformName = platformName ?? "?",
                    TrackRef = trackRef ?? "",
                    LengthM = lengthM
                });
            }

            GraphLogger.LogInfo($"[GraphPlatformBuilder] Loaded {result.Count} platforms ({unmatched} unmatched)");
            return result;
        }

        private static GraphPoint ComputeCentroid(List<GraphPoint> points)
        {
            GraphPoint sum = GraphPoint.Zero;
            foreach (var p in points) sum += p;
            return sum / points.Count;
        }

        private static float ComputePerimeter(List<GraphPoint> points)
        {
            if (points.Count < 2) return 0f;
            float total = 0f;
            for (int i = 1; i < points.Count; i++)
                total += GraphPoint.Distance(points[i - 1], points[i]);
            return total;
        }
    }
}
