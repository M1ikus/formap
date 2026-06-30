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
        /// route relations, propagated by formap's CreateRailwayMesh).
        /// v4 (2026-06-29): SourceMapHash field — freshness gate is now by v8 content hash, not mtime
        /// (mtime changes when poland-v8.bin is copied to StreamingAssets, causing false "stale").
        /// v5 (2026-06-30): track data model (TD-055/056) — §Tracks section, edge TrackIndex+StationId,
        /// station OsmNodeId, platform (TrackIndex,FromM,ToM) entries.</summary>
        public const int CurrentVersion = 5;

        /// <summary>ISO 3166-1 alpha-2 (PL, DE, CZ, etc.) — identifies the country in the file.</summary>
        public string? CountryCode;

        /// <summary>Format version — the loader rejects the file when != CurrentVersion (forces regeneration).</summary>
        public int Version;

        /// <summary>Source map file mtime — informational only since v4 (NOT a freshness gate; mtime changes
        /// when the map is copied to StreamingAssets). Kept for debugging.</summary>
        public long SourceMapMtime;

        /// <summary>v4: SHA-256 of the source v8 map's tile-index region (BinaryFormatV8.ComputeMapIndexHash) —
        /// the content fingerprint the freshness gate compares. Stable across copy/deploy/regen; unaffected by
        /// timestamps. null only for non-v8 source maps (then the gate falls back to magic/version/country).</summary>
        public byte[]? SourceMapHash;

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
        public int TrackCount;            // v5: physical tracks (chains between switches)
        public int CrossCountryLinkCount; // DLC border crossings, MVP=0
    }
}
