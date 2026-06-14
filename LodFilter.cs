using System.Numerics;

namespace formap;

/// <summary>
/// Per-LOD feature filtering and the road/place classification helpers it relies on.
/// </summary>
public static class LodFilter
{
    // --- 6-level LOD system ---

    /// <summary> LOD1 (1000-2000): residential+ roads, all buildings, all POIs. No paths/footways/service. </summary>
    public static Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> CreateLODLevel1(
        Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> full)
    {
        var result = new Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>();
        foreach (var (lt, features) in full)
        {
            if (features.Count == 0) continue;
            switch (lt)
            {
                case BinaryFormat.LayerType.Highways:
                    var roads = features.Where(g => IsResidentialOrAbove(g)).ToList();
                    if (roads.Count > 0) result[lt] = roads;
                    break;
                case BinaryFormat.LayerType.Military:
                    break; // skip
                default:
                    result[lt] = features;
                    break;
            }
        }
        return result;
    }

    /// <summary> LOD2 (2000-4000): residential+ roads, large buildings (>100m²), all POIs. No waterways, military. </summary>
    public static Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> CreateLODLevel2(
        Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> full)
    {
        var result = new Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>();
        foreach (var (lt, features) in full)
        {
            if (features.Count == 0) continue;
            switch (lt)
            {
                case BinaryFormat.LayerType.Highways:
                    var roads = features.Where(g => IsResidentialOrAbove(g)).ToList();
                    if (roads.Count > 0) result[lt] = roads;
                    break;
                case BinaryFormat.LayerType.Buildings:
                    var big = features.Where(g => MathF.Abs(PolygonUtils.CalculatePolygonArea(g.Vertices)) >= 100f).ToList();
                    if (big.Count > 0) result[lt] = big;
                    break;
                case BinaryFormat.LayerType.Waterways:
                case BinaryFormat.LayerType.Military:
                case BinaryFormat.LayerType.Coastlines: // LOD0/1 only — used once at init by SyntheticWaterRenderer
                    break; // skip
                default:
                    result[lt] = features;
                    break;
            }
        }
        return result;
    }

    /// <summary> LOD3 (4000-8000): motorway-tertiary, railways, water, forests, waterways. City/town/village. No buildings. </summary>
    public static Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> CreateLODLevel3(
        Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> full)
    {
        var result = new Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>();
        foreach (var (lt, features) in full)
        {
            if (features.Count == 0) continue;
            switch (lt)
            {
                case BinaryFormat.LayerType.Highways:
                    var roads = features.Where(g => IsMainRoad(g)).ToList();
                    if (roads.Count > 0) result[lt] = roads;
                    break;
                case BinaryFormat.LayerType.Places:
                    var placesCtv = features.Where(g => IsCityTownOrVillage(g)).ToList();
                    if (placesCtv.Count > 0) result[lt] = placesCtv;
                    break;
                case BinaryFormat.LayerType.POIs:
                case BinaryFormat.LayerType.Railways:
                case BinaryFormat.LayerType.Water:
                case BinaryFormat.LayerType.Forests:
                case BinaryFormat.LayerType.Waterways:
                case BinaryFormat.LayerType.AdminBoundaries:
                    result[lt] = features;
                    break;
                // Skip: Buildings, Industrial, Military, Platforms
            }
        }
        return result;
    }

    /// <summary> LOD4 (8000-16000): motorway/trunk/primary only, railways, water, forests. City/town. </summary>
    public static Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> CreateLODLevel4(
        Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> full)
    {
        var result = new Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>();
        foreach (var (lt, features) in full)
        {
            if (features.Count == 0) continue;
            switch (lt)
            {
                case BinaryFormat.LayerType.Highways:
                    var roads = features.Where(g => IsMajorRoad(g)).ToList();
                    if (roads.Count > 0) result[lt] = roads;
                    break;
                case BinaryFormat.LayerType.Places:
                    var placesCt = features.Where(g => IsCityOrTown(g)).ToList();
                    if (placesCt.Count > 0) result[lt] = placesCt;
                    break;
                case BinaryFormat.LayerType.POIs:
                case BinaryFormat.LayerType.Railways:
                case BinaryFormat.LayerType.Water:
                case BinaryFormat.LayerType.Forests:
                case BinaryFormat.LayerType.AdminBoundaries:
                    result[lt] = features;
                    break;
                // Skip everything else
            }
        }
        return result;
    }

    /// <summary> LOD5 (>16000): NO roads. Railways, water, forests. City/town only. </summary>
    public static Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> CreateLODLevel5(
        Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> full)
    {
        var result = new Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>();
        foreach (var (lt, features) in full)
        {
            if (features.Count == 0) continue;
            switch (lt)
            {
                case BinaryFormat.LayerType.Places:
                    var placesCt = features.Where(g => IsCityOrTown(g)).ToList();
                    if (placesCt.Count > 0) result[lt] = placesCt;
                    break;
                case BinaryFormat.LayerType.POIs:
                case BinaryFormat.LayerType.Railways:
                case BinaryFormat.LayerType.Water:
                case BinaryFormat.LayerType.Forests:
                case BinaryFormat.LayerType.AdminBoundaries:
                    result[lt] = features;
                    break;
                // Skip: Highways, Buildings, Industrial, Military, Platforms, Waterways
            }
        }
        return result;
    }

    /// <summary>True for city/town/village place tags on Places layer.</summary>
    public static bool IsCityTownOrVillage(MeshGeometry geom)
    {
        if (!geom.Metadata.TryGetValue("place", out var pt)) return false;
        return pt is "city" or "town" or "village";
    }

    /// <summary>True for city/town place tags (no village) — used at far zoom levels.</summary>
    public static bool IsCityOrTown(MeshGeometry geom)
    {
        if (!geom.Metadata.TryGetValue("place", out var pt)) return false;
        return pt is "city" or "town";
    }

    // --- Road classification helpers ---

    /// <summary> Residential and above (no footway, path, service, track, cycleway) </summary>
    public static bool IsResidentialOrAbove(MeshGeometry geom)
    {
        if (!geom.Metadata.TryGetValue("highway", out var hwType) || string.IsNullOrEmpty(hwType))
            return false;
        return hwType is "motorway" or "motorway_link" or "trunk" or "trunk_link"
            or "primary" or "primary_link" or "secondary" or "secondary_link"
            or "tertiary" or "tertiary_link" or "residential" or "living_street"
            or "unclassified" or "road";
    }

    /// <summary> Major road: motorway/trunk/primary (for LOD4) </summary>
    public static bool IsMajorRoad(MeshGeometry geom)
    {
        if (!geom.Metadata.TryGetValue("highway", out var hwType) || string.IsNullOrEmpty(hwType))
            return false;
        return hwType is "motorway" or "motorway_link" or "trunk" or "trunk_link"
            or "primary" or "primary_link";
    }

    /// <summary>
    /// LOD1: only motorway, trunk, primary (motorways, expressways, main urban roads).
    /// Returns false for everything else including features without highway metadata.
    /// </summary>
    public static bool IsMainRoad(MeshGeometry geom)
    {
        if (!geom.Metadata.TryGetValue("highway", out var hwType))
            return false;

        if (string.IsNullOrEmpty(hwType))
            return false;

        return hwType is "motorway" or "motorway_link" or "trunk" or "trunk_link"
            or "primary" or "primary_link" or "secondary" or "secondary_link"
            or "tertiary" or "tertiary_link";
    }
}
