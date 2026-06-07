namespace RailwayManager.GraphData
{
    public enum GraphBoundaryType : byte
    {
        Junction,    // rozjazd (node z 3+ edge'ami)
        Signal,      // semafor z OSM
        LineEnd,     // koniec linii (1 edge)
        Station      // stacja (railway=station/halt)
    }

    /// <summary>
    /// Odcinek blokowy (semaforowy) — fragment grafu między dwiema granicami
    /// (stacja-stacja, signal-signal, junction-junction itp.).
    /// Port z Unity Timetable.BlockSection.
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
