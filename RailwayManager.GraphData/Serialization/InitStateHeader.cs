namespace RailwayManager.GraphData
{
    /// <summary>
    /// Header dla init-state-{countryCode}.bin — pre-built initialization state
    /// dla pełnego kraju (PathfindingGraph + Loaders cache + BlockSections).
    ///
    /// **DLC architecture:** każdy kraj ma osobny plik (init-state-pl.bin, init-state-de.bin
    /// itp.). Player kupuje DLC → game ładuje dodatkowy plik → unified pathfinding poprzez
    /// border crossings (CrossCountryLinks section).
    ///
    /// Format binary big-endian (BinaryWriter default little-endian — Unity i .NET 8 same).
    /// </summary>
    public class InitStateHeader
    {
        /// <summary>Magic bytes na początku pliku — identifikuje format.</summary>
        public const string Magic = "INITSTATE";

        /// <summary>Format version — bump przy breaking change. Loader sprawdza compat.
        /// v2 (2026-05-11): GraphStationPlatform.Position field (centroid peronu).
        /// v3 (2026-05-11): edge.metadata["railway:line_ref"] (numer linii kolejowej z OSM
        /// route relations propagated przez formap CreateRailwayMesh).</summary>
        public const int CurrentVersion = 3;

        /// <summary>ISO 3166-1 alpha-2 (PL, DE, CZ itd.) — identifikuje kraj w pliku.</summary>
        public string? CountryCode;

        /// <summary>Wersja formatu — Loader rejects gdy != CurrentVersion (force regenerate).</summary>
        public int Version;

        /// <summary>Source map file mtime (Unix seconds) — invalidate cache gdy poland-v7.bin newer.</summary>
        public long SourceMapMtime;

        /// <summary>Build params — sprawdzane czy match runtime config.</summary>
        public float CellSizeM;
        public float JunctionToleranceM;
        public float GraphCellSizeM;

        /// <summary>Liczby sections do early validation.</summary>
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
