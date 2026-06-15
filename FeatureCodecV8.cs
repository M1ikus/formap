using System.Numerics;

namespace formap;

/// <summary>
/// v8 per-feature codec (FORMAP04). Lossless re-encoding — NO data dropped, NO coordinate precision change.
///
/// Vertices are NOT stored per feature: they go into a block-level pool that is split into X/Y planes and
/// byte-shuffled before compression (Change 3 — makes the otherwise-incompressible float32 stream compress
/// far better under LZ4, while keeping LZ4's fast decode). See <see cref="BinaryFormatV8"/> for the block layout.
///
/// Per-feature STRUCTURE record (everything except the vertex coordinates):
///   (bbox is NOT stored — it is min/max of the vertices, recomputed on read; bit-exact, ~0 load cost)
///   flags       : 1 byte                      — bit0 hasIndices, bit1 hasHoleStarts, bit2 hasSegmentIds,
///                                                bit3 hasJunctionIndices, bit4 hasMetadata, bit5 wideIndices
///   vertexCount : varint                      — how many vertices this feature pulls from the block pool
///   [hasIndices]         indexCount:varint + indices × (uint16 | int32 per bit5)
///   [hasHoleStarts]      count:varint + values × varint
///   [hasSegmentIds]      count:varint + values × varint
///   [hasJunctionIndices] count:varint + values × varint
///   [hasMetadata]        count:varint + count × (keyIdx:varint, valIdx:varint)   — string-table refs
/// </summary>
public static class FeatureCodecV8
{
    /// <summary>Indices are &lt; Vertices.Count; uint16 holds 0..65535, so 32-bit indices are needed only
    /// when a feature has more than 65536 vertices.</summary>
    public const int WideIndexVertexThreshold = 65536;

    // --- Unsigned LEB128 varint ---
    public static void WriteVarint(BinaryWriter w, uint value)
    {
        while (value >= 0x80) { w.Write((byte)(value | 0x80)); value >>= 7; }
        w.Write((byte)value);
    }

