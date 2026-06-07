using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Binary deserializer for InitState. Mirrors the format of <see cref="InitStateWriter"/>.
    /// Checks Magic + Version and throws on a mismatch (forces regeneration in formap).
    /// </summary>
    public static class InitStateReader
    {
        public static InitState Read(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8);

            // Magic + version
            byte[] magicBytes = br.ReadBytes(InitStateHeader.Magic.Length);
            string magic = Encoding.ASCII.GetString(magicBytes);
            if (magic != InitStateHeader.Magic)
                throw new InvalidDataException($"InitStateReader: invalid magic '{magic}' (expected '{InitStateHeader.Magic}')");

            int version = br.ReadInt32();
            if (version != InitStateHeader.CurrentVersion)
                throw new InvalidDataException($"InitStateReader: version mismatch {version} != {InitStateHeader.CurrentVersion} (regenerate via formap)");

            var state = new InitState();
            state.Header.Version = version;

            // Header
            state.Header.CountryCode = ReadString(br);
            state.Header.SourceMapMtime = br.ReadInt64();
            state.Header.CellSizeM = br.ReadSingle();
            state.Header.JunctionToleranceM = br.ReadSingle();
            state.Header.GraphCellSizeM = br.ReadSingle();
            state.Header.NodeCount = br.ReadInt32();
            state.Header.EdgeCount = br.ReadInt32();
            state.Header.StationCount = br.ReadInt32();
            state.Header.PlatformCount = br.ReadInt32();
            state.Header.RegionCount = br.ReadInt32();
            state.Header.PlaceCount = br.ReadInt32();
            state.Header.SignalCount = br.ReadInt32();
            state.Header.BlockSectionCount = br.ReadInt32();
            state.Header.CrossCountryLinkCount = br.ReadInt32();

            state.AdminRegions = ReadAdminRegions(br);
            state.PathfindingGraph = ReadPathfindingGraph(br);
            state.Places = ReadPlaces(br);
            state.Stations = ReadStations(br);
            state.Platforms = ReadPlatforms(br);
            state.Signals = ReadSignals(br);
            state.BlockSections = ReadBlockSections(br);
            state.Coastlines = ReadCoastlines(br);

            // CrossCountryLinks (MVP)
            int crossLinkCount = br.ReadInt32();
            // skip — MVP=0

            GraphLogger.LogInfo($"[InitStateReader] Loaded {path} (country={state.Header.CountryCode}, version={state.Header.Version})");
            return state;
        }

        /// <summary>Quick check whether the file is valid and the countryCode matches — without a full load.</summary>
        public static bool IsValidFor(string path, string expectedCountryCode, long maxAcceptedSourceMtime)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs, Encoding.UTF8);
                byte[] magicBytes = br.ReadBytes(InitStateHeader.Magic.Length);
                if (Encoding.ASCII.GetString(magicBytes) != InitStateHeader.Magic) return false;
                int version = br.ReadInt32();
                if (version != InitStateHeader.CurrentVersion) return false;
                string? country = ReadString(br);
                if (!string.IsNullOrEmpty(expectedCountryCode) && country != expectedCountryCode) return false;
                long mtime = br.ReadInt64();
                if (maxAcceptedSourceMtime > 0 && mtime < maxAcceptedSourceMtime) return false;
                return true;
            }
            catch { return false; }
        }

        private static List<GraphAdminRegion> ReadAdminRegions(BinaryReader br)
        {
            int n = br.ReadInt32();
            var list = new List<GraphAdminRegion>(n);
            for (int i = 0; i < n; i++)
            {
                var r = new GraphAdminRegion
                {
                    Name = ReadString(br),
                    AdminLevel = br.ReadInt32(),
                    Iso3166_1 = ReadString(br),
                    Iso3166_2 = ReadString(br),
                    BoundingBox = new GraphBBox
                    {
                        MinX = br.ReadSingle(),
                        MinY = br.ReadSingle(),
                        MaxX = br.ReadSingle(),
                        MaxY = br.ReadSingle()
                    }
                };
                int vCount = br.ReadInt32();
                r.Vertices = new List<GraphPoint>(vCount);
                for (int v = 0; v < vCount; v++)
                    r.Vertices.Add(new GraphPoint(br.ReadSingle(), br.ReadSingle()));
                int iCount = br.ReadInt32();
                r.Indices = new List<int>(iCount);
                for (int idx = 0; idx < iCount; idx++)
                    r.Indices.Add(br.ReadInt32());
                list.Add(r);
            }
            return list;
        }

        private static GraphPathfindingGraph ReadPathfindingGraph(BinaryReader br)
        {
            float cellSize = br.ReadSingle();

            int nodeCount = br.ReadInt32();
            var nodes = new List<GraphNode>(nodeCount);
            for (int i = 0; i < nodeCount; i++)
            {
                var n = new GraphNode
                {
                    Id = br.ReadInt32(),
                    Position = new GraphPoint(br.ReadSingle(), br.ReadSingle())
                };
                int eCount = br.ReadInt32();
                n.EdgeIds = new List<int>(eCount);
                for (int j = 0; j < eCount; j++) n.EdgeIds.Add(br.ReadInt32());
                nodes.Add(n);
            }

            int edgeCount = br.ReadInt32();
            var edges = new List<GraphEdge>(edgeCount);
            for (int i = 0; i < edgeCount; i++)
            {
                var e = new GraphEdge
                {
                    Id = br.ReadInt32(),
                    FromNodeId = br.ReadInt32(),
                    ToNodeId = br.ReadInt32(),
                    SegmentId = br.ReadInt32(),
                    LengthM = br.ReadSingle(),
                    MaxSpeedKmh = br.ReadInt32(),
                    IsOsmForward = br.ReadBoolean()
                };
                string? trackRef = ReadString(br);
                // v3 (2026-05-11): railway:line_ref (railway line number from OSM route relations).
                string? lineRef = ReadString(br);
                if (!string.IsNullOrEmpty(trackRef) || !string.IsNullOrEmpty(lineRef))
                {
                    e.Metadata = new Dictionary<string, string>();
                    if (!string.IsNullOrEmpty(trackRef)) e.Metadata["railway:track_ref"] = trackRef;
                    if (!string.IsNullOrEmpty(lineRef)) e.Metadata["railway:line_ref"] = lineRef;
                }
                edges.Add(e);
            }

            int junctionCount = br.ReadInt32();
            var junctions = new HashSet<int>();
            for (int i = 0; i < junctionCount; i++) junctions.Add(br.ReadInt32());

            var graph = new GraphPathfindingGraph();
            graph.LoadFromSerializedData(nodes, edges, junctions, cellSize);
            return graph;
        }

        private static List<GraphCityPlace> ReadPlaces(BinaryReader br)
        {
            int n = br.ReadInt32();
            var list = new List<GraphCityPlace>(n);
            for (int i = 0; i < n; i++)
            {
                list.Add(new GraphCityPlace
                {
                    Name = ReadString(br),
                    Position = new GraphPoint(br.ReadSingle(), br.ReadSingle()),
                    Type = (GraphPlaceType)br.ReadByte(),
                    Population = br.ReadInt32(),
                    Voivodeship = ReadString(br)
                });
            }
            return list;
        }

        private static List<GraphRailwayStation> ReadStations(BinaryReader br)
        {
            int n = br.ReadInt32();
            var list = new List<GraphRailwayStation>(n);
            for (int i = 0; i < n; i++)
            {
                list.Add(new GraphRailwayStation
                {
                    StationId = br.ReadInt32(),
                    Name = ReadString(br),
                    Position = new GraphPoint(br.ReadSingle(), br.ReadSingle()),
                    IsMajorStation = br.ReadBoolean(),
                    PathNodeId = br.ReadInt32(),
                    Voivodeship = ReadString(br),
                    CityName = ReadString(br)
                });
            }
            return list;
        }

        private static List<GraphStationPlatform> ReadPlatforms(BinaryReader br)
        {
            int n = br.ReadInt32();
            var list = new List<GraphStationPlatform>(n);
            for (int i = 0; i < n; i++)
            {
                list.Add(new GraphStationPlatform
                {
                    PlatformId = br.ReadInt32(),
                    StationNodeId = br.ReadInt32(),
                    // v2 (2026-05-11): platform centroid (2 floats).
                    Position = new GraphPoint(br.ReadSingle(), br.ReadSingle()),
                    PlatformName = ReadString(br),
                    TrackRef = ReadString(br),
                    LengthM = br.ReadSingle()
                });
            }
            return list;
        }

        private static List<GraphSignalInfo> ReadSignals(BinaryReader br)
        {
            int n = br.ReadInt32();
            var list = new List<GraphSignalInfo>(n);
            for (int i = 0; i < n; i++)
            {
                list.Add(new GraphSignalInfo
                {
                    NodeId = br.ReadInt32(),
                    Function = (GraphSignalFunction)br.ReadByte(),
                    Direction = (GraphSignalDirection)br.ReadByte(),
                    RefNum = ReadString(br)
                });
            }
            return list;
        }

        private static GraphBlockSectionBuilder.BuildResult ReadBlockSections(BinaryReader br)
        {
            int sectionCount = br.ReadInt32();
            var sections = new List<GraphBlockSection>(sectionCount);
            for (int i = 0; i < sectionCount; i++)
            {
                sections.Add(new GraphBlockSection
                {
                    Id = br.ReadInt32(),
                    StartNodeId = br.ReadInt32(),
                    EndNodeId = br.ReadInt32(),
                    LengthM = br.ReadSingle(),
                    MaxSpeedKmh = br.ReadInt32(),
                    EdgeCount = br.ReadInt32(),
                    StartBoundary = (GraphBoundaryType)br.ReadByte(),
                    EndBoundary = (GraphBoundaryType)br.ReadByte()
                });
            }
            int edgeMapLen = br.ReadInt32();
            var edgeMap = new int[edgeMapLen];
            for (int i = 0; i < edgeMapLen; i++) edgeMap[i] = br.ReadInt32();
            return new GraphBlockSectionBuilder.BuildResult { Sections = sections, EdgeToSection = edgeMap };
        }

        private static List<List<GraphPoint>> ReadCoastlines(BinaryReader br)
        {
            int count = br.ReadInt32();
            var result = new List<List<GraphPoint>>(count);
            for (int i = 0; i < count; i++)
            {
                int vc = br.ReadInt32();
                var line = new List<GraphPoint>(vc);
                for (int j = 0; j < vc; j++)
                    line.Add(new GraphPoint(br.ReadSingle(), br.ReadSingle()));
                result.Add(line);
            }
            return result;
        }

        private static string? ReadString(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len < 0) return null;
            byte[] bytes = br.ReadBytes(len);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
