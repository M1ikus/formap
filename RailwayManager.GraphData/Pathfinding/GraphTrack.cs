using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Physical track (v5, TD-055/056): a maximal chain of edges between switches (JunctionNodeIds) and
    /// dead-ends. The linear-referencing primitive — platform fromM/toM and kilometrage are measured 0→LengthM
    /// along <see cref="EdgeIds"/> (forward, StartNodeId→EndNodeId; "start" = the bounding node with the smaller
    /// (round(Y),round(X)), so the direction is canonical/regen-stable). Upstream travel uses LengthM − x.
    ///
    /// <see cref="TrackKey"/> is a stable content-derived id (hash over the chain's (osm wayId, vertexIndex));
    /// it survives a regen as long as the underlying OSM ways are unchanged. Persist TrackKey in save-games and
    /// map it to the array index on load (runtime addresses tracks by index).
    /// </summary>
    public class GraphTrack
    {
        public long TrackKey;
        public int StartNodeId;
        public int EndNodeId;
        public float LengthM;
        public List<int> EdgeIds = new(); // forward, Start→End; cumulative length = running sum of edge.LengthM
    }
}
