using System;
using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// v5: stamps edge.StationId — which station's extent each edge lies in — via a bounded graph BFS from each
    /// station's PathNodeId. The extent radius is adaptive: min(rMax, ½·distance-to-nearest-other-station),
    /// measured in TRACK distance (graph BFS, not euclidean) so a parallel line passing nearby isn't captured.
    /// Ties go to the nearer station. edge.StationId = the station's index in InitState.Stations (== the
    /// save-game StationId); -1 = open line. Both directed edges of a segment get the same station.
    ///
    /// netstandard2.1 (shared lib) — no System.Collections.Generic.PriorityQueue, so the bounded Dijkstra uses a
    /// SortedSet&lt;(dist,node)&gt; frontier with lazy stale-skip.
    /// </summary>
    public static class GraphStationExtentBuilder
    {
        public static void Assign(GraphPathfindingGraph graph, List<GraphRailwayStation> stations, float rMax = 1500f)
        {
            var nodes = graph.Nodes; var edges = graph.Edges;
            int edgeCount = edges.Count;
            var bestDist = new float[edgeCount];
            var stationOf = new int[edgeCount];
            for (int i = 0; i < edgeCount; i++) { bestDist[i] = float.PositiveInfinity; stationOf[i] = -1; }

            // reverse-twin lookup (same SegmentId, swapped endpoints) — to keep a segment's two directed edges consistent
            var revOf = new int[edgeCount];
            for (int i = 0; i < edgeCount; i++) revOf[i] = -1;
            for (int e = 0; e < edgeCount; e++)
            {
                if (revOf[e] != -1) continue;
                var ed = edges[e];
                foreach (int oe in nodes[ed.ToNodeId].EdgeIds)
                {
                    var o = edges[oe];
                    if (o.ToNodeId == ed.FromNodeId && o.SegmentId == ed.SegmentId) { revOf[e] = oe; revOf[oe] = e; break; }
                }
            }

            for (int si = 0; si < stations.Count; si++)
            {
                var s = stations[si];
                if (s.PathNodeId < 0) continue;

                float extentR = rMax;
                float nearest = float.PositiveInfinity;
                for (int oj = 0; oj < stations.Count; oj++)
                {
                    if (oj == si) continue;
                    float d = GraphPoint.Distance(s.Position, stations[oj].Position);
                    if (d < nearest) nearest = d;
                }
                if (!float.IsInfinity(nearest)) extentR = Math.Min(extentR, nearest * 0.5f);
                if (extentR <= 0f) continue;

                // bounded Dijkstra (track distance) from the station node; tag each edge by its start-node distance.
                var dist = new Dictionary<int, float> { [s.PathNodeId] = 0f };
                var frontier = new SortedSet<(float d, int n)> { (0f, s.PathNodeId) };
                while (frontier.Count > 0)
                {
                    var top = frontier.Min; frontier.Remove(top);
                    float du = top.d; int u = top.n;
                    if (du > extentR) break;                 // ascending frontier — nothing closer remains
                    if (du > dist[u]) continue;              // stale
                    foreach (int e in nodes[u].EdgeIds)
                    {
                        if (du < bestDist[e]) { bestDist[e] = du; stationOf[e] = si; }
                        float nd = du + edges[e].LengthM;
                        int v = edges[e].ToNodeId;
                        if (nd <= extentR && (!dist.TryGetValue(v, out var dv) || nd < dv)) { dist[v] = nd; frontier.Add((nd, v)); }
                    }
                }
            }

            // reconcile each segment: both directed edges adopt the station of the nearer-reached direction
            for (int e = 0; e < edgeCount; e++)
            {
                int r = revOf[e];
                if (r < 0 || r < e) continue; // process each segment once (smaller id)
                int pick = bestDist[e] <= bestDist[r] ? stationOf[e] : stationOf[r];
                stationOf[e] = pick; stationOf[r] = pick;
            }

            for (int e = 0; e < edgeCount; e++)
                if (stationOf[e] >= 0) graph.SetEdgeStationId(e, stationOf[e]);
        }
    }
}
