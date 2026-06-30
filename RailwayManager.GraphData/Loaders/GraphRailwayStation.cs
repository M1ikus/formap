namespace RailwayManager.GraphData
{
    /// <summary>
    /// Railway station (railway=station|halt). Ported from Unity's Timetable.RailwayStation.
    /// </summary>
    public class GraphRailwayStation
    {
        public int StationId;              // == position in InitState.Stations (== edge.StationId); sorted by OsmNodeId
        public long OsmNodeId;             // v5: source OSM railway=station/halt node id — stable save-game key
        public string? Name;
        public GraphPoint Position;
        public bool IsMajorStation;        // true = railway=station, false = railway=halt
        public int PathNodeId = -1;        // assigned graph node (or -1)
        public string? Voivodeship;
        public string? CityName;            // heuristic: prefix before the first space
    }
}
