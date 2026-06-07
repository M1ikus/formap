namespace RailwayManager.GraphData
{
    /// <summary>
    /// Stacja kolejowa (railway=station|halt). Port z Unity Timetable.RailwayStation.
    /// </summary>
    public class GraphRailwayStation
    {
        public int StationId;
        public string? Name;
        public GraphPoint Position;
        public bool IsMajorStation;        // true = railway=station, false = railway=halt
        public int PathNodeId = -1;        // przypisany węzeł grafu (lub -1)
        public string? Voivodeship;
        public string? CityName;            // heurystyka: prefix przed pierwszą spacją
    }
}