    public static uint ReadVarint(BinaryReader r)
    {
        uint result = 0; int shift = 0; byte b;
        do { b = r.ReadByte(); result |= (uint)(b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
        return result;
    }

    // --- Byte-shuffle (SoA "shuffle filter"): regroup the bytes of a fixed-stride value array into planes
    //     (all byte[0]s, then all byte[1]s, ...). Fully reversible, bit-exact. data.Length must be a multiple
    //     of stride (it always is here: float planes are vertexCount × 4). ---
    public static byte[] Shuffle(byte[] data, int stride)
    {
        int n = data.Length / stride;
        var o = new byte[n * stride];
        for (int i = 0; i < n; i++) { int s = i * stride; for (int b = 0; b < stride; b++) o[b * n + i] = data[s + b]; }
        return o;
    }

    public static byte[] Unshuffle(byte[] data, int stride)
    {
        int n = data.Length / stride;
        var o = new byte[n * stride];
        for (int i = 0; i < n; i++) { int s = i * stride; for (int b = 0; b < stride; b++) o[s + b] = data[b * n + i]; }
        return o;
    }

    /// <summary>Writes the per-feature structure (everything EXCEPT vertex coordinates — those are pooled).</summary>
    public static void WriteFeatureStructure(BinaryWriter w, MeshGeometry g, Func<string, int> intern)
    {
        // bbox is NOT stored in v8 — it is derived from vertices on read (ComputeBoundingBox), bit-exact.
        bool hasIdx = g.Indices.Count > 0, hasHole = g.HoleStarts.Count > 0, hasSeg = g.SegmentIds.Count > 0,
             hasJunc = g.JunctionIndices.Count > 0, hasMeta = g.Metadata.Count > 0;
        bool wide = g.Vertices.Count > WideIndexVertexThreshold;

        byte flags = 0;
        if (hasIdx) flags |= 1; if (hasHole) flags |= 2; if (hasSeg) flags |= 4;
        if (hasJunc) flags |= 8; if (hasMeta) flags |= 16; if (wide) flags |= 32;
        w.Write(flags);

        WriteVarint(w, (uint)g.Vertices.Count);

        if (hasIdx)
        {
            WriteVarint(w, (uint)g.Indices.Count);
            if (wide) foreach (var i in g.Indices) w.Write(i);
            else foreach (var i in g.Indices) w.Write((ushort)i);
        }
        if (hasHole) { WriteVarint(w, (uint)g.HoleStarts.Count); foreach (var h in g.HoleStarts) WriteVarint(w, (uint)h); }
        if (hasSeg) { WriteVarint(w, (uint)g.SegmentIds.Count); foreach (var s in g.SegmentIds) WriteVarint(w, (uint)s); }
        if (hasJunc) { WriteVarint(w, (uint)g.JunctionIndices.Count); foreach (var j in g.JunctionIndices) WriteVarint(w, (uint)j); }
        if (hasMeta)
        {
            WriteVarint(w, (uint)g.Metadata.Count);
            foreach (var kv in g.Metadata) { WriteVarint(w, (uint)intern(kv.Key)); WriteVarint(w, (uint)intern(kv.Value)); }
        }
    }

    /// <summary>Reads a per-feature structure. Vertices are filled later by the caller from the block pool;
    /// <paramref name="vertexCount"/> tells the caller how many to take.</summary>
    public static MeshGeometry ReadFeatureStructure(BinaryReader r, IReadOnlyList<string> table, out int vertexCount)
    {
        var g = new MeshGeometry(); // bbox derived from vertices by the caller after the pool is read

        byte flags = r.ReadByte();
        bool hasIdx = (flags & 1) != 0, hasHole = (flags & 2) != 0, hasSeg = (flags & 4) != 0,
             hasJunc = (flags & 8) != 0, hasMeta = (flags & 16) != 0, wide = (flags & 32) != 0;

        vertexCount = (int)ReadVarint(r);

        if (hasIdx)
        {
            int ic = (int)ReadVarint(r);
            for (int i = 0; i < ic; i++) g.Indices.Add(wide ? r.ReadInt32() : r.ReadUInt16());
        }
        if (hasHole) { int n = (int)ReadVarint(r); for (int i = 0; i < n; i++) g.HoleStarts.Add((int)ReadVarint(r)); }
        if (hasSeg) { int n = (int)ReadVarint(r); for (int i = 0; i < n; i++) g.SegmentIds.Add((int)ReadVarint(r)); }
        if (hasJunc) { int n = (int)ReadVarint(r); for (int i = 0; i < n; i++) g.JunctionIndices.Add((int)ReadVarint(r)); }
        if (hasMeta)
        {
            int n = (int)ReadVarint(r);
            for (int i = 0; i < n; i++) { int k = (int)ReadVarint(r); int v = (int)ReadVarint(r); g.Metadata[table[k]] = table[v]; }
        }
        return g;
    }

    /// <summary>Advances <paramref name="r"/> past one feature's structure record WITHOUT materializing it —
    /// no MeshGeometry, no index/hole/segment/junction lists, no metadata dictionary, no string-table lookups.
    /// Used by <see cref="BinaryFormatV8.DecodeBlockFiltered"/> to skip layers the caller does not want while
    /// still returning each feature's vertexCount (needed to skip its slice of the block's vertex pool).
    /// Must stay byte-for-byte in lockstep with <see cref="ReadFeatureStructure"/>.</summary>
    public static int SkipFeatureStructure(BinaryReader r)
    {
        byte flags = r.ReadByte();
        bool hasIdx = (flags & 1) != 0, hasHole = (flags & 2) != 0, hasSeg = (flags & 4) != 0,
             hasJunc = (flags & 8) != 0, hasMeta = (flags & 16) != 0, wide = (flags & 32) != 0;

        int vertexCount = (int)ReadVarint(r);

        if (hasIdx)
        {
            int ic = (int)ReadVarint(r);
            r.BaseStream.Seek((long)ic * (wide ? 4 : 2), SeekOrigin.Current); // indices are fixed-width (int32 | uint16)
        }
        // hole/segment/junction/metadata values are varints (variable length) → must decode each to find the boundary.
        if (hasHole) { int n = (int)ReadVarint(r); for (int i = 0; i < n; i++) ReadVarint(r); }
        if (hasSeg)  { int n = (int)ReadVarint(r); for (int i = 0; i < n; i++) ReadVarint(r); }
        if (hasJunc) { int n = (int)ReadVarint(r); for (int i = 0; i < n; i++) ReadVarint(r); }
        if (hasMeta) { int n = (int)ReadVarint(r); for (int i = 0; i < n; i++) { ReadVarint(r); ReadVarint(r); } }
        return vertexCount;
    }

    public static string? Compare(MeshGeometry a, MeshGeometry b)
    {
        if (a.Vertices.Count != b.Vertices.Count) return $"vertex count {a.Vertices.Count}->{b.Vertices.Count}";
        for (int i = 0; i < a.Vertices.Count; i++)
            if (a.Vertices[i] != b.Vertices[i]) return $"vertex {i} {a.Vertices[i]}->{b.Vertices[i]} (precision changed!)";
        if (!a.Indices.SequenceEqual(b.Indices)) return "indices differ";
        if (!a.HoleStarts.SequenceEqual(b.HoleStarts)) return "holeStarts differ";
        if (!a.SegmentIds.SequenceEqual(b.SegmentIds)) return "segmentIds differ";
        if (!a.JunctionIndices.SequenceEqual(b.JunctionIndices)) return "junctionIndices differ";
        if (a.Metadata.Count != b.Metadata.Count) return $"meta count {a.Metadata.Count}->{b.Metadata.Count}";
        foreach (var kv in a.Metadata)
            if (!b.Metadata.TryGetValue(kv.Key, out var v) || v != kv.Value) return $"meta '{kv.Key}' differs";
        var ba = a.BoundingBox; var bb = b.BoundingBox;
        if (ba.MinX != bb.MinX || ba.MinY != bb.MinY || ba.MaxX != bb.MaxX || ba.MaxY != bb.MaxY) return "bbox differs";
        return null;
    }

    // --- Unit self-tests: byte-shuffle losslessness + structure round-trip (run via `formap --selftest`) ---
    public static void SelfTest()
    {
        int fails = 0;

        // 1) Byte-shuffle must be perfectly reversible (the core "no data loss" guarantee for Change 3).
        var rndBytes = new byte[70000 * 4];
        for (int i = 0; i < rndBytes.Length; i++) rndBytes[i] = (byte)((i * 2654435761u) >> 24); // deterministic pseudo-random
        var round = Unshuffle(Shuffle(rndBytes, 4), 4);
        if (!rndBytes.SequenceEqual(round)) { Console.WriteLine("  shuffle: FAIL (not bit-exact)"); fails++; }
        else Console.WriteLine($"  shuffle: ok (bit-exact on {rndBytes.Length:N0} bytes)");

        // 2) Structure round-trip (everything except pooled vertices).
        var table = new List<string>(); var map = new Dictionary<string, int>();
        int Intern(string s) { if (!map.TryGetValue(s, out var i)) { i = table.Count; table.Add(s); map[s] = i; } return i; }
        var cases = new List<MeshGeometry> { Poi(), Poly(), Rail(), Wide(70000) };
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            foreach (var g in cases) WriteFeatureStructure(w, g, Intern);
        ms.Position = 0;
        using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            for (int c = 0; c < cases.Count; c++)
            {
                var got = ReadFeatureStructure(r, table, out int vc);
                if (vc != cases[c].Vertices.Count) { Console.WriteLine($"  struct case {c}: FAIL vertexCount {cases[c].Vertices.Count}->{vc}"); fails++; continue; }
                got.Vertices.AddRange(cases[c].Vertices); // vertices not part of structure; copy so Compare checks the rest
                got.ComputeBoundingBox(); // bbox is derived, not stored
                var diff = Compare(cases[c], got);
                if (diff != null) { Console.WriteLine($"  struct case {c}: FAIL {diff}"); fails++; }
                else Console.WriteLine($"  struct case {c}: ok");
            }

        Console.WriteLine(fails == 0 ? "[SELFTEST] FeatureCodecV8 PASS" : $"[SELFTEST] FeatureCodecV8 FAILED ({fails})");
        if (fails != 0) Environment.Exit(1);
    }

    private static MeshGeometry Poi() { var g = new MeshGeometry(); g.Vertices.Add(new Vector2(21.5f, 53.1f)); g.Metadata["railway"] = "signal"; g.Metadata["ref"] = "A3"; g.ComputeBoundingBox(); return g; }
    private static MeshGeometry Poly() { var g = new MeshGeometry(); g.Vertices.AddRange(new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) }); g.Indices.AddRange(new[] { 0, 1, 2, 0, 2, 3 }); g.Metadata["building"] = "yes"; g.Metadata["building:levels"] = "3"; g.ComputeBoundingBox(); return g; }
    private static MeshGeometry Rail() { var g = new MeshGeometry(); g.Vertices.AddRange(new[] { new Vector2(0, 0), new Vector2(5, 1), new Vector2(9, 2) }); g.Indices.AddRange(new[] { 0, 1, 1, 2 }); g.SegmentIds.AddRange(new[] { 100, 100 }); g.JunctionIndices.AddRange(new[] { 0, 2 }); g.Metadata["railway"] = "rail"; g.ComputeBoundingBox(); return g; }
    private static MeshGeometry Wide(int n) { var g = new MeshGeometry(); for (int i = 0; i < n; i++) g.Vertices.Add(new Vector2(i * 0.1f, i * 0.2f)); g.Indices.AddRange(new[] { 0, n - 1, n / 2 }); g.Metadata["natural"] = "wood"; g.ComputeBoundingBox(); return g; }
}
