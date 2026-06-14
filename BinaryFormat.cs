using System.IO;

namespace formap;

/// <summary>
/// Specifies the binary format for the output file
///
/// FORMAT v5 (FORMAP01):
/// Header (64 bytes):
///   - Magic number: "FORMAP01" (8 bytes)
///   - Version: 5 (int32)
///   - Layer count: int32
///   - Spatial bounds: float[4] (16 bytes: minX, minY, maxX, maxY)
///   - Reserved: 32 bytes
/// For each layer:
///   - Layer type: int32
///   - Feature count: int32
///   - Compressed layer length: int32
///   - Compressed layer bytes (LZ4)
///
/// FORMAT v7 (FORMAP03) - TILED, Multi-LOD (current output):
/// Header (128 bytes):
///   - Magic number: "FORMAP03" (8 bytes)
///   - Version: 7 (int32)
///   - Tile size: float32 (10000.0 = 10km)
///   - Global bounds: float[4] (16 bytes: minX, minY, maxX, maxY)
///   - Grid dimensions: int32 tilesX, int32 tilesY
///   - Total tiles: int32
///   - Index table offset: int64
///   - Reserved: 72 bytes
///
/// Tile Index Table (at index_table_offset):
///   For each tile:
///     - Tile ID: int64
///     - Grid X: int32, Grid Y: int32
///     - Tile bounds: float[4]
///     - Per-LOD info (LODCount entries): file offset int64, compressed size int32,
///       uncompressed size int32, layer mask int32
///     - Feature counts per layer (LOD0): int32[LayerCount]
///
/// Tile Data:
///   For each tile, for each LOD (at that LOD's file_offset):
///     - LZ4 compressed block (compressed_size bytes)
///       Decompressed tile body:
///         For each layer (if bit set in layer_mask):
///           - Layer type: int32
///           - Feature count: int32
///           - Features... (same format as v5)
/// </summary>
public static class BinaryFormat
{
    public const string MagicV7 = "FORMAP03"; // v7+: LayerCount=13 (Coastlines added on 2026-04-23, breaking change)
    public const string MagicV8 = "FORMAP04"; // v8: header carries LayerCount + LODCount explicitly (no compile-time assumptions)
    public const int LODCount = 6;

    public enum LayerType : int
    {
        Highways = 0,         // Roads - polylines (rendered as triangles)
        Railways = 1,         // Railway tracks - polylines
        Buildings = 2,        // Filled polygons
        Water = 3,            // Lakes, ponds - filled polygons
        Industrial = 4,       // Industrial areas - filled polygons
        Military = 5,         // Military areas - filled polygons
        Platforms = 6,        // Railway platforms - filled polygons
        Forests = 7,          // Forests - filled polygons
        POIs = 8,             // Railway stations/halts/signals - single points
        Waterways = 9,        // Rivers, streams, canals - polylines (rendered as triangles)
        AdminBoundaries = 10, // Administrative borders (country + voivodeships) - filled polygons
        Places = 11,          // Cities, towns, villages (place=*) - single points
        Coastlines = 12       // OSM natural=coastline ways - polylines (used for synthetic water)
    }

    /// <summary>Number of values in the LayerType enum — use this instead of a hardcoded 13.</summary>
    public const int LayerCount = 13;
    
    public static void WriteLayerHeader(BinaryWriter writer, LayerType layerType, int featureCount)
    {
        writer.Write((int)layerType);
        writer.Write(featureCount);
    }

    // --- Format v7 (multi-LOD) ---

    /// <summary>
    /// Per-LOD data stored in tile index
    /// </summary>
    public struct LODInfo
    {
        public long FileOffset;
        public int CompressedSize;
        public int UncompressedSize;
        public int LayerMask;
    }

