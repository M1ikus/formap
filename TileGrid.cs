using System.Numerics;

namespace formap;

/// <summary>
/// Manages spatial partitioning of map features into tiles for efficient loading
/// </summary>
public class TileGrid
{
    public const float TILE_SIZE = 10000f; // 10km x 10km tiles

    /// <summary>
    /// Offset to ensure grid coordinates are always positive for tile ID calculation.
    /// Supports grid coordinates from -100000 to +100000 in both X and Y.
    /// </summary>
    private const long GRID_COORDINATE_OFFSET = 100000L;

    /// <summary>
    /// Information about a single tile
    /// </summary>
    public class TileInfo
    {
        public long TileID { get; set; }
        public int GridX { get; set; }
        public int GridY { get; set; }
        public BBox Bounds { get; set; }
        public Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> Features { get; set; } = new();

        // File layout information (filled during write)
        public long FileOffset { get; set; }
        public int CompressedSize { get; set; }
        public int UncompressedSize { get; set; }
        public int LayerMask { get; set; } // Bitmask of which layers exist

        public TileInfo()
        {
            // Initialize all layer lists
            foreach (BinaryFormat.LayerType layerType in Enum.GetValues(typeof(BinaryFormat.LayerType)))
            {
                Features[layerType] = new List<MeshGeometry>();
            }
        }
    }

    public BBox GlobalBounds { get; set; }
    public int TilesX { get; set; }
    public int TilesY { get; set; }
    public Dictionary<long, TileInfo> Tiles { get; private set; } = new();

    /// <summary>
    /// Calculates deterministic tile ID from grid coordinates
    /// Uses Cantor pairing function for unique mapping
    /// </summary>
    public static long GetTileID(int gridX, int gridY)
    {
        // Handle negative coordinates by offsetting to positive space
        long x = (long)gridX + GRID_COORDINATE_OFFSET;
        long y = (long)gridY + GRID_COORDINATE_OFFSET;

        // Cantor pairing function: unique bijection from N×N to N
        return (x + y) * (x + y + 1) / 2 + y;
    }

    /// <summary>
    /// Calculates grid coordinates from world position
    /// </summary>
    public static (int gridX, int gridY) WorldToGrid(float worldX, float worldY)
    {
        return (
            (int)Math.Floor(worldX / TILE_SIZE),
            (int)Math.Floor(worldY / TILE_SIZE)
        );
    }

    /// <summary>
    /// Calculates tile bounds from grid coordinates
    /// </summary>
    public static BBox GetTileBounds(int gridX, int gridY)
    {
        float minX = gridX * TILE_SIZE;
        float minY = gridY * TILE_SIZE;

        return new BBox
        {
            MinX = minX,
            MinY = minY,
            MaxX = minX + TILE_SIZE,
            MaxY = minY + TILE_SIZE
        };
    }

    /// <summary>
    /// Initializes the grid based on global bounds
    /// </summary>
    public void Initialize(BBox globalBounds)
    {
        GlobalBounds = globalBounds;

        // Calculate grid dimensions
        var (minGridX, minGridY) = WorldToGrid(globalBounds.MinX, globalBounds.MinY);
        var (maxGridX, maxGridY) = WorldToGrid(globalBounds.MaxX, globalBounds.MaxY);

        TilesX = maxGridX - minGridX + 1;
        TilesY = maxGridY - minGridY + 1;

        Console.WriteLine($"[TILE GRID] Initialized: {TilesX} x {TilesY} = {TilesX * TilesY} tiles");
        Console.WriteLine($"[TILE GRID] Grid range: X=[{minGridX}, {maxGridX}], Y=[{minGridY}, {maxGridY}]");

        // Pre-create all tiles
        for (int gx = minGridX; gx <= maxGridX; gx++)
        {
            for (int gy = minGridY; gy <= maxGridY; gy++)
            {
                long tileID = GetTileID(gx, gy);
                Tiles[tileID] = new TileInfo
                {
                    TileID = tileID,
                    GridX = gx,
                    GridY = gy,
                    Bounds = GetTileBounds(gx, gy)
                };
            }
        }
    }

