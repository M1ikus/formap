using System;
using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// v5: stamps edge.StationId — which station's extent each edge lies in — via a bounded graph BFS from each
    /// station's PathNodeId, capped adaptively (min(rMax, ½·dist-to-nearest-station), in TRACK distance), and
    /// barred at line boundaries so a halt on a connector doesn't spill onto a parallel mainline across a switch.
    ///
    /// The barrier is TOPOLOGICAL, not distance-based (a station's own mainline can be farther in graph distance
    /// than a foreign parallel line — e.g. Jordanów's lk98 is 205 m away while Sucha Beskidzka Zamek's foreign
    /// lk98 is ~106 m). Each station gets a seed line-set from what it's on LOCALLY: its platforms' projected
    /// track line_refs (v5 platform Entries → TrackIndex) ∪ the line_refs incident to / within a small core of the
    /// station node. Then, during the extent BFS:
    ///  • if the seed is NON-empty (station sits on numbered line(s), e.g. Zamek=625, Sucha=97/98) → only those
    ///    numbered lines (plus no-line_ref station tracks) are traversed/tagged; a different number is barred;
    ///  • if the seed is EMPTY (platform on an unnumbered track, e.g. Jordanów) → the BFS commits to the FIRST
    ///    numbered line it reaches (the nearest mainline) and bars switching to a different number afterwards.
    /// Edges with no line_ref are always allowed. Ties go to the nearer station; both directed edges of a segment
    /// share the id; -1 = open line.
    /// </summary>
    public static class GraphStationExtentBuilder
    {
        private const float CoreRadiusM = 70f;

        public static void Assign(GraphPathfindingGraph graph, List<GraphRailwayStation> stations,
                                  List<GraphStationPlatform> platforms, List<GraphTrack> tracks, float rMax = 1500f)
        {
            var nodes = graph.Nodes; var edges = graph.Edges;
            int edgeCount = edges.Count;

            static List<string>? Refs(GraphEdge e)
            {
                if (e.Metadata == null || !e.Metadata.TryGetValue("railway:line_ref", out var lr) || lr.Length == 0) return null;
                var list = new List<string>();
                foreach (var r in lr.Split(';')) if (r.Length > 0) list.Add(r);
                return list.Count > 0 ? list : null;
            }

            // per-track line-set (a physical track's edges carry its line number)
            var trackLines = new HashSet<string>[tracks?.Count ?? 0];
            for (int ti = 0; ti < trackLines.Length; ti++)
            {
                trackLines[ti] = new HashSet<string>();
                var t = tracks[ti];
                if (t.EdgeIds != null)
                    foreach (int eid in t.EdgeIds) { if (eid < 0 || eid >= edgeCount) continue; var rf = Refs(edges[eid]); if (rf != null) trackLines[ti].UnionWith(rf); }
            }

            // seed line-set per station: platform-track lines ∪ core (incident + small BFS) lines
            var seed = new HashSet<string>[stations.Count];
            for (int si = 0; si < stations.Count; si++)
            {
                seed[si] = new HashSet<string>();
                int pn = stations[si].PathNodeId;
                if (pn < 0) continue;
                var d2 = new Dictionary<int, float> { [pn] = 0f };
                var fr = new SortedSet<(float d, int n)> { (0f, pn) };
                while (fr.Count > 0)
                {
                    var top = fr.Min; fr.Remove(top); float du = top.d; int u = top.n;
                    if (du > CoreRadiusM) break; if (du > d2[u]) continue;
                    foreach (int e in nodes[u].EdgeIds)
                    {
                        var rf = Refs(edges[e]); if (rf != null) seed[si].UnionWith(rf);
                        float nd = du + edges[e].LengthM; int v = edges[e].ToNodeId;
                        if (nd <= CoreRadiusM && (!d2.TryGetValue(v, out var dv) || nd < dv)) { d2[v] = nd; fr.Add((nd, v)); }
                    }
                }
            }
            if (platforms != null)
            {
                foreach (var p in platforms)
                {
                    if (p.Entries == null || p.Entries.Count == 0) continue;
                    int best = -1; float bestD = 1500f;
                    for (int si = 0; si < stations.Count; si++)
                    { float d = GraphPoint.Distance(p.Position, stations[si].Position); if (d < bestD) { bestD = d; best = si; } }
                    if (best < 0) continue;
                    foreach (var en in p.Entries)
                        if (en.TrackIndex >= 0 && en.TrackIndex < trackLines.Length) seed[best].UnionWith(trackLines[en.TrackIndex]);
                }
            }

            var bestDist = new float[edgeCount];
            var stationOf = new int[edgeCount];
            for (int i = 0; i < edgeCount; i++) { bestDist[i] = float.PositiveInfinity; stationOf[i] = -1; }

            var revOf = new int[edgeCount];
            for (int i = 0; i < edgeCount; i++) revOf[i] = -1;
            for (int e = 0; e < edgeCount; e++)
            {
                if (revOf[e] != -1) continue;
                var ed = edges[e];
                foreach (int oe in nodes[ed.ToNodeId].EdgeIds)
                { var o = edges[oe]; if (o.ToNodeId == ed.FromNodeId && o.SegmentId == ed.SegmentId) { revOf[e] = oe; revOf[oe] = e; break; } }
            }

            for (int si = 0; si < stations.Count; si++)
            {
                var s = stations[si];
                if (s.PathNodeId < 0) continue;

                float extentR = rMax;
                float nearest = float.PositiveInfinity;
                for (int oj = 0; oj < stations.Count; oj++)
                { if (oj == si) continue; float d = GraphPoint.Distance(s.Position, stations[oj].Position); if (d < nearest) nearest = d; }
                if (!float.IsInfinity(nearest)) extentR = Math.Min(extentR, nearest * 0.5f);
                if (extentR <= 0f) continue;

                var committed = new HashSet<string>(seed[si]); // grows only if it started empty (dynamic commit)
                var dist = new Dictionary<int, float> { [s.PathNodeId] = 0f };
                var frontier = new SortedSet<(float d, int n)> { (0f, s.PathNodeId) };
                while (frontier.Count > 0)
                {
                    var top = frontier.Min; frontier.Remove(top);
                    float du = top.d; int u = top.n;
                    if (du > extentR) break;
                    if (du > dist[u]) continue;
                    foreach (int e in nodes[u].EdgeIds)
                    {
                        var rf = Refs(edges[e]);
                        if (rf != null) // numbered edge — apply the line barrier
                        {
                            bool onLine = false;
                            foreach (var r in rf) if (committed.Contains(r)) { onLine = true; break; }
                            if (!onLine)
                            {
                                if (committed.Count == 0) { foreach (var r in rf) committed.Add(r); } // commit to first line reached
                                else continue;                                                        // bar switching to a foreign line
                            }
                        }
                        if (du < bestDist[e]) { bestDist[e] = du; stationOf[e] = si; }
                        float nd = du + edges[e].LengthM;
                        int v = edges[e].ToNodeId;
                        if (nd <= extentR && (!dist.TryGetValue(v, out var dv) || nd < dv)) { dist[v] = nd; frontier.Add((nd, v)); }
                    }
                }
            }

            for (int e = 0; e < edgeCount; e++)
            {
                int r = revOf[e];
                if (r < 0 || r < e) continue;
                int pick = bestDist[e] <= bestDist[r] ? stationOf[e] : stationOf[r];
                stationOf[e] = pick; stationOf[r] = pick;
            }

            for (int e = 0; e < edgeCount; e++)
                if (stationOf[e] >= 0) graph.SetEdgeStationId(e, stationOf[e]);
        }
    }
}