    /// <summary>
    /// Tile index entry with 6 LOD levels — v7
    /// </summary>
    public struct TileIndexEntryV7
    {
        public long TileID;
        public int GridX;
        public int GridY;
        public BBox Bounds;
        public LODInfo[] LODs; // One LODInfo per LOD level (LODCount total)
        public int[] FeatureCounts; // LOD0 feature counts for stats
    }

    public static void WriteHeaderV7(BinaryWriter writer, BBox globalBounds, int tilesX, int tilesY, int totalTiles, long indexTableOffset)
    {
        writer.Write(System.Text.Encoding.ASCII.GetBytes(MagicV7));
        writer.Write(7); // Version
        writer.Write(TileGrid.TILE_SIZE);
        writer.Write(globalBounds.MinX);
        writer.Write(globalBounds.MinY);
        writer.Write(globalBounds.MaxX);
        writer.Write(globalBounds.MaxY);
        writer.Write(tilesX);
        writer.Write(tilesY);
        writer.Write(totalTiles);
        writer.Write(indexTableOffset);
        writer.Write(new byte[72]); // Reserved — total 128 bytes
    }

    public static void ReadHeaderV7(BinaryReader reader, out float tileSize, out BBox globalBounds,
        out int tilesX, out int tilesY, out int totalTiles, out long indexTableOffset)
    {
        int version = reader.ReadInt32();
        if (version != 7)
            throw new InvalidDataException($"Expected version 7, got {version}");

        tileSize = reader.ReadSingle();
        globalBounds = new BBox
        {
            MinX = reader.ReadSingle(),
            MinY = reader.ReadSingle(),
            MaxX = reader.ReadSingle(),
            MaxY = reader.ReadSingle()
        };
        tilesX = reader.ReadInt32();
        tilesY = reader.ReadInt32();
        totalTiles = reader.ReadInt32();
        indexTableOffset = reader.ReadInt64();
        reader.ReadBytes(72);
    }

    // --- Format v8 (FORMAP04) ---

    /// <summary>
    /// v8 header (128 bytes). Same fields as v7 plus explicit <paramref name="layerCount"/> and
    /// <paramref name="lodCount"/>, so every reader reads them dynamically instead of assuming
    /// compile-time constants. This permanently removes the v7 layer-count desync trap
    /// (a 12- vs 13-layer file silently misaligning a reader that hard-codes the count).
    /// Layout: magic(8) version(4) tileSize(4) bounds(16) tilesX(4) tilesY(4) totalTiles(4)
    ///         indexTableOffset(8) layerCount(4) lodCount(4) compressionType(4) signatureLength(4)
    ///         reserved(60) = 128 bytes.
    /// compressionType: 0 = LZ4-HC (K4os pickle), 1 = Zstd.
    /// signatureLength: 0 = unsigned, 64 = Ed25519-signed (trailing 64-byte signature at end of file).
    /// </summary>
    public static void WriteHeaderV8(BinaryWriter writer, BBox globalBounds, int tilesX, int tilesY,
        int totalTiles, long indexTableOffset, int layerCount, int lodCount, int compressionType,
        int signatureLength)
    {
        writer.Write(System.Text.Encoding.ASCII.GetBytes(MagicV8));
        writer.Write(8); // Version
        writer.Write(TileGrid.TILE_SIZE);
        writer.Write(globalBounds.MinX);
        writer.Write(globalBounds.MinY);
        writer.Write(globalBounds.MaxX);
        writer.Write(globalBounds.MaxY);
        writer.Write(tilesX);
        writer.Write(tilesY);
        writer.Write(totalTiles);
        writer.Write(indexTableOffset);
        writer.Write(layerCount);       // NEW in v8
        writer.Write(lodCount);         // NEW in v8
        writer.Write(compressionType);  // NEW in v8: 0=LZ4-HC, 1=Zstd
        writer.Write(signatureLength);  // NEW in v8 (Pillar 2): 0=unsigned, 64=Ed25519-signed
        writer.Write(new byte[60]);     // Reserved — total 128 bytes
    }

