namespace RailwayManager.GraphData
{
    /// <summary>
    /// Header for init-state-{countryCode}.bin — pre-built initialization state
    /// for a full country (PathfindingGraph + Loaders cache + BlockSections).
    ///
    /// **DLC architecture:** each country has its own file (init-state-pl.bin, init-state-de.bin,
    /// etc.). Player buys DLC → game loads the extra file → unified pathfinding via
    /// border crossings (CrossCountryLinks section).
    ///
    /// Binary format, little-endian (BinaryWriter default — same on Unity and .NET 8).
    /// </summary>
    public class InitStateHeader
    {
        /// <summary>Magic bytes at the start of the file — identifies the format.</summary>
        public const string Magic = "INITSTATE";

        /// <summary>Format version — bump on a breaking change. The loader checks compatibility.
        /// v2 (2026-05-11): GraphStationPlatform.Position field (platform centroid).
        /// v3 (2026-05-11): edge.metadata["railway:line_ref"] (railway line number from OSM
        /// route relations, propagated by formap's CreateRailwayMesh).</summary>
        public const int CurrentVersion = 3;

        /// <summary>ISO 3166-1 alpha-2 (PL, DE, CZ, etc.) — identifies the country in the file.</summary>
        public string? CountryCode;

        /// <summary>Format version — the loader rejects the file when != CurrentVersion (forces regeneration).</summary>
        public int Version;

        /// <summary>Source map file mtime (Unix seconds) — invalidate the cache when poland-v7.bin is newer.</summary>
        public long SourceMapMtime;

        /// <summary>Build params — checked against the runtime config.</summary>
        public float CellSizeM;
        public float JunctionToleranceM;
        public float GraphCellSizeM;

        /// <summary>Section counts for early validation.</summary>
        public int NodeCount;
        public int EdgeCount;
        public int StationCount;
        public int PlatformCount;
        public int RegionCount;
        public int PlaceCount;
        public int SignalCount;
        public int BlockSectionCount;
        public int CrossCountryLinkCount; // DLC border crossings, MVP=0
    }
}
