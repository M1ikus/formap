using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Builder dla SignalInfo z POIs layer (railway=signal). Snap do graph + parse function.
    /// Port z Unity SignalsStreamProcessor / TimetableInitializer.LoadSignals.
    /// </summary>
    public static class GraphSignalsBuilder
    {
        public static List<GraphSignalInfo> Build(List<GraphMeshGeometry> poiFeatures, GraphPathfindingGraph graph)
        {
            var result = new List<GraphSignalInfo>();
            if (poiFeatures == null || poiFeatures.Count == 0 || graph == null) return result;

            int total = 0, withFunction = 0, duplicates = 0;
            int entry = 0, exit = 0, block = 0, intermediate = 0;
            var seenPositions = new HashSet<string>();

            foreach (var feature in poiFeatures)
            {
                if (feature?.Vertices == null || feature.Vertices.Count == 0) continue;
                if (!feature.Metadata.TryGetValue("railway", out var railway)) continue;
                if (railway != "signal") continue;

                total++;
                var func = ParseSignalFunction(feature.Metadata);
                if (func == GraphSignalFunction.Unknown) continue;
                withFunction++;

                var pos = feature.Vertices[0];
                string posKey = $"{pos.X:F2}|{pos.Y:F2}";
                if (!seenPositions.Add(posKey)) { duplicates++; continue; }

                int nodeId = graph.FindNearestNode(pos, 10f);
                if (nodeId < 0) continue;

                var dir = GraphSignalDirection.Both;
                if (feature.Metadata.TryGetValue("railway:signal:direction", out var dirStr))
                {
                    if (dirStr == "forward") dir = GraphSignalDirection.Forward;
                    else if (dirStr == "backward") dir = GraphSignalDirection.Backward;
                }

                feature.Metadata.TryGetValue("ref", out var refNum);

                switch (func)
                {
                    case GraphSignalFunction.Entry: entry++; break;
                    case GraphSignalFunction.Exit: exit++; break;
                    case GraphSignalFunction.Block: block++; break;
                    case GraphSignalFunction.Intermediate: intermediate++; break;
                }

                result.Add(new GraphSignalInfo
                {
                    NodeId = nodeId,
                    Function = func,
                    Direction = dir,
                    RefNum = refNum ?? ""
                });
            }

            GraphLogger.LogInfo($"[GraphSignalsBuilder] {total} total, {withFunction} with function, "
                     + $"{duplicates} dup skipped, {result.Count} snapped — "
                     + $"entry={entry} exit={exit} block={block} intermediate={intermediate}");
            return result;
        }

        private static GraphSignalFunction ParseSignalFunction(Dictionary<string, string> metadata)
        {
            string[] keys = { "railway:signal:main:function", "railway:signal:combined:function" };
            foreach (var key in keys)
            {
                if (!metadata.TryGetValue(key, out var func)) continue;
                switch (func)
                {
                    case "entry": return GraphSignalFunction.Entry;
                    case "exit": return GraphSignalFunction.Exit;
                    case "block": return GraphSignalFunction.Block;
                    case "intermediate": return GraphSignalFunction.Intermediate;
                }
            }
            return GraphSignalFunction.Unknown;
        }
    }
}