    /// <summary>
    /// Reads a v8 header. Magic is consumed by the caller; this starts at the version field.
    /// </summary>
    public static void ReadHeaderV8(BinaryReader reader, out float tileSize, out BBox globalBounds,
        out int tilesX, out int tilesY, out int totalTiles, out long indexTableOffset,
        out int layerCount, out int lodCount, out int compressionType, out int signatureLength)
    {
        int version = reader.ReadInt32();
        if (version != 8)
            throw new InvalidDataException($"Expected version 8, got {version}");

        tileSize = reader.ReadSingle();
        globalBounds = new BBox
        {
            MinX = reader.ReadSingle(),
            MinY = reader.ReadSingle(),
            MaxX = reader.ReadSingle(),
            MaxY = reader.ReadSingle()
        };
        tilesX = reader.ReadInt32();
        tilesY = reader.ReadInt32();
        totalTiles = reader.ReadInt32();
        indexTableOffset = reader.ReadInt64();
        layerCount = reader.ReadInt32();
        lodCount = reader.ReadInt32();
        compressionType = reader.ReadInt32();
        signatureLength = reader.ReadInt32();
        reader.ReadBytes(60);
    }

    public static void WriteTileIndexEntryV7(BinaryWriter writer, TileIndexEntryV7 entry)
    {
        writer.Write(entry.TileID);
        writer.Write(entry.GridX);
        writer.Write(entry.GridY);
        writer.Write(entry.Bounds.MinX);
        writer.Write(entry.Bounds.MinY);
        writer.Write(entry.Bounds.MaxX);
        writer.Write(entry.Bounds.MaxY);

        // LODCount LOD infos
        for (int i = 0; i < LODCount; i++)
        {
            var lod = entry.LODs[i];
            writer.Write(lod.FileOffset);
            writer.Write(lod.CompressedSize);
            writer.Write(lod.UncompressedSize);
            writer.Write(lod.LayerMask);
        }

        // Feature counts (LOD0)
        for (int i = 0; i < LayerCount; i++)
        {
            writer.Write(entry.FeatureCounts != null && i < entry.FeatureCounts.Length ? entry.FeatureCounts[i] : 0);
        }
    }

    public static TileIndexEntryV7 ReadTileIndexEntryV7(BinaryReader reader)
    {
        var entry = new TileIndexEntryV7
        {
            TileID = reader.ReadInt64(),
            GridX = reader.ReadInt32(),
            GridY = reader.ReadInt32(),
            Bounds = new BBox
            {
                MinX = reader.ReadSingle(),
                MinY = reader.ReadSingle(),
                MaxX = reader.ReadSingle(),
                MaxY = reader.ReadSingle()
            },
            LODs = new LODInfo[LODCount]
        };

        for (int i = 0; i < LODCount; i++)
        {
            entry.LODs[i] = new LODInfo
            {
                FileOffset = reader.ReadInt64(),
                CompressedSize = reader.ReadInt32(),
                UncompressedSize = reader.ReadInt32(),
                LayerMask = reader.ReadInt32()
            };
        }

        entry.FeatureCounts = new int[LayerCount];
        for (int i = 0; i < LayerCount; i++)
        {
            entry.FeatureCounts[i] = reader.ReadInt32();
        }

        return entry;
    }
}

public struct BBox
{
    public float MinX;
    public float MinY;
    public float MaxX;
    public float MaxY;
    
    public bool IsValid => MinX <= MaxX && MinY <= MaxY;
    
    public static BBox Empty => new BBox { MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue };
    
    public void Expand(float x, float y)
    {
        if (x < MinX) MinX = x;
        if (x > MaxX) MaxX = x;
        if (y < MinY) MinY = y;
        if (y > MaxY) MaxY = y;
    }
    
    public void Expand(BBox other)
    {
        if (!other.IsValid) return;
        Expand(other.MinX, other.MinY);
        Expand(other.MaxX, other.MaxY);
    }
}