    /// <summary>
    /// Adds a feature to all tiles it intersects
    /// </summary>
    public void AddFeature(BinaryFormat.LayerType layerType, MeshGeometry geom)
    {
        if (geom == null || !geom.BoundingBox.IsValid)
            return;

        // Find all tiles this feature intersects
        var affectedTiles = GetAffectedTiles(geom.BoundingBox);

        foreach (long tileID in affectedTiles)
        {
            if (Tiles.TryGetValue(tileID, out var tile))
            {
                // For now, add the whole feature to each tile
                // TODO: In the future, we could clip features to tile boundaries
                // to avoid rendering features outside the tile bounds
                tile.Features[layerType].Add(geom);
            }
        }
    }

    /// <summary>
    /// Finds all tile IDs that intersect with the given bounding box
    /// </summary>
    private List<long> GetAffectedTiles(BBox featureBounds)
    {
        var result = new List<long>();

        if (!featureBounds.IsValid)
            return result;

        // Calculate grid range
        var (minGridX, minGridY) = WorldToGrid(featureBounds.MinX, featureBounds.MinY);
        var (maxGridX, maxGridY) = WorldToGrid(featureBounds.MaxX, featureBounds.MaxY);

        // Add all tiles in range
        for (int gx = minGridX; gx <= maxGridX; gx++)
        {
            for (int gy = minGridY; gy <= maxGridY; gy++)
            {
                long tileID = GetTileID(gx, gy);
                if (Tiles.ContainsKey(tileID))
                {
                    result.Add(tileID);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Calculates layer mask for a tile
    /// </summary>
    public static int CalculateLayerMask(TileInfo tile)
    {
        int mask = 0;

        foreach (var (layerType, features) in tile.Features)
        {
            if (features.Count > 0)
            {
                mask |= (1 << (int)layerType);
            }
        }

        return mask;
    }

    /// <summary>
    /// Gets statistics about the tile grid
    /// </summary>
    public void PrintStatistics()
    {
        Console.WriteLine("\n[TILE GRID STATISTICS]");
        Console.WriteLine($"  Total tiles:        {Tiles.Count:N0}");
        Console.WriteLine($"  Grid dimensions:    {TilesX} x {TilesY}");
        Console.WriteLine($"  Tile size:          {TILE_SIZE:N0}m x {TILE_SIZE:N0}m");

        // Count empty tiles
        int emptyTiles = Tiles.Values.Count(t => t.Features.Values.All(list => list.Count == 0));
        int nonEmptyTiles = Tiles.Count - emptyTiles;

        Console.WriteLine($"  Non-empty tiles:    {nonEmptyTiles:N0} ({(nonEmptyTiles * 100.0 / Tiles.Count):F1}%)");
        Console.WriteLine($"  Empty tiles:        {emptyTiles:N0} ({(emptyTiles * 100.0 / Tiles.Count):F1}%)");

        // Per-layer statistics
        Console.WriteLine("\n  Features per layer:");
        foreach (BinaryFormat.LayerType layerType in Enum.GetValues(typeof(BinaryFormat.LayerType)))
        {
            int totalFeatures = Tiles.Values.Sum(t => t.Features[layerType].Count);
            int tilesWithLayer = Tiles.Values.Count(t => t.Features[layerType].Count > 0);

            if (totalFeatures > 0)
            {
                Console.WriteLine($"    {layerType,-12}: {totalFeatures,10:N0} features in {tilesWithLayer,5:N0} tiles");
            }
        }

        // Find tile with most features
        var maxTile = Tiles.Values.MaxBy(t => t.Features.Values.Sum(list => list.Count));
        if (maxTile != null)
        {
            int maxFeatures = maxTile.Features.Values.Sum(list => list.Count);
            Console.WriteLine($"\n  Busiest tile:       ({maxTile.GridX}, {maxTile.GridY}) with {maxFeatures:N0} features");
        }
    }
}
