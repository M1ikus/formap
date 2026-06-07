using System.Numerics;

namespace formap;

/// <summary>
/// Represents a geometric feature with vertices and indices.
/// For lines: vertices form the polyline, indices are consecutive pairs (0-1, 1-2, ...)
/// For polygons: vertices form the polygon outline, indices form triangles
/// All coordinates are in meters (X, Y) on Z=0 plane
/// </summary>
public class MeshGeometry
{
    public BBox BoundingBox { get; set; }
    public List<Vector2> Vertices { get; set; } = new();
    public List<int> Indices { get; set; } = new();
    public List<int> HoleStarts { get; set; } = new();
    public List<int> SegmentIds { get; set; } = new();
    public List<int> JunctionIndices { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
    
    /// <summary>
    /// Computes bounding box from vertices
    /// </summary>
    public void ComputeBoundingBox()
    {
        if (Vertices.Count == 0)
        {
            BoundingBox = BBox.Empty;
            return;
        }

        // Initialize with first vertex
        var first = Vertices[0];
        float minX = first.X, minY = first.Y;
        float maxX = first.X, maxY = first.Y;

        // Expand with remaining vertices
        for (int i = 1; i < Vertices.Count; i++)
        {
            var v = Vertices[i];
            if (v.X < minX) minX = v.X;
            if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.Y > maxY) maxY = v.Y;
        }

        // Assign final bounding box (struct assignment copies values)
        BoundingBox = new BBox
        {
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY
        };
    }
    
    /// <summary>
    /// Validates and fixes indices to ensure they're within valid range [0, Vertices.Count-1]
    /// Returns true if indices are valid, false if geometry should be rejected
    /// </summary>
    public bool ValidateAndFixIndices()
    {
        if (Vertices.Count == 0)
        {
            Indices.Clear();
            return false;
        }
        
        int maxIndex = Vertices.Count - 1;
        bool hasInvalidIndices = false;
        
        // Check all indices
        for (int i = 0; i < Indices.Count; i++)
        {
            if (Indices[i] < 0 || Indices[i] > maxIndex)
            {
                hasInvalidIndices = true;
                break;
            }
        }
        
        if (hasInvalidIndices)
        {
            // Remove invalid indices or clamp them (safer to reject)
            // For now, reject geometry with invalid indices
            Indices.Clear();
            return false;
        }
        
        // Validate HoleStarts
        for (int i = 0; i < HoleStarts.Count; i++)
        {
            if (HoleStarts[i] < 0 || HoleStarts[i] > maxIndex)
            {
                // Remove invalid hole starts
                HoleStarts.RemoveAt(i);
                i--;
            }
        }
        
        // Validate JunctionIndices
        for (int i = 0; i < JunctionIndices.Count; i++)
        {
            if (JunctionIndices[i] < 0 || JunctionIndices[i] > maxIndex)
            {
                // Remove invalid junction indices
                JunctionIndices.RemoveAt(i);
                i--;
            }
        }
        
        return true;
    }
    
    public void WriteBody(BinaryWriter writer)
    {
        // Final validation before write - use ValidateAndFixIndices() instead of duplicating logic
        // This ensures consistency and removes the code duplication
        ValidateAndFixIndices();

        // NOTE: Even if Indices.Count == 0, we MUST write the complete structure
        // because the reader expects a consistent format. Empty counts are valid.

        // Write vertices
        writer.Write(Vertices.Count);
        foreach (var v in Vertices)
        {
            writer.Write(v.X);
            writer.Write(v.Y);
        }
        
        // Write indices
        writer.Write(Indices.Count);
        foreach (var idx in Indices)
        {
            writer.Write(idx);
        }
        
        // Write hole starts (for multipolygons with holes)
        writer.Write(HoleStarts.Count);
        foreach (var hs in HoleStarts)
        {
            writer.Write(hs);
        }
        
        // Write segment ids (for railways), empty for others
        writer.Write(SegmentIds.Count);
        foreach (var sid in SegmentIds)
        {
            writer.Write(sid);
        }
        
        // Write railway junction vertex indices (for railways), empty for others
        writer.Write(JunctionIndices.Count);
        foreach (var j in JunctionIndices)
        {
            writer.Write(j);
        }
        
        // Write metadata
        writer.Write(Metadata.Count);
        foreach (var kvp in Metadata)
        {
            WriteString(writer, kvp.Key);
            WriteString(writer, kvp.Value);
        }
    }
    
    /// <summary>
    /// Reads geometry from binary format
    /// </summary>
    public static MeshGeometry Read(BinaryReader reader)
    {
        var geom = new MeshGeometry();
        
        // Read bounding box first
        geom.BoundingBox = new BBox
        {
            MinX = reader.ReadSingle(),
            MinY = reader.ReadSingle(),
            MaxX = reader.ReadSingle(),
            MaxY = reader.ReadSingle()
        };
        
        // Read vertices
        int vertexCount = reader.ReadInt32();
        for (int i = 0; i < vertexCount; i++)
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            geom.Vertices.Add(new Vector2(x, y));
        }
        
        // Read indices
        int indexCount = reader.ReadInt32();
        for (int i = 0; i < indexCount; i++)
        {
            geom.Indices.Add(reader.ReadInt32());
        }
        
        // Read hole starts
        int holeCount = reader.ReadInt32();
        for (int i = 0; i < holeCount; i++)
        {
            geom.HoleStarts.Add(reader.ReadInt32());
        }
        
        // Read segment ids
        int sidCount = reader.ReadInt32();
        for (int i = 0; i < sidCount; i++)
        {
            geom.SegmentIds.Add(reader.ReadInt32());
        }
        
        // Read junction indices
        int jCount = reader.ReadInt32();
        for (int i = 0; i < jCount; i++)
        {
            geom.JunctionIndices.Add(reader.ReadInt32());
        }
        
        // Read metadata
        int metadataCount = reader.ReadInt32();
        for (int i = 0; i < metadataCount; i++)
        {
            string key = ReadString(reader);
            string value = ReadString(reader);
            geom.Metadata[key] = value;
        }
        
        return geom;
    }
    
    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? "");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
    
    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        byte[] bytes = reader.ReadBytes(length);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
