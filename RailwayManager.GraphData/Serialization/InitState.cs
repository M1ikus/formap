using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Top-level container with the entire pre-built initialization state for a single country.
    /// Serialized to init-state-{countryCode}.bin in formap, deserialized in Unity.
    ///
    /// **DLC:** each country has its own InitState. Multi-country runtime: load N files,
    /// merge via border crossings (CrossCountryLinks, MVP=0 list).
    /// </summary>
    public class InitState
    {
        public InitStateHeader Header = new InitStateHeader();
        public List<GraphAdminRegion> AdminRegions = new List<GraphAdminRegion>();
        public GraphPathfindingGraph PathfindingGraph = new GraphPathfindingGraph();
        public List<GraphTrack> Tracks = new List<GraphTrack>(); // v5: physical tracks (chains between switches)
        public List<GraphCityPlace> Places = new List<GraphCityPlace>();
        public List<GraphRailwayStation> Stations = new List<GraphRailwayStation>();
        public List<GraphStationPlatform> Platforms = new List<GraphStationPlatform>();
        public List<GraphSignalInfo> Signals = new List<GraphSignalInfo>();
        public GraphBlockSectionBuilder.BuildResult BlockSections;

        /// <summary>
        /// OSM natural=coastline ways. Each element is a list of vertices (a line). Used by
        /// Unity's SyntheticWaterRenderer to generate meshes for the Baltic and the Vistula Lagoon
        /// (the Poland PBF does not contain natural=water for these bodies — the multipolygons are too large).
        /// </summary>
        public List<List<GraphPoint>> Coastlines = new List<List<GraphPoint>>();
        // CrossCountryLinks — reserved for a future (DLC) extension. Empty in the MVP.
    }
}
