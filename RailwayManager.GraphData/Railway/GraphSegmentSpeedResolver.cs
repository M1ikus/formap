using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Computes Vmax for a railway segment from OSM metadata. Stateless utility.
    /// Ported from Unity's SegmentSpeedResolver to the shared library.
    /// </summary>
    public static class GraphSegmentSpeedResolver
    {
        public static int GetMaxSpeedKmh(Dictionary<string, string> metadata)
        {
            if (metadata == null) return GraphLineUsageSpeedCatalog.Unknown;

            if (metadata.TryGetValue("maxspeed", out var rawMaxSpeed))
            {
                int parsed = GraphLineUsageSpeedCatalog.ParseMaxSpeed(rawMaxSpeed);
                if (parsed > 0) return parsed;
            }

            metadata.TryGetValue("usage", out var usage);
            metadata.TryGetValue("service", out var service);
            return GraphLineUsageSpeedCatalog.GetFallbackSpeed(usage, service);
        }
    }
}
