using System.Numerics;

namespace formap;

/// <summary>
/// Geometry-math helpers for polygon ring classification and area computation.
/// </summary>
public static class PolygonUtils
{
    /// <summary>
    /// Checks if inner ring is inside outer ring using multiple test points for robustness.
    /// Tests centroid and several vertices - majority vote determines result.
    /// </summary>
    public static bool IsRingInsideRing(List<Vector2> inner, List<Vector2> outer)
    {
        if (inner.Count == 0 || outer.Count < 3)
            return false;

        // Calculate centroid of inner ring
        float cx = 0, cy = 0;
        foreach (var p in inner)
        {
            cx += p.X;
            cy += p.Y;
        }
        cx /= inner.Count;
        cy /= inner.Count;
        var centroid = new Vector2(cx, cy);

        // Test multiple points: centroid + evenly distributed vertices
        int insideCount = 0;
        int totalTests = 0;

        // Test centroid first (most reliable)
        if (IsPointInPolygon(centroid, outer))
            insideCount++;
        totalTests++;

        // Test several vertices distributed around the ring
        int step = Math.Max(1, inner.Count / 5); // Test ~5 vertices
        for (int i = 0; i < inner.Count; i += step)
        {
            if (IsPointInPolygon(inner[i], outer))
                insideCount++;
            totalTests++;
        }

        // Majority vote - more than half of test points must be inside
        return insideCount > totalTests / 2;
    }

    public static bool IsPointInPolygon(Vector2 point, List<Vector2> polygon)
    {
        bool inside = false;
        int j = polygon.Count - 1;

        for (int i = 0; i < polygon.Count; i++)
        {
            if ((polygon[i].Y > point.Y) != (polygon[j].Y > point.Y) &&
                point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
            {
                inside = !inside;
            }
            j = i;
        }

        return inside;
    }

    /// <summary>
    /// Classifies unassigned rings as outer or inner based on:
    /// 1. Signed area (positive = CCW = outer, negative = CW = inner)
    /// 2. Containment (ring inside another = inner)
    /// </summary>
    public static void ClassifyRings(List<List<Vector2>> unassigned, List<List<Vector2>> outers, List<List<Vector2>> inners)
    {
        if (unassigned.Count == 0)
            return;

        // Calculate signed area for each ring
        var ringAreas = unassigned.Select(ring => (ring, area: CalculateSignedArea(ring))).ToList();

        // Sort by absolute area (largest first)
        ringAreas.Sort((a, b) => Math.Abs(b.area).CompareTo(Math.Abs(a.area)));

        foreach (var (ring, area) in ringAreas)
        {
            // Check if this ring is inside any existing outer ring
            bool isInsideOuter = false;
            foreach (var outer in outers)
            {
                if (IsRingInsideRing(ring, outer))
                {
                    isInsideOuter = true;
                    break;
                }
            }

            if (isInsideOuter)
            {
                // Ring is inside an outer - it's a hole (inner)
                inners.Add(ring);
            }
            else
            {
                // Ring is not inside any outer
                // Check signed area: positive (CCW) = outer, negative (CW) = inner
                // But if no outers exist yet, this should be outer regardless of winding
                if (outers.Count == 0 || area > 0)
                {
                    outers.Add(ring);
                }
                else
                {
                    // Negative area and we have outers - check if it contains any outer
                    bool containsOuter = false;
                    foreach (var outer in outers)
                    {
                        if (IsRingInsideRing(outer, ring))
                        {
                            containsOuter = true;
                            break;
                        }
                    }

                    if (containsOuter)
                    {
                        // This ring contains an outer, so it's actually an outer itself
                        // (outer with a hole that is also an outer)
                        outers.Add(ring);
                    }
                    else
                    {
                        // Standalone ring with CW winding - treat as outer but reverse
                        outers.Add(ring);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Calculates signed area of a polygon.
    /// Positive = counter-clockwise (outer), Negative = clockwise (inner/hole)
    /// </summary>
    public static float CalculateSignedArea(List<Vector2> polygon)
    {
        if (polygon.Count < 3)
            return 0;

        float area = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var p1 = polygon[i];
            var p2 = polygon[(i + 1) % polygon.Count];
            area += (p2.X - p1.X) * (p2.Y + p1.Y);
        }
        return -area * 0.5f; // Negative because Y is often inverted
    }

    /// <summary>
    /// Checks if polygon is counter-clockwise (positive signed area)
    /// </summary>
    public static bool IsPolygonCCW(List<Vector2> polygon)
    {
        if (polygon.Count < 3) return true;

        float sum = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var p1 = polygon[i];
            var p2 = polygon[(i + 1) % polygon.Count];
            sum += (p2.X - p1.X) * (p2.Y + p1.Y);
        }
        return sum < 0; // Negative sum = CCW in screen coordinates (Y up)
    }

    public static float CalculatePolygonArea(List<Vector2> polygon)
    {
        if (polygon.Count < 3)
            return 0;

        float area = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var p1 = polygon[i];
            var p2 = polygon[(i + 1) % polygon.Count];
            area += p1.X * p2.Y - p2.X * p1.Y;
        }
        return area * 0.5f;
    }
}
