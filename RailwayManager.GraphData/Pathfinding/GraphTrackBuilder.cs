using System;
using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// v5 (TD-055/056): segments the railway graph into physical tracks — maximal chains of edges between
    /// switches and dead-ends. A boundary is a node of degree != 2 (degree ≥3 = switch/rozjazd, degree 1 =
    /// dead-end); degree-2 nodes are pass-through (a track continues through them, including benign two-way
    /// way-joins). Each <see cref="GraphTrack"/> gets a stable content-derived <see cref="GraphTrack.TrackKey"/>
    /// (64-bit FNV-1a over the chain's sorted (osm wayId, vertexIndex) set), a canonical 0→Length direction
    /// (start = bounding node with the smaller (round(Y),round(X))), and ordered forward EdgeIds. Both directed
    /// edges of every physical segment are stamped with the track index.
    ///
    /// NOTE on the frozen contract wording ("JunctionNodeIds ∪ dead-ends"): we use the graph DEGREE, not the raw
    /// JunctionNodeIds set, because JunctionNodeIds also marks degree-2 two-way joins (one physical track, two
    /// OSM ways) — splitting there would fragment tracks at every way boundary instead of only at real switches.
    /// Pure formap-internal change; no init-state byte-layout impact.
    /// </summary>
    public static class GraphTrackBuilder
    {
        public static List<GraphTrack> Build(GraphPathfindingGraph graph, Dictionary<int, (long wayId, int vtx)> segMap)
        {
            var nodes = graph.Nodes;
            var edges = graph.Edges;
            int nodeCount = nodes.Count, edgeCount = edges.Count;

            // Reverse-edge twin (same SegmentId, swapped endpoints).
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

            bool Boundary(int n) => nodes[n].EdgeIds.Count != 2; // switch (≥3) or dead-end (1)

            var segDone = new HashSet<int>();          // by SegmentId — dedupes fwd/rev + tile replicas
            var edgeTrack = new int[edgeCount];
            for (int i = 0; i < edgeCount; i++) edgeTrack[i] = -1;
            var tracks = new List<GraphTrack>();

            (long, long) PosKey(int n)
            {
                var p = nodes[n].Position;
                return ((long)Math.Round(p.Y * 1000.0), (long)Math.Round(p.X * 1000.0));
            }
            bool FirstIsStart(int a, int b)
            {
                var ka = PosKey(a); var kb = PosKey(b);
                if (ka.Item1 != kb.Item1) return ka.Item1 < kb.Item1;
                if (ka.Item2 != kb.Item2) return ka.Item2 < kb.Item2;
                return a <= b;
            }

            void MakeTrack(int start, int end, List<int> chain)
            {
                int ti = tracks.Count;
                List<int> fwd;
                int sNode, eNode;
                if (FirstIsStart(start, end)) { fwd = chain; sNode = start; eNode = end; }
                else
                {
                    fwd = new List<int>(chain.Count);
                    for (int i = chain.Count - 1; i >= 0; i--) fwd.Add(revOf[chain[i]] >= 0 ? revOf[chain[i]] : chain[i]);
                    sNode = end; eNode = start;
                }
                float len = 0f;
                var keys = new SortedSet<(long, int)>();
                foreach (int eid in fwd)
                {
                    len += edges[eid].LengthM;
                    edgeTrack[eid] = ti;
                    int rev = revOf[eid]; if (rev >= 0) edgeTrack[rev] = ti;
                    if (segMap.TryGetValue(edges[eid].SegmentId, out var wv)) keys.Add((wv.wayId, wv.vtx));
                }
                tracks.Add(new GraphTrack { TrackKey = Fnv1a64(keys), StartNodeId = sNode, EndNodeId = eNode, LengthM = len, EdgeIds = fwd });
            }

            void Walk(int b, int startEdge)
            {
                var chain = new List<int> { startEdge };
                segDone.Add(edges[startEdge].SegmentId);
                int cur = startEdge;
                int node = edges[cur].ToNodeId;
                int guard = 0;
                while (nodes[node].EdgeIds.Count == 2 && node != b && guard++ <= edgeCount)
                {
                    int next = -1;
                    foreach (int oe in nodes[node].EdgeIds)
                        if (edges[oe].SegmentId != edges[cur].SegmentId) { next = oe; break; }
                    if (next < 0 || segDone.Contains(edges[next].SegmentId)) break;
                    chain.Add(next); segDone.Add(edges[next].SegmentId);
                    cur = next; node = edges[cur].ToNodeId;
                }
                MakeTrack(b, node, chain);
            }

            // 1) chains anchored at boundary nodes (switches / dead-ends)
            for (int n = 0; n < nodeCount; n++)
            {
                if (!Boundary(n)) continue;
                foreach (int oe in nodes[n].EdgeIds)
                    if (!segDone.Contains(edges[oe].SegmentId)) Walk(n, oe);
            }
            // 2) leftover degree-2 cycles (no boundary node) — start anywhere
            for (int e = 0; e < edgeCount; e++)
                if (!segDone.Contains(edges[e].SegmentId)) Walk(edges[e].FromNodeId, e);

            for (int e = 0; e < edgeCount; e++)
                if (edgeTrack[e] >= 0) graph.SetEdgeTrackIndex(e, edgeTrack[e]);

            return tracks;
        }

        private static long Fnv1a64(IEnumerable<(long wayId, int vtx)> items)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                void Mix(ulong x, int bytes)
                {
                    for (int b = 0; b < bytes; b++) { h ^= (x >> (b * 8)) & 0xFF; h *= 1099511628211UL; }
                }
                foreach (var (w, v) in items) { Mix((ulong)w, 8); Mix((uint)v, 4); }
                return (long)h;
            }
        }
    }
}
