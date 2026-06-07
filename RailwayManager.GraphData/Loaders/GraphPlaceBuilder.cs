using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Builder dla CityPlaces z Places layer (place=city|town|village). Dedup po name+type.
    /// Voivodeship resolved jeśli resolver przekazany. Port z Unity PlaceLoader.
    /// </summary>
    public static class GraphPlaceBuilder
    {
        public static List<GraphCityPlace> Build(List<GraphMeshGeometry> features, GraphVoivodeshipResolver? resolver = null)
        {
            var result = new List<GraphCityPlace>();
            if (features == null || features.Count == 0) return result;

            var seen = new HashSet<string>();
            int cities = 0, towns = 0, villages = 0;

            foreach (var feature in features)
            {
                if (feature?.Metadata == null || feature.Vertices == null || feature.Vertices.Count == 0) continue;

                feature.Metadata.TryGetValue("place", out var placeStr);
                if (!TryParsePlaceType(placeStr, out var placeType)) continue;

                feature.Metadata.TryGetValue("name", out var name);
                if (string.IsNullOrEmpty(name)) continue;

                string key = $"{name}|{placeType}";
                if (!seen.Add(key)) continue;

                feature.Metadata.TryGetValue("population", out var popStr);
                int.TryParse(popStr, out int population);

                var pos = feature.Vertices[0];
                result.Add(new GraphCityPlace
                {
                    Name = name,
                    Position = pos,
                    Type = placeType,
                    Population = population,
                    Voivodeship = resolver?.GetVoivodeship(pos)
                });

                if (placeType == GraphPlaceType.City) cities++;
                else if (placeType == GraphPlaceType.Town) towns++;
                else villages++;
            }

            GraphLogger.LogInfo($"[GraphPlaceBuilder] Loaded {result.Count} places ({cities} cities, {towns} towns, {villages} villages)");
            return result;
        }

        private static bool TryParsePlaceType(string raw, out GraphPlaceType type)
        {
            switch (raw)
            {
                case "city":    type = GraphPlaceType.City;    return true;
                case "town":    type = GraphPlaceType.Town;    return true;
                case "village": type = GraphPlaceType.Village; return true;
                default:        type = default;                return false;
            }
        }
    }
}
