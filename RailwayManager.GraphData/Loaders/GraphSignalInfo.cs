namespace RailwayManager.GraphData
{
    public enum GraphSignalFunction
    {
        Unknown,
        Entry,        // semafor wjazdowy do stacji
        Exit,         // semafor wyjazdowy ze stacji
        Block,        // semafor szlakowy / blokowy
        Intermediate  // semafor pośredni / inny główny
    }

    public enum GraphSignalDirection
    {
        Both,
        Forward,     // railway:signal:direction=forward
        Backward     // railway:signal:direction=backward
    }

    /// <summary>
    /// Semafor z OSM (railway=signal). Port z Unity Timetable.SignalInfo.
    /// </summary>
    public class GraphSignalInfo
    {
        public int NodeId;                       // PathNodeId snapped position
        public GraphSignalFunction Function;
        public GraphSignalDirection Direction;
        public string? RefNum;                    // ref tag z OSM
    }
}
