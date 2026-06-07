using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Wylicza Vmax dla railway segment z OSM metadata. Stateless utility.
    /// Port z Unity SegmentSpeedResolver do shared library.
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
