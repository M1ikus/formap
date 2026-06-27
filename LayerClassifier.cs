using OsmSharp;
using OsmSharp.Tags;

namespace formap;

/// <summary>
/// Tag/relation predicates that classify OSM features into map layers.
/// </summary>
public static class LayerClassifier
{
    /// <summary>
    /// Checks if tags represent an administrative boundary (country or voivodeship).
    /// Only admin_level 2 (country) and 4 (voivodeship in Poland) are kept — lower levels are too fine-grained.
    /// </summary>
    public static bool IsAdminBoundary(TagsCollectionBase tags)
    {
        if (tags == null) return false;
        if (!tags.ContainsKey("boundary") || tags["boundary"] != "administrative") return false;
        if (!tags.ContainsKey("admin_level")) return false;

        string level = tags["admin_level"];
        return level == "2" || level == "4";
    }

    public static bool IsBuilding(TagsCollectionBase tags)
    {
        return tags.ContainsKey("building") || tags.ContainsKey("building:part");
    }

    public static bool IsWaterFeature(TagsCollectionBase tags)
    {
        // natural=water polygon (lake, pond, etc.)
        // natural=bay — sea bays (Gulf of Gdańsk, lagoons such as the Vistula Lagoon)
        if (tags.ContainsKey("natural"))
        {
            string naturalType = tags["natural"];
            if (naturalType == "water" || naturalType == "bay")
                return true;
        }

        // place=sea — seas as multipolygon relations (the Baltic, if present in the PBF)
        if (tags.ContainsKey("place") && tags["place"] == "sea")
            return true;

        // water=* tag indicates a water body polygon (lagoon, lake, pond, etc.)
        if (tags.ContainsKey("water"))
            return true;

        // landuse=reservoir is a polygon
        if (tags.ContainsKey("landuse") && tags["landuse"] == "reservoir")
            return true;

        // For waterway tag, only polygon types are valid for triangulation
        // riverbank and dock are polygons, others (river, stream, canal, etc.) are lines
        if (tags.ContainsKey("waterway"))
        {
            string waterwayType = tags["waterway"];
            // Only these waterway types are polygons:
            return waterwayType == "riverbank" ||
                   waterwayType == "dock" ||
                   waterwayType == "boatyard" ||
                   waterwayType == "dam";
        }

        return false;
    }

    /// <summary>
    /// Checks if tags represent a waterway line (river, stream, canal, etc.)
    /// These are rendered as lines with width, not filled polygons
    /// </summary>
    public static bool IsWaterwayLine(TagsCollectionBase tags)
    {
        if (!tags.ContainsKey("waterway"))
            return false;

        string waterwayType = tags["waterway"];
        // Line-type waterways (not polygons)
        return waterwayType == "river" ||
               waterwayType == "stream" ||
               waterwayType == "canal" ||
               waterwayType == "drain" ||
               waterwayType == "ditch" ||
               waterwayType == "brook";
    }

    public static bool IsIndustrialArea(TagsCollectionBase tags)
    {
        return tags.ContainsKey("landuse") &&
               (tags["landuse"] == "industrial" ||
                tags["landuse"] == "commercial" ||
                tags["landuse"] == "retail" ||
                tags["landuse"] == "warehouse") ||
               tags.ContainsKey("amenity") && tags["amenity"] == "industrial";
    }

    public static bool IsMilitaryArea(TagsCollectionBase tags)
    {
        return tags.ContainsKey("military") ||
               tags.ContainsKey("landuse") && tags["landuse"] == "military" ||
               tags.ContainsKey("landuse") && tags["landuse"] == "military:airfield";
    }

    public static bool IsPlatform(TagsCollectionBase tags)
    {
        return tags.ContainsKey("public_transport") && tags["public_transport"] == "platform" ||
               tags.ContainsKey("railway") && tags["railway"] == "platform" ||
               tags.ContainsKey("highway") && tags["highway"] == "platform";
    }

    public static bool IsHighway(TagsCollectionBase tags)
    {
        return tags.ContainsKey("highway");
    }

    /// <summary>
    /// natural=coastline — OSM sea shoreline. A way (line, not a polygon).
    /// Used by Unity's SyntheticWaterRenderer to generate meshes for the Baltic and the Vistula Lagoon
    /// (the Poland PBF does not contain natural=water for the Baltic — the relation is too large).
    /// </summary>
    public static bool IsCoastline(TagsCollectionBase tags)
    {
        return tags.ContainsKey("natural") && tags["natural"] == "coastline";
    }

