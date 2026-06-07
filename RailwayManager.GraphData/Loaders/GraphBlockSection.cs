namespace RailwayManager.GraphData
{
    public enum GraphBoundaryType : byte
    {
        Junction,    // turnout (node with 3+ edges)
        Signal,      // signal from OSM
        LineEnd,     // end of line (1 edge)
        Station      // station (railway=station/halt)
    }

    /// <summary>
    /// Block (signal) section — a portion of the graph between two boundaries
    /// (station-station, signal-signal, junction-junction, etc.).
    /// Ported from Unity's Timetable.BlockSection.
    /// </summary>
    public struct GraphBlockSection
    {
        public int Id;
        public int StartNodeId;
        public int EndNodeId;
        public float LengthM;
        public int MaxSpeedKmh;
        public int EdgeCount;
        public GraphBoundaryType StartBoundary;
        public GraphBoundaryType EndBoundary;
    }
}
