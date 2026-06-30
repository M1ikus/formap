using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Builder for RailwayStations from the POIs layer (railway=station|halt). Snap to PathfindingGraph.
    /// Ported from Unity's StationLoader.
    /// </summary>
    public static class GraphStationBuilder
    {
        public static List<GraphRailwayStation> Build(
            List<GraphMeshGeometry> poiFeatures,
            GraphPathfindingGraph pathGraph,
            float maxSnapRadiusM = 200f,
            GraphVoivodeshipResolver? resolver = null)
        {
            var result = new List<GraphRailwayStation>();
            if (poiFeatures == null || poiFeatures.Count == 0 || pathGraph == null) return result;

            int nextId = 1;
            int unmatched = 0;
            int withVoivodeship = 0;
            var seen = new HashSet<string>();

            foreach (var feature in poiFeatures)
            {
                if (feature?.Vertices == null || feature.Vertices.Count == 0) continue;
                if (feature.Metadata == null) continue;
                if (!feature.Metadata.TryGetValue("railway", out var railwayType)) continue;

                bool isMajor = railwayType == "station";
                bool isHalt = railwayType == "halt";
                if (!isMajor && !isHalt) continue;

                // Skip tram/subway/light_rail/narrow_gauge/monorail stations — not playable for mainline.
                if (feature.Metadata.TryGetValue("station", out var stationKind))
                {
                    if (stationKind == "subway" || stationKind == "tram" || stationKind == "light_rail"
                        || stationKind == "monorail" || stationKind == "narrow_gauge")
                        continue;
                }
                // Plus: some stops are tagged as railway=halt + <type>=yes — skip
                if (feature.Metadata.TryGetValue("tram", out var tramTag) && tramTag == "yes") continue;
                if (feature.Metadata.TryGetValue("subway", out var subwayTag) && subwayTag == "yes") continue;
                if (feature.Metadata.TryGetValue("light_rail", out var lrTag) && lrTag == "yes") continue;
                if (feature.Metadata.TryGetValue("narrow_gauge", out var ngTag) && ngTag == "yes") continue;

                feature.Metadata.TryGetValue("name", out var name);
                var pos = feature.Vertices[0];

                string key = $"{name}|{pos.X:F0}|{pos.Y:F0}";
                if (!seen.Add(key)) continue;

                int nodeId = pathGraph.FindNearestNode(pos, maxSnapRadiusM);
                if (nodeId < 0) unmatched++;

                long osmId = 0; // v5: stable save-game key (propagated by formap as osm:node_id)
                if (feature.Metadata.TryGetValue("osm:node_id", out var nidStr)) long.TryParse(nidStr, out osmId);

                var station = new GraphRailwayStation
                {
                    StationId = nextId++, // provisional — reindexed deterministically (by OsmNodeId) in InitStateBuilder
                    OsmNodeId = osmId,
                    Name = name ?? "(no name)",
                    Position = pos,
                    IsMajorStation = isMajor,
                    PathNodeId = nodeId,
                    CityName = ExtractCityName(name)
                };

                if (resolver != null)
                {
                    station.Voivodeship = resolver.GetVoivodeship(pos);
                    if (station.Voivodeship != null) withVoivodeship++;
                }

                result.Add(station);
            }

            GraphLogger.LogInfo($"[GraphStationBuilder] Loaded {result.Count} stations ({unmatched} unmatched, {withVoivodeship} with voivodeship)");
            return result;
        }

        private static string? ExtractCityName(string? stationName)
        {
            if (string.IsNullOrEmpty(stationName)) return null;
            int spaceIdx = stationName.IndexOf(' ');
            return spaceIdx < 0 ? stationName : stationName.Substring(0, spaceIdx);
        }
    }
}
