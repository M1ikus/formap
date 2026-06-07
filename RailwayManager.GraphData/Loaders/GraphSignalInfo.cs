namespace RailwayManager.GraphData
{
    public enum GraphSignalFunction
    {
        Unknown,
        Entry,        // station entry signal
        Exit,         // station exit signal
        Block,        // line / block signal
        Intermediate  // intermediate / other main signal
    }

    public enum GraphSignalDirection
    {
        Both,
        Forward,     // railway:signal:direction=forward
        Backward     // railway:signal:direction=backward
    }

    /// <summary>
    /// Signal from OSM (railway=signal). Ported from Unity's Timetable.SignalInfo.
    /// </summary>
    public class GraphSignalInfo
    {
        public int NodeId;                       // PathNodeId snapped position
        public GraphSignalFunction Function;
        public GraphSignalDirection Direction;
        public string? RefNum;                    // ref tag from OSM
    }
}
