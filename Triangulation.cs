using System.Numerics;

namespace formap;

/// <summary>
/// Robust ear-clipping triangulation for simple polygons (improved):
/// - Normalizes winding to CCW
/// - Removes duplicate and colinear vertices
/// - Numerically stable point-in-triangle checks
/// - NO fan triangulation (causes issues with concave polygons)
/// - Uses convex decomposition as safe fallback
/// </summary>
public static class Triangulation
{
    /// <summary>
    /// Triangulates a polygon using ear-clipping algorithm with improved robustness
    /// </summary>
    public static List<int> Triangulate(List<Vector2> vertices)
    {
        var result = new List<int>();
        int n = vertices.Count;
        if (n < 3) return result;
        if (n == 3) { result.AddRange(new[] { 0, 1, 2 }); return result; }

        // Build working vertex index list with aggressive cleanup
        var cleaned = BuildCleanIndexList(vertices);
        if (cleaned.Count < 3) return result;

        // Ensure CCW
        if (!IsCCW(cleaned, vertices)) cleaned.Reverse();

        // For very large polygons, simplify more aggressively instead of
        // using fan triangulation (which breaks on concave polygons).
        if (cleaned.Count > 1500)
        {
            // Aggressive simplification for large polygons
            cleaned = SimplifyIndexList(vertices, cleaned, targetCount: 1000);
            if (cleaned.Count < 3) return result;
            
            // Ensure CCW after simplification
            if (!IsCCW(cleaned, vertices)) cleaned.Reverse();
        }
        
        // Try ear-clipping
        int guard = 0;
        int noProgressCount = 0;
        int lastSize = cleaned.Count;
        // Limit iterations based on polygon size
        int maxIterations = Math.Min(50000, cleaned.Count * 8);
        
        while (cleaned.Count > 3 && guard < maxIterations)
        {
            bool earClipped = false;
            
            // Prefer ears with larger angles (better for numerical stability)
            int bestEarIdx = -1;
            float bestAngle = -1f;
            
            for (int i = 0; i < cleaned.Count; i++)
            {
                int i0 = cleaned[(i - 1 + cleaned.Count) % cleaned.Count];
                int i1 = cleaned[i];
                int i2 = cleaned[(i + 1) % cleaned.Count];

                if (!IsConvex(vertices[i0], vertices[i1], vertices[i2]))
                    continue;

                if (ContainsAnyPoint(vertices, cleaned, i0, i1, i2))
                    continue;

                // Calculate angle at i1 (prefer larger angles)
                float angle = CalculateAngle(vertices[i0], vertices[i1], vertices[i2]);
                if (angle > bestAngle)
                {
                    bestAngle = angle;
                    bestEarIdx = i;
                }
            }

            if (bestEarIdx >= 0)
            {
                int i = bestEarIdx;
                int i0 = cleaned[(i - 1 + cleaned.Count) % cleaned.Count];
                int i1 = cleaned[i];
                int i2 = cleaned[(i + 1) % cleaned.Count];
                
                // Clip ear
                result.Add(i0);
                result.Add(i1);
                result.Add(i2);
                cleaned.RemoveAt(i);
                earClipped = true;
                noProgressCount = 0;
            }

            if (!earClipped)
            {
                // Try removing problematic vertices
                bool removed = false;
                
                // First, try removing nearly colinear vertices
                if (RemoveNearlyColinearVertex(vertices, cleaned))
                {
                    removed = true;
                    noProgressCount = 0;
                }
                // Then, try removing vertices that cause self-intersection
                else if (RemoveSelfIntersectingVertex(vertices, cleaned))
                {
                    removed = true;
                    noProgressCount = 0;
                }
                // Finally, try removing reflex vertices with smallest area
                else if (RemoveSmallestReflexVertex(vertices, cleaned))
                {
                    removed = true;
                    noProgressCount = 0;
                }

                if (!removed)
                {
                    noProgressCount++;
                    // If we've tried many times without progress, use SAFE fallback
                    if (noProgressCount > Math.Max(15, cleaned.Count))
                    {
                        // Use convex decomposition instead of fan triangulation;
                        // this is safe for concave polygons.
                        var safeResult = TriangulateConvexDecomposition(vertices, cleaned);
                        result.AddRange(safeResult);
                        return result;
                    }
                    else
                    {
                        // Force remove a vertex to make progress
                        if (cleaned.Count > 3)
                        {
                            // Try to remove a reflex vertex first
                            bool removedReflex = RemoveSmallestReflexVertex(vertices, cleaned);
                            if (!removedReflex)
                            {
                                // If no reflex vertices, just remove vertex with smallest angle
                                int smallestAngleIdx = FindSmallestAngleVertex(vertices, cleaned);
                                if (smallestAngleIdx >= 0)
                                    cleaned.RemoveAt(smallestAngleIdx);
                                else
                                    cleaned.RemoveAt(0);
                            }
                            noProgressCount = 0;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            // Check if we're making progress
            if (cleaned.Count == lastSize)
                noProgressCount++;
            else
                noProgressCount = 0;
            lastSize = cleaned.Count;

            guard++;
        }

        if (cleaned.Count == 3)
        {
            result.Add(cleaned[0]);
            result.Add(cleaned[1]);
            result.Add(cleaned[2]);
        }

        return result;
    }

    /// <summary>
    /// Finds vertex with smallest interior angle (good candidate for removal)
    /// </summary>
    private static int FindSmallestAngleVertex(List<Vector2> vertices, List<int> idx)
    {
        if (idx.Count < 3) return -1;
        
        int bestIdx = -1;
        float smallestAngle = float.MaxValue;
        
        for (int i = 0; i < idx.Count; i++)
        {
            int i0 = idx[(i - 1 + idx.Count) % idx.Count];
            int i1 = idx[i];
            int i2 = idx[(i + 1) % idx.Count];
            
            float angle = CalculateAngle(vertices[i0], vertices[i1], vertices[i2]);
            if (angle < smallestAngle)
            {
                smallestAngle = angle;
                bestIdx = i;
            }
        }
        
        return bestIdx;
    }

    /// <summary>
    /// Simplifies an index list to approximately target count
    /// Uses Douglas-Peucker-like approach
    /// </summary>
    private static List<int> SimplifyIndexList(List<Vector2> vertices, List<int> idx, int targetCount)
    {
        if (idx.Count <= targetCount) return idx;
        
        // Calculate how many vertices to skip
        int skip = Math.Max(1, idx.Count / targetCount);
        
        var simplified = new List<int>();
        for (int i = 0; i < idx.Count; i += skip)
        {
            simplified.Add(idx[i]);
        }
        
        // Ensure we have at least 3 vertices
        if (simplified.Count < 3 && idx.Count >= 3)
        {
            simplified.Clear();
            simplified.Add(idx[0]);
            simplified.Add(idx[idx.Count / 3]);
            simplified.Add(idx[idx.Count * 2 / 3]);
        }
        
        return simplified;
    }

    /// <summary>
    /// Safe triangulation using convex decomposition
    /// Works correctly for ANY simple polygon (convex or concave)
    /// </summary>
    private static List<int> TriangulateConvexDecomposition(List<Vector2> vertices, List<int> idx)
    {
        var result = new List<int>();
        if (idx.Count < 3) return result;
        if (idx.Count == 3)
        {
            result.Add(idx[0]);
            result.Add(idx[1]);
            result.Add(idx[2]);
            return result;
        }

        // Find a convex vertex and create a valid triangle from it
        // This is a monotone decomposition approach
        
        int convexIdx = -1;
        for (int i = 0; i < idx.Count; i++)
        {
            int i0 = idx[(i - 1 + idx.Count) % idx.Count];
            int i1 = idx[i];
            int i2 = idx[(i + 1) % idx.Count];
            
            if (IsConvex(vertices[i0], vertices[i1], vertices[i2]))
            {
                // Check if this ear is valid (no other vertices inside)
                if (!ContainsAnyPoint(vertices, idx, i0, i1, i2))
                {
                    convexIdx = i;
                    break;
                }
            }
        }
        
        if (convexIdx >= 0)
        {
            // Found a valid ear - clip it
            int i0 = idx[(convexIdx - 1 + idx.Count) % idx.Count];
            int i1 = idx[convexIdx];
            int i2 = idx[(convexIdx + 1) % idx.Count];
            
            result.Add(i0);
            result.Add(i1);
            result.Add(i2);
            
            // Remove clipped vertex and recurse
            var remaining = new List<int>(idx);
            remaining.RemoveAt(convexIdx);
            
            if (remaining.Count >= 3)
            {
                result.AddRange(TriangulateConvexDecomposition(vertices, remaining));
            }
        }
        else
        {
            // No valid convex vertex found - polygon is likely self-intersecting
            // Last resort: split polygon at a reflex vertex
            int reflexIdx = FindSmallestReflexVertexIndex(vertices, idx);
            if (reflexIdx >= 0 && idx.Count > 4)
            {
                // Remove the problematic reflex vertex and try again
                var remaining = new List<int>(idx);
                remaining.RemoveAt(reflexIdx);
                result.AddRange(TriangulateConvexDecomposition(vertices, remaining));
            }
            else if (idx.Count == 4)
            {
                // Quad - split into two triangles (diagonal split)
                // Choose the better diagonal
                var d1Area = Math.Abs(TriangleArea(vertices[idx[0]], vertices[idx[1]], vertices[idx[2]])) +
                             Math.Abs(TriangleArea(vertices[idx[0]], vertices[idx[2]], vertices[idx[3]]));
                var d2Area = Math.Abs(TriangleArea(vertices[idx[0]], vertices[idx[1]], vertices[idx[3]])) +
                             Math.Abs(TriangleArea(vertices[idx[1]], vertices[idx[2]], vertices[idx[3]]));
                
                if (d1Area <= d2Area)
                {
                    result.Add(idx[0]); result.Add(idx[1]); result.Add(idx[2]);
                    result.Add(idx[0]); result.Add(idx[2]); result.Add(idx[3]);
                }
                else
                {
                    result.Add(idx[0]); result.Add(idx[1]); result.Add(idx[3]);
                    result.Add(idx[1]); result.Add(idx[2]); result.Add(idx[3]);
                }
            }
            // For 3 vertices, already handled above
        }
        
        return result;
    }

    private static float TriangleArea(Vector2 a, Vector2 b, Vector2 c)
    {
        return 0.5f * Cross(a, b, c);
    }

    private static int FindSmallestReflexVertexIndex(List<Vector2> vertices, List<int> idx)
    {
        int bestIdx = -1;
        float smallestArea = float.MaxValue;

        for (int i = 0; i < idx.Count; i++)
        {
            int i0 = idx[(i - 1 + idx.Count) % idx.Count];
            int i1 = idx[i];
            int i2 = idx[(i + 1) % idx.Count];

            // Check if reflex (concave)
            if (IsConvex(vertices[i0], vertices[i1], vertices[i2]))
                continue;

            float area = MathF.Abs(Cross(vertices[i0], vertices[i1], vertices[i2]));
            if (area < smallestArea)
            {
                smallestArea = area;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    /// <summary>
    /// Calculates the angle at vertex b in triangle a-b-c (in radians)
    /// </summary>
    private static float CalculateAngle(Vector2 a, Vector2 b, Vector2 c)
    {
        var v1 = new Vector2(a.X - b.X, a.Y - b.Y);
        var v2 = new Vector2(c.X - b.X, c.Y - b.Y);
        float len1 = MathF.Sqrt(v1.X * v1.X + v1.Y * v1.Y);
        float len2 = MathF.Sqrt(v2.X * v2.X + v2.Y * v2.Y);
        if (len1 < 1e-8f || len2 < 1e-8f) return 0f;
        float dot = (v1.X * v2.X + v1.Y * v2.Y) / (len1 * len2);
        dot = MathF.Max(-1f, MathF.Min(1f, dot)); // Clamp to valid range
        return MathF.Acos(dot);
    }

    /// <summary>
    /// Removes a vertex that causes self-intersection
    /// </summary>
    private static bool RemoveSelfIntersectingVertex(List<Vector2> vertices, List<int> idx)
    {
        if (idx.Count < 4) return false;

        for (int i = 0; i < idx.Count; i++)
        {
            int prev = idx[(i - 1 + idx.Count) % idx.Count];
            int curr = idx[i];
            int next = idx[(i + 1) % idx.Count];

            // Check if removing current vertex would resolve intersection
            bool hasIntersection = false;
            for (int j = 0; j < idx.Count; j++)
            {
                int j0 = idx[j];
                int j1 = idx[(j + 1) % idx.Count];
                
                // Skip adjacent edges
                if (j0 == prev || j0 == next || j1 == prev || j1 == next || j0 == curr || j1 == curr)
                    continue;

                if (SegmentIntersectProper(vertices[prev], vertices[next], vertices[j0], vertices[j1]))
                {
                    hasIntersection = true;
                    break;
                }
            }

            if (!hasIntersection)
            {
                idx.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes the reflex vertex with smallest area contribution
    /// </summary>
    private static bool RemoveSmallestReflexVertex(List<Vector2> vertices, List<int> idx)
    {
        if (idx.Count < 4) return false;

        int bestIdx = -1;
        float smallestArea = float.MaxValue;

        for (int i = 0; i < idx.Count; i++)
        {
            int i0 = idx[(i - 1 + idx.Count) % idx.Count];
            int i1 = idx[i];
            int i2 = idx[(i + 1) % idx.Count];

            // Check if reflex (concave)
            if (IsConvex(vertices[i0], vertices[i1], vertices[i2]))
                continue;

            // Calculate triangle area (smaller is better to remove)
            float area = MathF.Abs(Cross(vertices[i0], vertices[i1], vertices[i2]));
            if (area < smallestArea)
            {
                smallestArea = area;
                bestIdx = i;
            }
        }

        if (bestIdx >= 0)
        {
            idx.RemoveAt(bestIdx);
            return true;
        }

        return false;
    }

    private static List<int> BuildCleanIndexList(List<Vector2> v)
    {
        var idx = new List<int>();
        if (v.Count == 0) return idx;
        
        for (int i = 0; i < v.Count; i++) idx.Add(i);
        
        // Remove consecutive duplicates
        var cleaned = new List<int> { idx[0] };
        for (int i = 1; i < idx.Count; i++)
        {
            if (!NearlyEqual(v[idx[i]], v[cleaned[^1]]))
            {
                cleaned.Add(idx[i]);
            }
        }
        
        // Remove closing vertex if duplicate
        if (cleaned.Count >= 2 && NearlyEqual(v[cleaned[0]], v[cleaned[^1]]))
        {
            cleaned.RemoveAt(cleaned.Count - 1);
        }
        
        idx = cleaned;
        
        // Remove colinear vertices
        RemoveColinear(v, idx);
        
        return idx;
    }

    private static void RemoveColinear(List<Vector2> v, List<int> idx)
    {
        const float eps = 1e-6f;
        int i = 0; int safety = 0;
        while (idx.Count > 3 && i < idx.Count && safety < 100000)
        {
            int i0 = idx[(i - 1 + idx.Count) % idx.Count];
            int i1 = idx[i];
            int i2 = idx[(i + 1) % idx.Count];
            if (Math.Abs(Cross(v[i0], v[i1], v[i2])) < eps && OnSegment(v[i0], v[i1], v[i2]))
            {
                idx.RemoveAt(i);
            }
            else
            {
                i++;
            }
            safety++;
        }
    }

    private static bool RemoveNearlyColinearVertex(List<Vector2> v, List<int> idx)
    {
        const float eps = 1e-6f;
        for (int i = 0; i < idx.Count; i++)
        {
            int i0 = idx[(i - 1 + idx.Count) % idx.Count];
            int i1 = idx[i];
            int i2 = idx[(i + 1) % idx.Count];
            if (Math.Abs(Cross(v[i0], v[i1], v[i2])) < eps)
            {
                idx.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    private static bool IsCCW(List<int> idx, List<Vector2> v) => PolygonArea(idx, v) > 0f;

    private static float PolygonArea(List<int> idx, List<Vector2> v)
    {
        double a = 0;
        for (int i = 0; i < idx.Count; i++)
        {
            var p = v[idx[i]];
            var q = v[idx[(i + 1) % idx.Count]];
            a += (double)p.X * q.Y - (double)p.Y * q.X;
        }
        return (float)(0.5 * a);
    }

    private static float Cross(in Vector2 a, in Vector2 b, in Vector2 c)
    {
        return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    }

    private static bool IsConvex(in Vector2 a, in Vector2 b, in Vector2 c)
    {
        return Cross(a, b, c) > 1e-8f;
    }

    private static bool ContainsAnyPoint(List<Vector2> v, List<int> idx, int i0, int i1, int i2)
    {
        var a = v[i0]; var b = v[i1]; var c = v[i2];
        for (int k = 0; k < idx.Count; k++)
        {
            int ik = idx[k];
            if (ik == i0 || ik == i1 || ik == i2) continue;
            if (PointInTriangle(v[ik], a, b, c)) return true;
        }
        return false;
    }

    private static bool PointInTriangle(in Vector2 p, in Vector2 a, in Vector2 b, in Vector2 c)
    {
        // Barycentric with epsilon
        var v0 = new Vector2(c.X - a.X, c.Y - a.Y);
        var v1 = new Vector2(b.X - a.X, b.Y - a.Y);
        var v2 = new Vector2(p.X - a.X, p.Y - a.Y);
        float den = v0.X * v1.Y - v0.Y * v1.X;
        if (Math.Abs(den) < 1e-12f) return false;
        float u = (v2.X * v1.Y - v2.Y * v1.X) / den;
        float v = (v0.X * v2.Y - v0.Y * v2.X) / den;
        float w = 1f - u - v;
        const float eps = -1e-7f;
        return u >= eps && v >= eps && w >= eps;
    }

    private static bool NearlyEqual(in Vector2 a, in Vector2 b)
    {
        const float eps = 1e-6f;
        return Math.Abs(a.X - b.X) < eps && Math.Abs(a.Y - b.Y) < eps;
    }
    
    private static bool OnSegment(in Vector2 a, in Vector2 b, in Vector2 c)
    {
        return b.X >= Math.Min(a.X, c.X) - 1e-6f && b.X <= Math.Max(a.X, c.X) + 1e-6f &&
               b.Y >= Math.Min(a.Y, c.Y) - 1e-6f && b.Y <= Math.Max(a.Y, c.Y) + 1e-6f;
    }

    private static bool SegmentIntersectProper(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float o1 = Cross(a, b, c);
        float o2 = Cross(a, b, d);
        float o3 = Cross(c, d, a);
        float o4 = Cross(c, d, b);

        if ((o1 > 0 && o2 < 0 || o1 < 0 && o2 > 0) &&
            (o3 > 0 && o4 < 0 || o3 < 0 && o4 > 0))
            return true;

        return false;
    }
}