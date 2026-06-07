namespace RailwayManager.GraphData
{
    /// <summary>
    /// Station platform (railway=platform). Ported from Unity's Timetable.StationPlatform.
    /// </summary>
    public class GraphStationPlatform
    {
        public int PlatformId;
        public int StationNodeId;     // PathNodeId of the station this platform belongs to
        public GraphPoint Position;   // Platform centroid (X, Y) from OSM Vertices — introduced in v2
        public string? PlatformName;   // ref tag from OSM
        public string? TrackRef;       // railway:track_ref
        public float LengthM;
    }
}
