namespace RailwayManager.GraphData
{
    /// <summary>
    /// Union-Find (disjoint set union) with path compression and union by rank.
    /// Used in PathfindingGraph.BuildFromFeaturesUnionFind to merge
    /// raw vertices that are close together (junctions, endpoints).
    ///
    /// Ported from Unity's RailwayManager.Timetable.UnionFind to the shared library.
    /// O(α(N)) per Find/Union (amortized inverse-Ackermann ≈ O(1)).
    /// </summary>
    public class GraphUnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _rank;

        public GraphUnionFind(int size)
        {
            _parent = new int[size];
            _rank = new int[size];
            for (int i = 0; i < size; i++) _parent[i] = i;
        }

        public int Find(int x)
        {
            // Path compression
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]]; // halve the path
                x = _parent[x];
            }
            return x;
        }

        public bool Union(int x, int y)
        {
            int rx = Find(x);
            int ry = Find(y);
            if (rx == ry) return false;

            // Union by rank
            if (_rank[rx] < _rank[ry]) (rx, ry) = (ry, rx);
            _parent[ry] = rx;
            if (_rank[rx] == _rank[ry]) _rank[rx]++;
            return true;
        }
    }
}
