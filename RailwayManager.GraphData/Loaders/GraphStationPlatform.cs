namespace RailwayManager.GraphData
{
    /// <summary>
    /// Peron stacyjny (railway=platform). Port z Unity Timetable.StationPlatform.
    /// </summary>
    public class GraphStationPlatform
    {
        public int PlatformId;
        public int StationNodeId;     // PathNodeId stacji do której peron należy
        public GraphPoint Position;   // Centroid peronu (X, Y) z OSM Vertices — wprowadzony v2
        public string? PlatformName;   // ref tag z OSM
        public string? TrackRef;       // railway:track_ref
        public float LengthM;
    }
}
