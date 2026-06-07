using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Builder for AdminRegions from AdminBoundaries layer features. Deduplicates by name
    /// (formap replicates a feature up to the tile boundary). Ported from Unity's AdminBoundaryLoader.
    /// </summary>
    public static class GraphAdminBoundaryBuilder
    {
        public static List<GraphAdminRegion> Build(List<GraphMeshGeometry> features)
        {
            var result = new List<GraphAdminRegion>();
            if (features == null || features.Count == 0) return result;

            var seen = new HashSet<string>();
            int countries = 0, voivodeships = 0;

            foreach (var feature in features)
            {
                if (feature?.Metadata == null || feature.Vertices == null || feature.Indices == null) continue;
                if (feature.Vertices.Count < 3 || feature.Indices.Count < 3) continue;

                feature.Metadata.TryGetValue("name", out var name);
                if (string.IsNullOrEmpty(name)) continue;
                if (!seen.Add(name)) continue;

                feature.Metadata.TryGetValue("admin_level", out var levelStr);
                int.TryParse(levelStr, out int adminLevel);
                if (adminLevel != 2 && adminLevel != 4) continue;

                feature.Metadata.TryGetValue("ISO3166-1", out var iso1);
                feature.Metadata.TryGetValue("ISO3166-2", out var iso2);

                result.Add(new GraphAdminRegion
                {
                    Name = name,
                    AdminLevel = adminLevel,
                    Iso3166_1 = iso1,
                    Iso3166_2 = iso2,
                    BoundingBox = feature.BoundingBox,
                    Vertices = new List<GraphPoint>(feature.Vertices),
                    Indices = new List<int>(feature.Indices)
                });

                if (adminLevel == 2) countries++; else voivodeships++;
            }

            GraphLogger.LogInfo($"[GraphAdminBoundaryBuilder] Loaded {result.Count} regions ({countries} countries, {voivodeships} voivodeships)");
            return result;
        }
    }
}
