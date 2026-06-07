using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Buduje odcinki blokowe z PathfindingGraph. Granice = stacje (boundaryNodeIds) +
    /// rozjazdy (3+ edges) + dead-ends. Port z Unity BlockSectionBuilder.
    /// </summary>
    public static class GraphBlockSectionBuilder
    {
        public struct BuildResult
        {
            public List<GraphBlockSection> Sections;
            public int[] EdgeToSection; // edgeId → sectionId, -1 gdy brak
        }

        public static BuildResult Build(GraphPathfindingGraph graph, HashSet<int> boundaryNodeIds)
        {
            var result = new BuildResult
            {
                Sections = new List<GraphBlockSection>(),
                EdgeToSection = new int[graph != null ? graph.EdgeCount : 0]
            };

            if (graph == null || graph.EdgeCount == 0) return result;

            for (int i = 0; i < result.EdgeToSection.Length; i++)
                result.EdgeToSection[i] = -1;

            var boundaryType = new Dictionary<int, GraphBoundaryType>();
            if (boundaryNodeIds != null)
            {
                foreach (int sn in boundaryNodeIds)
                {
                    if (sn >= 0 && sn < graph.NodeCount)
                        boundaryType[sn] = GraphBoundaryType.Station;
                }
            }

            int nextId = 0;
            for (int e = 0; e < graph.EdgeCount; e++)
            {
                if (result.EdgeToSection[e] >= 0) continue;

                var startEdge = graph.Edges[e];
                string trackRef = GetTrackRef(startEdge);

                var chain = BuildChain(graph, e, trackRef, boundaryType, result.EdgeToSection);
                if (chain.EdgeIds.Count == 0) continue;

                int sectionId = nextId++;
                float totalLength = 0f;
                int minSpeed = int.MaxValue;

                foreach (int eid in chain.EdgeIds)
                {
                    result.EdgeToSection[eid] = sectionId;
                    var edge = graph.Edges[eid];
                    totalLength += edge.LengthM;
                    if (edge.MaxSpeedKmh > 0 && edge.MaxSpeedKmh < minSpeed)
                        minSpeed = edge.MaxSpeedKmh;
                }
                if (minSpeed == int.MaxValue) minSpeed = 80;

                boundaryType.TryGetValue(chain.StartNodeId, out var startBnd);
                boundaryType.TryGetValue(chain.EndNodeId, out var endBnd);

                result.Sections.Add(new GraphBlockSection
                {
                    Id = sectionId,
                    StartNodeId = chain.StartNodeId,
                    EndNodeId = chain.EndNodeId,
                    LengthM = totalLength,
                    MaxSpeedKmh = minSpeed,
                    EdgeCount = chain.EdgeIds.Count,
                    StartBoundary = startBnd,
                    EndBoundary = endBnd
                });
            }

            float avgLen = 0;
            if (result.Sections.Count > 0)
            {
                float sum = 0;
                foreach (var s in result.Sections) sum += s.LengthM;
                avgLen = sum / result.Sections.Count;
            }
            GraphLogger.LogInfo($"[GraphBlockSectionBuilder] Built {result.Sections.Count} sections, "
                + $"avg length {avgLen:F0}m, boundary nodes {boundaryType.Count}");

            return result;
        }

        private struct ChainResult
        {
            public List<int> EdgeIds;
            public int StartNodeId;
            public int EndNodeId;
        }

        private static ChainResult BuildChain(
            GraphPathfindingGraph graph, int seedEdgeId, string trackRef,
            Dictionary<int, GraphBoundaryType> boundaries, int[] edgeToSection)
        {
            var result = new ChainResult { EdgeIds = new List<int>() };
            var seedEdge = graph.Edges[seedEdgeId];

            var forwardEdges = new List<int>();
            int forwardEndNode = Extend(graph, seedEdge.ToNodeId, seedEdgeId, trackRef,
                boundaries, edgeToSection, forwardEdges);

            var backwardEdges = new List<int>();
            int backwardEndNode = Extend(graph, seedEdge.FromNodeId, seedEdgeId, trackRef,
                boundaries, edgeToSection, backwardEdges);

            backwardEdges.Reverse();
            result.EdgeIds.AddRange(backwardEdges);
            result.EdgeIds.Add(seedEdgeId);
            result.EdgeIds.AddRange(forwardEdges);
            result.StartNodeId = backwardEndNode;
            result.EndNodeId = forwardEndNode;
            return result;
        }

        private static int Extend(
            GraphPathfindingGraph graph, int currentNode, int prevEdgeId,
            string trackRef, Dictionary<int, GraphBoundaryType> boundaries,
            int[] edgeToSection, List<int> collectedEdges)
        {
            if (boundaries.ContainsKey(currentNode)) return currentNode;

            int maxSteps = 50000;
            var seedEdge = graph.Edges[prevEdgeId];
            int prevNode = (seedEdge.ToNodeId == currentNode) ? seedEdge.FromNodeId : seedEdge.ToNodeId;

            while (maxSteps-- > 0)
            {
                var node = graph.Nodes[currentNode];
                if (node.EdgeIds == null) break;

                int nextEdge = -1;
                int nextNode = -1;
                foreach (int eid in node.EdgeIds)
                {
                    if (edgeToSection[eid] >= 0) continue;
                    var edge = graph.Edges[eid];
                    int other = edge.FromNodeId == currentNode ? edge.ToNodeId : edge.FromNodeId;
                    if (other == prevNode) continue;

                    string edgeTrack = GetTrackRef(edge);
                    if (!string.IsNullOrEmpty(trackRef) && !string.IsNullOrEmpty(edgeTrack)
                        && edgeTrack != trackRef) continue;

                    nextEdge = eid;
                    nextNode = other;
                    break;
                }

                if (nextEdge < 0) break;
                collectedEdges.Add(nextEdge);
                prevNode = currentNode;
                currentNode = nextNode;
                if (boundaries.ContainsKey(currentNode)) return currentNode;
            }
            return currentNode;
        }

        private static string GetTrackRef(GraphEdge edge)
        {
            if (edge.Metadata == null) return "";
            edge.Metadata.TryGetValue("railway:track_ref", out var tr);
            return tr ?? "";
        }
    }
}
