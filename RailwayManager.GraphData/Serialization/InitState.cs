using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Top-level container z całym pre-built initialization state dla jednego kraju.
    /// Serializowany do init-state-{countryCode}.bin w formap, deserializowany w Unity.
    ///
    /// **DLC:** każdy kraj ma osobny InitState. Multi-country runtime: load N plików,
    /// merge przez border crossings (CrossCountryLinks, MVP=0 list).
    /// </summary>
    public class InitState
    {
        public InitStateHeader Header = new InitStateHeader();
        public List<GraphAdminRegion> AdminRegions = new List<GraphAdminRegion>();
        public GraphPathfindingGraph PathfindingGraph = new GraphPathfindingGraph();
        public List<GraphCityPlace> Places = new List<GraphCityPlace>();
        public List<GraphRailwayStation> Stations = new List<GraphRailwayStation>();
        public List<GraphStationPlatform> Platforms = new List<GraphStationPlatform>();
        public List<GraphSignalInfo> Signals = new List<GraphSignalInfo>();
        public GraphBlockSectionBuilder.BuildResult BlockSections;

        /// <summary>
        /// OSM natural=coastline ways. Każdy element to lista vertices (line). Używane przez
        /// Unity SyntheticWaterRenderer do generowania mesh'ów Bałtyku i Zalewu Wiślanego
        /// (PBF Polski nie zawiera natural=water dla tych zbiorników — multipolygons zbyt duże).
        /// </summary>
        public List<List<GraphPoint>> Coastlines = new List<List<GraphPoint>>();
        // CrossCountryLinks — Day 5 lub post-DLC. MVP empty.
    }
}