    public static bool IsRailway(TagsCollectionBase tags)
    {
        // Exclude ferry routes.
        if (tags.ContainsKey("route") && tags["route"] == "ferry") return false;
        if (tags.ContainsKey("ferry")) return false;

        if (!tags.ContainsKey("railway"))
            return false;

        string railwayType = tags["railway"];

        // Skip projected/planned/construction — not playable.
        if (railwayType == "construction" || railwayType == "proposed" || railwayType == "planned")
            return false;

        // Disused/abandoned: accept mainline rail (Unity renders it gray),
        // SKIP disused tram/narrow_gauge/subway/light_rail/monorail (the user does not want to see unused trams/narrow-gauge lines).
        if (railwayType == "disused" || railwayType == "abandoned")
        {
            string? originalType = null;
            if (tags.ContainsKey("disused:railway")) originalType = tags["disused:railway"];
            else if (tags.ContainsKey("abandoned:railway")) originalType = tags["abandoned:railway"];
            if (originalType == "tram" || originalType == "subway" || originalType == "monorail"
                || originalType == "narrow_gauge" || originalType == "light_rail")
                return false;
            return true; // disused mainline rail → accept, rendered gray
        }

        // ACTIVE tram/subway/monorail/narrow_gauge/light_rail — included in the bin.
        // Unity's MapRenderer hides them at LOD>2 (transitOnly branch).

        // Skip POI types (station/halt/signal/platform rendered via the POI layer).
        return railwayType != "platform" &&
               railwayType != "station" &&
               railwayType != "halt" &&
               railwayType != "signal";
    }

    public static bool IsForest(TagsCollectionBase tags)
    {
        return tags.ContainsKey("landuse") && tags["landuse"] == "forest" ||
               tags.ContainsKey("natural") && tags["natural"] == "wood";
    }

    public static bool IsMultipolygon(Relation relation)
    {
        if (relation.Tags == null)
            return false;

        // Standard multipolygon (buildings, water, forests, etc.)
        if (relation.Tags.ContainsKey("type") && relation.Tags["type"] == "multipolygon")
            return true;

        // Administrative boundary relations (country, voivodeship) — also treated as multipolygons
        // so ProcessMultipolygon can build closed rings from outer/inner ways.
        // OSM relation for a voivodeship has type=boundary + boundary=administrative + admin_level=2|4.
        if (relation.Tags.ContainsKey("type") && relation.Tags["type"] == "boundary"
            && IsAdminBoundary(relation.Tags))
            return true;

        return false;
    }

    public static bool IsWaterwayRelation(Relation relation)
    {
        if (relation.Tags == null)
            return false;

        // Check if relation has waterway tag (river, stream, canal, etc.)
        // Relations can represent multi-segment waterways (e.g., long rivers)
        return relation.Tags.ContainsKey("waterway") &&
               (relation.Tags["waterway"] == "river" ||
                relation.Tags["waterway"] == "stream" ||
                relation.Tags["waterway"] == "canal" ||
                relation.Tags["waterway"] == "drain" ||
                relation.Tags["waterway"] == "ditch" ||
                relation.Tags["waterway"] == "brook");
    }

    /// <summary>
    /// Detect railway *infrastructure* route relations — those whose `ref` is a physical PKP PLK
    /// line number (the "lk" number the game shows). These are OSM `type=route` + `route=tracks|railway`.
    ///
    /// `route=train` is deliberately EXCLUDED: it tags a *carrier's passenger service* (Polregio,
    /// Koleje Śląskie, PKP Intercity, …) whose `ref` is a service/brand code ("S51", "1", "IC38172"),
    /// NOT a PLK infrastructure line number. Propagating those refs painted every track a train runs
    /// over with the service number — e.g. Sucha Beskidzka→Zakopane wrongly showed lk51 (Koleje Śląskie
    /// S51) and lk1 (Polregio "Podhalańska Kolej Regionalna", ref=1) on top of the real lk98/lk99.
    /// Verified against poland-260613.osm.pbf: 0 of 606 `route=train` relations are operated by PKP PLK,
    /// so excluding them never drops a real line number — every PLK line lives on route=railway/tracks.
    /// </summary>
    public static bool IsRailwayRouteRelation(Relation relation)
    {
        if (relation.Tags == null) return false;
        if (!relation.Tags.ContainsKey("type") || relation.Tags["type"] != "route") return false;
        if (!relation.Tags.ContainsKey("route")) return false;
        var route = relation.Tags["route"];
        // Do NOT add "train" here — that re-introduces the lk51/lk1 service-ref bug (see summary above).
        return route == "tracks" || route == "railway";
    }
}
