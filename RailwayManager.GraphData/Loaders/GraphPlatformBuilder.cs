using System;
using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Builder for StationPlatforms from the Platforms layer (railway=platform). Centroid + snap to the nearest
    /// graph node (mapping to a station). v5: also projects each platform onto the nearest physical track(s) to
    /// produce (TrackIndex, FromM, ToM) entries in that track's 0→Length kilometrage. Island platform = the two
    /// nearest tracks on opposite sides (sign of the track-direction × platform-offset cross product). A platform
    /// with no track within maxTrackDistanceM gets 0 entries (the game falls back to the centroid).
    /// </summary>
    public static class GraphPlatformBuilder
    {
        public static List<GraphStationPlatform> Build(
            List<GraphMeshGeometry> features,
            GraphPathfindingGraph pathGraph,
            List<GraphTrack> tracks,                 // v5
            float maxStationDistanceM = 500f,
            float maxTrackDistanceM = 30f)           // v5: platform→track projection cutoff
        {
            var result = new List<GraphStationPlatform>();
            if (features == null || features.Count == 0 || pathGraph == null) return result;

            var ix = BuildTrackIndex(pathGraph, tracks);

            int nextId = 1;
            int unmatched = 0, withEntries = 0, islands = 0;
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

                var platform = new GraphStationPlatform
                {
                    PlatformId = nextId++,
                    StationNodeId = nearestNode,
                    Position = centroid,
                    PlatformName = platformName ?? "?",
                    TrackRef = trackRef ?? "",
                    LengthM = lengthM
                };

                AssignEntries(platform, feature.Vertices, centroid, ix, maxTrackDistanceM);
                if (platform.Entries.Count > 0) withEntries++;
                if (platform.Entries.Count > 1) islands++;
                result.Add(platform);
            }

            GraphLogger.LogInfo($"[GraphPlatformBuilder] Loaded {result.Count} platforms ({unmatched} unmatched, {withEntries} with track entries, {islands} island).");
            return result;
        }

        // ── v5 platform → track projection ──────────────────────────────────────────

        private struct TrackSeg { public int Track; public GraphPoint A, B; public float ArcAtA, Len; }

        private sealed class TrackIndex
        {
            public List<TrackSeg> Segs = new List<TrackSeg>();
            public Dictionary<long, List<int>> Grid = new Dictionary<long, List<int>>();
            public Dictionary<int, (int start, int count)> TrackRange = new Dictionary<int, (int, int)>();
            public float Cell = 64f;
        }

        private static TrackIndex BuildTrackIndex(GraphPathfindingGraph g, List<GraphTrack> tracks)
        {
            var ix = new TrackIndex();
            if (tracks == null) return ix;
            var nodes = g.Nodes; var edges = g.Edges;
            for (int ti = 0; ti < tracks.Count; ti++)
            {
                var t = tracks[ti];
                int start = ix.Segs.Count;
                float arc = 0f;
                if (t.EdgeIds != null)
                {
                    foreach (int eid in t.EdgeIds)
                    {
                        if (eid < 0 || eid >= edges.Count) continue;
                        var e = edges[eid];
                        var A = nodes[e.FromNodeId].Position;
                        var B = nodes[e.ToNodeId].Position;
                        int idx = ix.Segs.Count;
                        ix.Segs.Add(new TrackSeg { Track = ti, A = A, B = B, ArcAtA = arc, Len = e.LengthM });
                        AddCell(ix, A, idx); AddCell(ix, B, idx);
                        AddCell(ix, new GraphPoint((A.X + B.X) * 0.5f, (A.Y + B.Y) * 0.5f), idx);
                        arc += e.LengthM;
                    }
                }
                ix.TrackRange[ti] = (start, ix.Segs.Count - start);
            }
            return ix;
        }

        private static long CellKey(float x, float y, float cs)
            => ((long)(int)Math.Floor(x / cs) << 32) | (uint)(int)Math.Floor(y / cs);

        private static void AddCell(TrackIndex ix, GraphPoint p, int segIdx)
        {
            long k = CellKey(p.X, p.Y, ix.Cell);
            if (!ix.Grid.TryGetValue(k, out var list)) { list = new List<int>(); ix.Grid[k] = list; }
            list.Add(segIdx);
        }

        private static void AssignEntries(GraphStationPlatform platform, List<GraphPoint> verts, GraphPoint centroid,
                                          TrackIndex ix, float maxDist)
        {
            // platform long axis (PCA) → endpoints P0, P1
            double sxx = 0, sxy = 0, syy = 0;
            foreach (var v in verts) { double dx = v.X - centroid.X, dy = v.Y - centroid.Y; sxx += dx * dx; sxy += dx * dy; syy += dy * dy; }
            double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            double dirX = Math.Cos(theta), dirY = Math.Sin(theta);
            float half = 0f;
            foreach (var v in verts) { float pr = (float)((v.X - centroid.X) * dirX + (v.Y - centroid.Y) * dirY); float a = Math.Abs(pr); if (a > half) half = a; }
            var P0 = new GraphPoint((float)(centroid.X - half * dirX), (float)(centroid.Y - half * dirY));
            var P1 = new GraphPoint((float)(centroid.X + half * dirX), (float)(centroid.Y + half * dirY));

            // candidate tracks: distinct tracks among segments in cells near the centroid
            float reach = maxDist + half + ix.Cell;
            int cellsR = (int)Math.Ceiling(reach / ix.Cell);
            int cx = (int)Math.Floor(centroid.X / ix.Cell), cy = (int)Math.Floor(centroid.Y / ix.Cell);
            var candidates = new HashSet<int>();
            for (int gx = -cellsR; gx <= cellsR; gx++)
                for (int gy = -cellsR; gy <= cellsR; gy++)
                {
                    long k = ((long)(cx + gx) << 32) | (uint)(cy + gy);
                    if (!ix.Grid.TryGetValue(k, out var list)) continue;
                    foreach (int si in list) candidates.Add(ix.Segs[si].Track);
                }
            if (candidates.Count == 0) return;

            // for each candidate track: distance from centroid + side sign + (fromM,toM) from projecting P0,P1
            (int track, float dist, int side, float fromM, float toM) best = (-1, float.MaxValue, 0, 0, 0);
            (int track, float dist, int side, float fromM, float toM) second = (-1, float.MaxValue, 0, 0, 0);
            foreach (int ti in candidates)
            {
                if (!ix.TrackRange.TryGetValue(ti, out var rng) || rng.count == 0) continue;
                ProjectOnTrack(ix, rng, centroid, out float cDist, out _, out int side);
                if (cDist > maxDist) continue;
                ProjectOnTrack(ix, rng, P0, out _, out float a0, out _);
                ProjectOnTrack(ix, rng, P1, out _, out float a1, out _);
                float fromM = Math.Min(a0, a1), toM = Math.Max(a0, a1);
                var cand = (ti, cDist, side, fromM, toM);
                if (cDist < best.dist) { second = best; best = cand; }
                else if (cDist < second.dist) { second = cand; }
            }
            if (best.track < 0) return;
            platform.Entries.Add(new GraphPlatformEntry { TrackIndex = best.track, FromM = best.fromM, ToM = best.toM });
            // island: a second track within maxDist on the opposite side
            if (second.track >= 0 && second.dist <= maxDist && second.side != 0 && best.side != 0 && second.side != best.side)
                platform.Entries.Add(new GraphPlatformEntry { TrackIndex = second.track, FromM = second.fromM, ToM = second.toM });
        }

        private static void ProjectOnTrack(TrackIndex ix, (int start, int count) rng, GraphPoint P,
                                           out float dist, out float arc, out int side)
        {
            dist = float.MaxValue; arc = 0f; side = 0;
            for (int i = rng.start; i < rng.start + rng.count; i++)
            {
                var s = ix.Segs[i];
                float dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y;
                float l2 = dx * dx + dy * dy;
                float t = l2 > 1e-9f ? ((P.X - s.A.X) * dx + (P.Y - s.A.Y) * dy) / l2 : 0f;
                if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                float qx = s.A.X + t * dx, qy = s.A.Y + t * dy;
                float d = (float)Math.Sqrt((P.X - qx) * (P.X - qx) + (P.Y - qy) * (P.Y - qy));
                if (d < dist)
                {
                    dist = d;
                    arc = s.ArcAtA + t * s.Len;
                    float cross = dx * (P.Y - qy) - dy * (P.X - qx);
                    side = cross > 0f ? 1 : (cross < 0f ? -1 : 0);
                }
            }
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
