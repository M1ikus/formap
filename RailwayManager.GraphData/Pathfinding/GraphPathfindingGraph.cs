using System;
using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Graf pathfindingu zbudowany z Railways MeshGeometry. Port z Unity PathfindingGraph
    /// do shared library — używany w pre-build w formap, Unity loaduje wynik z init-state.bin.
    ///
    /// Kluczowa metoda: <see cref="BuildFromFeaturesUnionFind"/> — Union-Find merging
    /// raw vertices które są blisko siebie (junctions, endpoints), z guards
    /// (perpendicular offset + track_ref) blokującymi false merges między równoległymi torami.
    ///
    /// MVP: Edge.Geometry = null (eliminuje 100k+ List allocs w Step 4 build edges).
    /// Wizualizacja trasy: prosta linia node→node lub on-demand reconstruct.
    /// </summary>
    public class GraphPathfindingGraph
    {
        private readonly List<GraphNode> _nodes = new List<GraphNode>();
        private readonly List<GraphEdge> _edges = new List<GraphEdge>();
        private readonly Dictionary<long, List<int>> _spatialGrid = new Dictionary<long, List<int>>();

        public float CellSize { get; private set; } = 1.0f;

        public IReadOnlyList<GraphNode> Nodes => _nodes;
        public IReadOnlyList<GraphEdge> Edges => _edges;
        public int NodeCount => _nodes.Count;
        public int EdgeCount => _edges.Count;

        public HashSet<int> JunctionNodeIds { get; private set; } = new HashSet<int>();

        // ─────────────────────────────────────────────
        //  Builder — Union-Find based
        // ─────────────────────────────────────────────

        /// <summary>
        /// Buduje graf z railway features. Algorytm:
        /// 1. Każdy vertex każdego feature startuje jako osobny "raw node"
        /// 2. Union-Find merguje raw nodes których pozycje są w zadanej tolerancji (cellSizeM)
        /// 3. Finalne PathfindingGraph nodes = UF components (centroid pozycji)
        /// 4. Edges z feature chain — segment per pair consecutive vertices, deduplikacja
        /// </summary>
        /// <param name="junctionOnlyMerge">Gdy true, merging między wayami tylko na junction
        /// vertices (OSM shared nodes) lub endpoint'ach. Zapobiega fałszywym skrótom.</param>
        public void BuildFromFeaturesUnionFind(List<GraphMeshGeometry> railwayFeatures,
            float cellSizeM = 10f, bool junctionOnlyMerge = true)
        {
            CellSize = cellSizeM;
            _nodes.Clear();
            _edges.Clear();
            _spatialGrid.Clear();

            if (railwayFeatures == null) return;

            // Pre-pass: count total vertices for List capacity (eliminuje N reallocs)
            int totalVertices = 0;
            for (int fi = 0; fi < railwayFeatures.Count; fi++)
            {
                var f = railwayFeatures[fi];
                if (f != null && f.Vertices != null && f.Vertices.Count >= 2)
                    totalVertices += f.Vertices.Count;
            }
            GraphLogger.LogInfo($"[GraphPathfindingGraph] Pre-pass: {railwayFeatures.Count} features → {totalVertices} total vertices");

            // Step 1: zbierz raw nodes + junction flags + endpoint flags + track_ref + direction
            var rawPositions = new List<GraphPoint>(totalVertices);
            var rawIsJunction = new List<bool>(totalVertices);
            var rawIsEndpoint = new List<bool>(totalVertices);
            var rawTrackRef = new List<string?>(totalVertices);
            var rawDirection = new List<GraphPoint>(totalVertices);
            int[]?[] featureVertexIds = new int[railwayFeatures.Count][];
            int featuresWithTrackRef = 0;

            for (int fi = 0; fi < railwayFeatures.Count; fi++)
            {
                var f = railwayFeatures[fi];
                if (f == null || f.Vertices == null || f.Vertices.Count < 2)
                {
                    featureVertexIds[fi] = null;
                    continue;
                }

                var jSet = (f.JunctionIndices != null && f.JunctionIndices.Count > 0)
                    ? new HashSet<int>(f.JunctionIndices) : null;

                string? featureTrackRef = null;
                if (f.Metadata != null)
                    f.Metadata.TryGetValue("railway:track_ref", out featureTrackRef);
                if (!string.IsNullOrEmpty(featureTrackRef)) featuresWithTrackRef++;

                int lastVi = f.Vertices.Count - 1;
                int[] ids = new int[f.Vertices.Count];
                for (int vi = 0; vi < f.Vertices.Count; vi++)
                {
                    ids[vi] = rawPositions.Count;
                    rawPositions.Add(f.Vertices[vi]);
                    rawIsJunction.Add(jSet != null && jSet.Contains(vi));
                    rawIsEndpoint.Add(vi == 0 || vi == lastVi);
                    rawTrackRef.Add(featureTrackRef);

                    GraphPoint dir;
                    if (vi == 0)
                        dir = f.Vertices[1] - f.Vertices[0];
                    else if (vi == lastVi)
                        dir = f.Vertices[vi] - f.Vertices[vi - 1];
                    else
                        dir = f.Vertices[vi + 1] - f.Vertices[vi - 1];
                    float mag = dir.Magnitude;
                    rawDirection.Add(mag > 0.001f ? dir / mag : new GraphPoint(1f, 0f));
                }
                featureVertexIds[fi] = ids;
            }
            GraphLogger.LogInfo($"[GraphPathfindingGraph] Step 1 done. Features with track_ref: {featuresWithTrackRef}/{railwayFeatures.Count}");

            // Step 2: Union-Find merge (junction↔junction + endpoint↔endpoint, z guards)
            const float perpThresholdM = 1.0f;
            var uf = new GraphUnionFind(rawPositions.Count);
            var cellMap = new Dictionary<long, List<int>>();
            float toleranceSq = cellSizeM * cellSizeM;
            int mergesByJunction = 0, mergesByEndpoint = 0;
            int blockedByTrackRef = 0, blockedByPerp = 0;

            for (int i = 0; i < rawPositions.Count; i++)
            {
                var pos = rawPositions[i];
                int cx = (int)Math.Floor(pos.X / cellSizeM);
                int cy = (int)Math.Floor(pos.Y / cellSizeM);
                long selfKey = ((long)(uint)cx << 32) | (uint)cy;
                if (!cellMap.TryGetValue(selfKey, out var list))
                {
                    list = new List<int>();
                    cellMap[selfKey] = list;
                }
                list.Add(i);

                bool iMergeable = !junctionOnlyMerge || rawIsJunction[i] || rawIsEndpoint[i];
                if (!iMergeable) continue;

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    long key = ((long)(uint)(cx + dx) << 32) | (uint)(cy + dy);
                    if (!cellMap.TryGetValue(key, out var candidates)) continue;
                    foreach (int j in candidates)
                    {
                        if (j == i) continue;
                        bool jMergeable = !junctionOnlyMerge || rawIsJunction[j] || rawIsEndpoint[j];
                        if (!jMergeable) continue;
                        if (GraphPoint.SqrDistance(rawPositions[j], pos) <= toleranceSq)
                        {
                            // Guard 1: Perpendicular offset
                            GraphPoint offset = rawPositions[j] - pos;
                            GraphPoint perpI = new GraphPoint(-rawDirection[i].Y, rawDirection[i].X);
                            float perpDist = Math.Abs(GraphPoint.Dot(offset, perpI));
                            if (perpDist > perpThresholdM) { blockedByPerp++; continue; }

                            // Guard 2: Track-ref (tylko endpoint↔endpoint)
                            if (!rawIsJunction[i] && !rawIsJunction[j])
                            {
                                var trI = rawTrackRef[i];
                                var trJ = rawTrackRef[j];
                                if (!string.IsNullOrEmpty(trI) && !string.IsNullOrEmpty(trJ) && trI != trJ)
                                { blockedByTrackRef++; continue; }
                            }

                            if (uf.Find(i) != uf.Find(j))
                            {
                                if (rawIsJunction[i] || rawIsJunction[j]) mergesByJunction++;
                                else mergesByEndpoint++;
                            }
                            uf.Union(i, j);
                        }
                    }
                }
            }
            GraphLogger.LogInfo($"[GraphPathfindingGraph] Step 2 (Union-Find) merges: {mergesByJunction} junction, "
                     + $"{mergesByEndpoint} endpoint | blocked: {blockedByPerp} perp, {blockedByTrackRef} track_ref");

            // Step 2.5: Rescue merge — pair raw vertices w RÓŻNYCH komponentach i blisko (<2.5m)
            const float rescueRadiusM = 2.5f;
            float rescueRadiusSq = rescueRadiusM * rescueRadiusM;
            int rescueMerges = 0, rescueBlockedByPerp = 0, rescueBlockedByTrackRef = 0;
            for (int i = 0; i < rawPositions.Count; i++)
            {
                var pos = rawPositions[i];
                int cx = (int)Math.Floor(pos.X / cellSizeM);
                int cy = (int)Math.Floor(pos.Y / cellSizeM);
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    long key = ((long)(uint)(cx + dx) << 32) | (uint)(cy + dy);
                    if (!cellMap.TryGetValue(key, out var candidates)) continue;
                    foreach (int j in candidates)
                    {
                        if (j <= i) continue;
                        if (GraphPoint.SqrDistance(rawPositions[j], pos) > rescueRadiusSq) continue;
                        if (uf.Find(i) == uf.Find(j)) continue;

                        GraphPoint offset = rawPositions[j] - pos;
                        GraphPoint perpI = new GraphPoint(-rawDirection[i].Y, rawDirection[i].X);
                        float perpDist = Math.Abs(GraphPoint.Dot(offset, perpI));
                        if (perpDist > perpThresholdM) { rescueBlockedByPerp++; continue; }

                        var trI = rawTrackRef[i];
                        var trJ = rawTrackRef[j];
                        if (!string.IsNullOrEmpty(trI) && !string.IsNullOrEmpty(trJ) && trI != trJ)
                        { rescueBlockedByTrackRef++; continue; }

                        uf.Union(i, j);
                        rescueMerges++;
                    }
                }
            }
            GraphLogger.LogInfo($"[GraphPathfindingGraph] Step 2.5 rescue merges: {rescueMerges} | "
                     + $"blocked: {rescueBlockedByPerp} perp, {rescueBlockedByTrackRef} track_ref");

            // Step 3: Centroid pozycji per UF component + tworzenie nodes + spatial grid
            JunctionNodeIds = new HashSet<int>();
            var componentToNode = new Dictionary<int, int>();
            var componentSum = new Dictionary<int, GraphPoint>();
            var componentCount = new Dictionary<int, int>();

            for (int i = 0; i < rawPositions.Count; i++)
            {
                int root = uf.Find(i);
                if (!componentSum.ContainsKey(root))
                {
                    componentSum[root] = rawPositions[i];
                    componentCount[root] = 1;
                }
                else
                {
                    componentSum[root] += rawPositions[i];
                    componentCount[root]++;
                }
            }

            for (int i = 0; i < rawPositions.Count; i++)
            {
                int root = uf.Find(i);
                if (!componentToNode.ContainsKey(root))
                {
                    int nodeId = _nodes.Count;
                    var pos = componentCount[root] > 1
                        ? componentSum[root] / componentCount[root]
                        : rawPositions[root];

                    _nodes.Add(new GraphNode
                    {
                        Id = nodeId,
                        Position = pos,
                        EdgeIds = new List<int>(4) // pre-alloc capacity
                    });
                    componentToNode[root] = nodeId;

                    long spatialKey = CellKey(pos);
                    if (!_spatialGrid.TryGetValue(spatialKey, out var gridList))
                    {
                        gridList = new List<int>();
                        _spatialGrid[spatialKey] = gridList;
                    }
                    gridList.Add(nodeId);
                }

                if (rawIsJunction[i])
                    JunctionNodeIds.Add(componentToNode[root]);
            }
            GraphLogger.LogInfo($"[GraphPathfindingGraph] Step 3 done — {_nodes.Count} nodes, {JunctionNodeIds.Count} junctions");

            // Step 4: Build edges z feature chain (skip geometry storage dla MVP)
            var edgeKey = new HashSet<long>();
            int estimatedEdges = railwayFeatures.Count * 30;
            if (_edges.Capacity < estimatedEdges) _edges.Capacity = estimatedEdges;

            for (int fi = 0; fi < railwayFeatures.Count; fi++)
            {
                var f = railwayFeatures[fi];
                var ids = featureVertexIds[fi];
                if (ids == null) continue;

                int maxSpeed = GraphSegmentSpeedResolver.GetMaxSpeedKmh(f.Metadata);
                float lengthAccum = 0f;
                int prevNode = componentToNode[uf.Find(ids[0])];
                int prevVi = 0;

                for (int vi = 1; vi < ids.Length; vi++)
                {
                    lengthAccum += GraphPoint.Distance(f.Vertices[vi - 1], f.Vertices[vi]);

                    int currNode = componentToNode[uf.Find(ids[vi])];
                    if (currNode == prevNode) continue;

                    long forwardKey = ((long)(uint)prevNode << 32) | (uint)currNode;
                    if (edgeKey.Add(forwardKey))
                    {
                        long reverseKey = ((long)(uint)currNode << 32) | (uint)prevNode;
                        edgeKey.Add(reverseKey);

                        int segId = (f.SegmentIds != null && prevVi < f.SegmentIds.Count)
                            ? f.SegmentIds[prevVi] : 0;

                        AddEdge(prevNode, currNode, segId, lengthAccum, maxSpeed, f.Metadata, isOsmForward: true);
                        AddEdge(currNode, prevNode, segId, lengthAccum, maxSpeed, f.Metadata, isOsmForward: false);
                    }

                    lengthAccum = 0f;
                    prevNode = currNode;
                    prevVi = vi;
                }
            }
            GraphLogger.LogInfo($"[GraphPathfindingGraph] Step 4 done — {_edges.Count} edges");
        }

        private int AddEdge(int from, int to, int segmentId, float length, int maxSpeed,
                            Dictionary<string, string> metadata, bool isOsmForward)
        {
            int edgeId = _edges.Count;
            _edges.Add(new GraphEdge
            {
                Id = edgeId,
                FromNodeId = from,
                ToNodeId = to,
                SegmentId = segmentId,
                LengthM = length,
                MaxSpeedKmh = maxSpeed,
                Metadata = metadata,
                Geometry = null, // MVP: skip geometry
                IsOsmForward = isOsmForward
            });
            _nodes[from].EdgeIds.Add(edgeId);
            return edgeId;
        }

        // ─────────────────────────────────────────────
        //  Spatial grid helpers
        // ─────────────────────────────────────────────

        private long CellKey(GraphPoint pos)
        {
            int cx = (int)Math.Floor(pos.X / CellSize);
            int cy = (int)Math.Floor(pos.Y / CellSize);
            return ((long)(uint)cx << 32) | (uint)cy;
        }

        /// <summary>
        /// Populate graph z deserialized data (Unity load init-state.bin). Rebuilds
        /// spatial grid z node positions.
        /// </summary>
        public void LoadFromSerializedData(List<GraphNode> nodes, List<GraphEdge> edges,
            HashSet<int> junctionIds, float cellSize)
        {
            CellSize = cellSize;
            _nodes.Clear();
            _edges.Clear();
            _spatialGrid.Clear();
            JunctionNodeIds = junctionIds ?? new HashSet<int>();

            _nodes.AddRange(nodes);
            _edges.AddRange(edges);

            for (int i = 0; i < _nodes.Count; i++)
            {
                long key = CellKey(_nodes[i].Position);
                if (!_spatialGrid.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    _spatialGrid[key] = list;
                }
                list.Add(i);
            }
            GraphLogger.LogInfo($"[GraphPathfindingGraph] LoadFromSerializedData: {_nodes.Count} nodes, {_edges.Count} edges, {JunctionNodeIds.Count} junctions, spatial cells: {_spatialGrid.Count}");
        }

        /// <summary>Znajduje najbliższy node w promieniu maxRadiusM. Zwraca -1 gdy brak.</summary>
        public int FindNearestNode(GraphPoint position, float maxRadiusM)
        {
            int cellRadius = (int)Math.Ceiling(maxRadiusM / CellSize);
            int cx = (int)Math.Floor(position.X / CellSize);
            int cy = (int)Math.Floor(position.Y / CellSize);

            int bestNode = -1;
            float bestDistSq = maxRadiusM * maxRadiusM;

            for (int dx = -cellRadius; dx <= cellRadius; dx++)
            for (int dy = -cellRadius; dy <= cellRadius; dy++)
            {
                long key = ((long)(uint)(cx + dx) << 32) | (uint)(cy + dy);
                if (!_spatialGrid.TryGetValue(key, out var nodeIds)) continue;
                foreach (int nodeId in nodeIds)
                {
                    float distSq = GraphPoint.SqrDistance(_nodes[nodeId].Position, position);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestNode = nodeId;
                    }
                }
            }
            return bestNode;
        }
    }
}
