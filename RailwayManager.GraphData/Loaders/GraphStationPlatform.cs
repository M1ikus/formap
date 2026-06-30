using System.Collections.Generic;

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
        public List<GraphPlatformEntry> Entries = new(); // v5: (trackIndex, fromM, toM); island platform = 2; none = 0
    }

    /// <summary>v5: one platform-to-track binding — the platform's extent projected onto a track centerline,
    /// in that track's 0→LengthM kilometrage. Island platform = 2 entries (the two adjacent tracks).</summary>
    public struct GraphPlatformEntry
    {
        public int TrackIndex; // index into InitState.Tracks
        public float FromM;
        public float ToM;
    }
}
