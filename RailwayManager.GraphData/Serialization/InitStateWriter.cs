using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Binary serializer for InitState. Format:
    ///
    /// MAGIC (9 bytes ASCII "INITSTATE")
    /// VERSION (4B int)
    /// HEADER:
    ///   CountryCode (string UTF-8 length-prefixed)
    ///   SourceMapMtime (8B long)
    ///   CellSizeM (4B float)
    ///   JunctionToleranceM (4B float)
    ///   GraphCellSizeM (4B float)
    ///   Counts (9 × 4B int) — for early validation
    /// SECTIONS (one after another):
    ///   1. AdminRegions
    ///   2. PathfindingGraph (nodes + edges + junctionIds + cellSize)
    ///   3. Places
    ///   4. Stations
    ///   5. Platforms
    ///   6. Signals
    ///   7. BlockSections (sections + edgeToSection array)
    ///   8. CrossCountryLinks (DLC, MVP=0 entries)
    ///
    /// Strings: 4B length-prefix + UTF-8 bytes. Null = -1 length.
    /// Lists: 4B count + items.
    /// </summary>
    public static class InitStateWriter
    {
        public static void Write(InitState state, string path)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs, Encoding.UTF8);

            // Magic + version
            bw.Write(Encoding.ASCII.GetBytes(InitStateHeader.Magic));
            bw.Write(InitStateHeader.CurrentVersion);

            // Header
            WriteString(bw, state.Header.CountryCode);
            bw.Write(state.Header.SourceMapMtime);
            bw.Write(state.Header.CellSizeM);
            bw.Write(state.Header.JunctionToleranceM);
            bw.Write(state.Header.GraphCellSizeM);
            bw.Write(state.PathfindingGraph.NodeCount);
            bw.Write(state.PathfindingGraph.EdgeCount);
            bw.Write(state.Stations.Count);
            bw.Write(state.Platforms.Count);
            bw.Write(state.AdminRegions.Count);
            bw.Write(state.Places.Count);
            bw.Write(state.Signals.Count);
            bw.Write(state.BlockSections.Sections != null ? state.BlockSections.Sections.Count : 0);
            bw.Write(0); // CrossCountryLinkCount (MVP)

            WriteAdminRegions(bw, state.AdminRegions);
            WritePathfindingGraph(bw, state.PathfindingGraph);
            WritePlaces(bw, state.Places);
            WriteStations(bw, state.Stations);
            WritePlatforms(bw, state.Platforms);
            WriteSignals(bw, state.Signals);
            WriteBlockSections(bw, state.BlockSections);
            WriteCoastlines(bw, state.Coastlines);
            // CrossCountryLinks placeholder
            bw.Write(0);

            GraphLogger.LogInfo($"[InitStateWriter] Wrote {path} ({fs.Length / 1024} KB)");
        }

        private static void WriteCoastlines(BinaryWriter bw, List<List<GraphPoint>> coastlines)
        {
            int count = coastlines != null ? coastlines.Count : 0;
            bw.Write(count);
            if (coastlines == null) return;
            foreach (var line in coastlines)
            {
                int vc = line != null ? line.Count : 0;
                bw.Write(vc);
                if (line == null) continue;
                foreach (var p in line)
                {
                    bw.Write(p.X);
                    bw.Write(p.Y);
                }
            }
        }

        private static void WriteAdminRegions(BinaryWriter bw, List<GraphAdminRegion> regions)
        {
            bw.Write(regions.Count);
            foreach (var r in regions)
            {
                WriteString(bw, r.Name);
                bw.Write(r.AdminLevel);
                WriteString(bw, r.Iso3166_1);
                WriteString(bw, r.Iso3166_2);
                bw.Write(r.BoundingBox.MinX);
                bw.Write(r.BoundingBox.MinY);
                bw.Write(r.BoundingBox.MaxX);
                bw.Write(r.BoundingBox.MaxY);
                bw.Write(r.Vertices.Count);
                foreach (var v in r.Vertices) { bw.Write(v.X); bw.Write(v.Y); }
                bw.Write(r.Indices.Count);
                foreach (var i in r.Indices) bw.Write(i);
            }
        }

        private static void WritePathfindingGraph(BinaryWriter bw, GraphPathfindingGraph graph)
        {
            bw.Write(graph.CellSize);
            // Nodes
            bw.Write(graph.NodeCount);
            for (int i = 0; i < graph.NodeCount; i++)
            {
                var n = graph.Nodes[i];
                bw.Write(n.Id);
                bw.Write(n.Position.X);
                bw.Write(n.Position.Y);
                bw.Write(n.EdgeIds.Count);
                foreach (int eid in n.EdgeIds) bw.Write(eid);
            }
            // Edges
            bw.Write(graph.EdgeCount);
            for (int i = 0; i < graph.EdgeCount; i++)
            {
                var e = graph.Edges[i];
                bw.Write(e.Id);
                bw.Write(e.FromNodeId);
                bw.Write(e.ToNodeId);
                bw.Write(e.SegmentId);
                bw.Write(e.LengthM);
                bw.Write(e.MaxSpeedKmh);
                bw.Write(e.IsOsmForward);
                // Metadata: we write "railway:track_ref" (used by BlockSection) + "railway:line_ref"
                // (v3 2026-05-11: railway line number from OSM route relations, propagated by formap).
                if (e.Metadata != null && e.Metadata.TryGetValue("railway:track_ref", out var tr))
                    WriteString(bw, tr);
                else
                    WriteString(bw, null);
                if (e.Metadata != null && e.Metadata.TryGetValue("railway:line_ref", out var lr))
                    WriteString(bw, lr);
                else
                    WriteString(bw, null);
            }
            // JunctionNodeIds
            bw.Write(graph.JunctionNodeIds.Count);
            foreach (int id in graph.JunctionNodeIds) bw.Write(id);
        }

        private static void WritePlaces(BinaryWriter bw, List<GraphCityPlace> places)
        {
            bw.Write(places.Count);
            foreach (var p in places)
            {
                WriteString(bw, p.Name);
                bw.Write(p.Position.X);
                bw.Write(p.Position.Y);
                bw.Write((byte)p.Type);
                bw.Write(p.Population);
                WriteString(bw, p.Voivodeship);
            }
        }

        private static void WriteStations(BinaryWriter bw, List<GraphRailwayStation> stations)
        {
            bw.Write(stations.Count);
            foreach (var s in stations)
            {
                bw.Write(s.StationId);
                WriteString(bw, s.Name);
                bw.Write(s.Position.X);
                bw.Write(s.Position.Y);
                bw.Write(s.IsMajorStation);
                bw.Write(s.PathNodeId);
                WriteString(bw, s.Voivodeship);
                WriteString(bw, s.CityName);
            }
        }

        private static void WritePlatforms(BinaryWriter bw, List<GraphStationPlatform> platforms)
        {
            bw.Write(platforms.Count);
            foreach (var p in platforms)
            {
                bw.Write(p.PlatformId);
                bw.Write(p.StationNodeId);
                // v2 (2026-05-11): platform centroid (2 floats) — used to propagate platform→track track_ref.
                bw.Write(p.Position.X);
                bw.Write(p.Position.Y);
                WriteString(bw, p.PlatformName);
                WriteString(bw, p.TrackRef);
                bw.Write(p.LengthM);
            }
        }

        private static void WriteSignals(BinaryWriter bw, List<GraphSignalInfo> signals)
        {
            bw.Write(signals.Count);
            foreach (var s in signals)
            {
                bw.Write(s.NodeId);
                bw.Write((byte)s.Function);
                bw.Write((byte)s.Direction);
                WriteString(bw, s.RefNum);
            }
        }

        private static void WriteBlockSections(BinaryWriter bw, GraphBlockSectionBuilder.BuildResult bs)
        {
            int sectionCount = bs.Sections != null ? bs.Sections.Count : 0;
            bw.Write(sectionCount);
            if (bs.Sections != null)
            {
                foreach (var s in bs.Sections)
                {
                    bw.Write(s.Id);
                    bw.Write(s.StartNodeId);
                    bw.Write(s.EndNodeId);
                    bw.Write(s.LengthM);
                    bw.Write(s.MaxSpeedKmh);
                    bw.Write(s.EdgeCount);
                    bw.Write((byte)s.StartBoundary);
                    bw.Write((byte)s.EndBoundary);
                }
            }
            int edgeMapLen = bs.EdgeToSection != null ? bs.EdgeToSection.Length : 0;
            bw.Write(edgeMapLen);
            if (bs.EdgeToSection != null)
                foreach (int v in bs.EdgeToSection) bw.Write(v);
        }

        private static void WriteString(BinaryWriter bw, string? s)
        {
            if (s == null) { bw.Write(-1); return; }
            var bytes = Encoding.UTF8.GetBytes(s);
            bw.Write(bytes.Length);
            bw.Write(bytes);
        }
    }
}
