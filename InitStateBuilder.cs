using System;
using System.Collections.Generic;
using System.IO;
using K4os.Compression.LZ4;
using RailwayManager.GraphData;

namespace formap;

/// <summary>
/// Builds the pre-built init-state-{country}.bin next to poland-v7.bin.
/// Reads the logic layers from the .bin (Railways/AdminBoundaries/Places/POIs/Platforms),
/// runs the GraphData builders, and serializes the InitState.
///
/// Unity loads the result in seconds, eliminating the 600s build for all of Poland.
/// </summary>
public static class InitStateBuilder
{
    private static readonly HashSet<BinaryFormat.LayerType> LogicLayers = new()
    {
        BinaryFormat.LayerType.Railways,
        BinaryFormat.LayerType.AdminBoundaries,
        BinaryFormat.LayerType.Places,
        BinaryFormat.LayerType.POIs,
        BinaryFormat.LayerType.Platforms,
        BinaryFormat.LayerType.Coastlines // for Unity's SyntheticWaterRenderer
    };

    public static void BuildAndWrite(string mapBinPath, string countryCode)
    {
        // Plug the logger into Console (formap context)
        GraphLogger.Info ??= Console.WriteLine;
        GraphLogger.Warn ??= msg => Console.WriteLine($"WARN: {msg}");
        GraphLogger.Error ??= msg => Console.Error.WriteLine($"ERROR: {msg}");

        var t0 = DateTime.UtcNow;
        Console.WriteLine($"[InitStateBuilder] Reading logic layers from {mapBinPath}...");
        var layers = ReadAllLogicLayers(mapBinPath);

        var state = new InitState();
        state.Header.CountryCode = countryCode;
        state.Header.Version = InitStateHeader.CurrentVersion;
        state.Header.SourceMapMtime = new FileInfo(mapBinPath).LastWriteTimeUtc.ToFileTimeUtc();
        // v4: pin the v8 content fingerprint so the freshness gate survives copy/deploy (mtime is unreliable).
        try { state.Header.SourceMapHash = BinaryFormatV8.ComputeMapIndexHash(mapBinPath); }
        catch (Exception ex) { Console.WriteLine($"[InitStateBuilder] WARN: no v8 content hash for {mapBinPath} ({ex.Message}); freshness gate falls back to version/country."); }
        state.Header.CellSizeM = 10f;
        state.Header.JunctionToleranceM = 0.5f;
        state.Header.GraphCellSizeM = 10f;

        Console.WriteLine($"[InitStateBuilder] Building AdminRegions ({layers[BinaryFormat.LayerType.AdminBoundaries].Count} features)...");
        state.AdminRegions = GraphAdminBoundaryBuilder.Build(layers[BinaryFormat.LayerType.AdminBoundaries]);
        var resolver = new GraphVoivodeshipResolver(state.AdminRegions);

        Console.WriteLine($"[InitStateBuilder] Building PathfindingGraph ({layers[BinaryFormat.LayerType.Railways].Count} railway features)...");
        var railwayMainline = FilterMainlineRailways(layers[BinaryFormat.LayerType.Railways]);
        Console.WriteLine($"[InitStateBuilder]   filtered to {railwayMainline.Count} mainline (skip tram/sidings/etc.)");
        state.PathfindingGraph.BuildFromFeaturesUnionFind(railwayMainline, state.Header.GraphCellSizeM);

        // v5: physical tracks (chains between switches) + per-edge TrackIndex. TrackKey is content-derived from
        // each segment's (osm wayId, vertexIndex), so map segmentId → (wayId, vtx) from the source features.
        var segMap = new Dictionary<int, (long wayId, int vtx)>();
        foreach (var f in railwayMainline)
        {
            if (f?.SegmentIds == null) continue;
            long wid = 0;
            if (f.Metadata != null && f.Metadata.TryGetValue("osm:way_id", out var ws)) long.TryParse(ws, out wid);
            for (int i = 0; i < f.SegmentIds.Count; i++) segMap[f.SegmentIds[i]] = (wid, i);
        }
        state.Tracks = GraphTrackBuilder.Build(state.PathfindingGraph, segMap);
        Console.WriteLine($"[InitStateBuilder] Built {state.Tracks.Count} physical tracks (chains between switches).");

        Console.WriteLine($"[InitStateBuilder] Building Places ({layers[BinaryFormat.LayerType.Places].Count} features)...");
        state.Places = GraphPlaceBuilder.Build(layers[BinaryFormat.LayerType.Places], resolver);

        Console.WriteLine($"[InitStateBuilder] Building Stations + Platforms + Signals...");
        state.Stations = GraphStationBuilder.Build(layers[BinaryFormat.LayerType.POIs], state.PathfindingGraph, 200f, resolver);
        // v5: deterministic station ids — order by stable OsmNodeId, then StationId = array index (== edge.StationId).
        state.Stations.Sort((a, b) =>
        {
            int c = a.OsmNodeId.CompareTo(b.OsmNodeId);
            if (c != 0) return c;
            c = a.Position.X.CompareTo(b.Position.X);
            if (c != 0) return c;
            return a.Position.Y.CompareTo(b.Position.Y);
        });
        for (int i = 0; i < state.Stations.Count; i++) state.Stations[i].StationId = i;
        state.Platforms = GraphPlatformBuilder.Build(layers[BinaryFormat.LayerType.Platforms], state.PathfindingGraph, state.Tracks);
        // v5: stamp edge.StationId — needs platforms (the station's line-set = its platforms' track line_refs,
        // which bars the extent from spilling across a switch onto a parallel line). After stations + platforms.
        GraphStationExtentBuilder.Assign(state.PathfindingGraph, state.Stations, state.Platforms, state.Tracks, 1500f);
        state.Signals = GraphSignalsBuilder.Build(layers[BinaryFormat.LayerType.POIs], state.PathfindingGraph);

        Console.WriteLine($"[InitStateBuilder] Building BlockSections...");
        var stationNodes = new HashSet<int>();
        foreach (var st in state.Stations)
            if (st.PathNodeId >= 0 && st.IsMajorStation)
                stationNodes.Add(st.PathNodeId);
        state.BlockSections = GraphBlockSectionBuilder.Build(state.PathfindingGraph, stationNodes);

        // Coastlines: keep raw lines (no triangulation) for Unity's SyntheticWaterRenderer.
        // Each coastline = a list of vertices (line, no closing).
        if (layers.TryGetValue(BinaryFormat.LayerType.Coastlines, out var coastlineFeatures))
        {
            state.Coastlines = new List<List<GraphPoint>>(coastlineFeatures.Count);
            foreach (var f in coastlineFeatures)
            {
                if (f?.Vertices == null || f.Vertices.Count < 2) continue;
                state.Coastlines.Add(new List<GraphPoint>(f.Vertices));
            }
            Console.WriteLine($"[InitStateBuilder] Coastlines: {state.Coastlines.Count} lines saved");
        }

        string outputPath = GetInitStatePath(mapBinPath, countryCode);
        Console.WriteLine($"[InitStateBuilder] Writing {outputPath}...");
        InitStateWriter.Write(state, outputPath);

        var elapsed = (DateTime.UtcNow - t0).TotalSeconds;
        Console.WriteLine($"[InitStateBuilder] DONE in {elapsed:F1}s — {state.PathfindingGraph.NodeCount} nodes, "
            + $"{state.PathfindingGraph.EdgeCount} edges, {state.Stations.Count} stations, "
            + $"{state.Platforms.Count} platforms, {state.BlockSections.Sections.Count} block sections.");
    }

    public static string GetInitStatePath(string mapBinPath, string countryCode)
    {
        string dir = Path.GetDirectoryName(mapBinPath) ?? ".";
        return Path.Combine(dir, $"init-state-{countryCode.ToLower()}.bin");
    }

    /// <summary>
    /// Filter mainline railways — skip tram/narrow_gauge/light_rail/abandoned/sidings/yards/etc.
    /// Mirrors the logic from Unity's RailwayFeatureCollector.IsMainlineRail.
    /// </summary>
    private static List<GraphMeshGeometry> FilterMainlineRailways(List<GraphMeshGeometry> features)
    {
        var result = new List<GraphMeshGeometry>(features.Count);
        foreach (var f in features)
        {
            if (f?.Metadata == null) { result.Add(f!); continue; }

            if (f.Metadata.TryGetValue("railway", out var railway))
            {
                if (railway is "tram" or "narrow_gauge" or "light_rail" or "monorail"
                    or "subway" or "construction" or "abandoned" or "disused"
                    or "preserved" or "miniature" or "funicular") continue;
            }
            if (f.Metadata.TryGetValue("service", out var service))
            {
                // Keep service=crossover: a crossover is a real track-switching connection between
                // running tracks that passenger trains use. Excluding it deleted ~5k legitimate links
                // nationwide and forced detours (e.g. Sucha Beskidzka lk97↔lk98 routed via the throat /
                // lk625 instead of the R1-R2 crossover). Only skip shunting yards and dead-end spurs.
                if (service is "yard" or "spur") continue;
            }
            if (f.Metadata.TryGetValue("usage", out var usage))
            {
                if (usage is "industrial" or "tourism" or "military") continue;
            }
            result.Add(f);
        }
        return result;
    }

    /// <summary>
    /// Reads poland-v7.bin tile-by-tile, accumulating logic-layer features per type.
    /// Skips non-logic layers (Buildings/Forests/Highways/etc.) via stream seek.
    /// </summary>
    private static Dictionary<BinaryFormat.LayerType, List<GraphMeshGeometry>> ReadAllLogicLayers(string binPath)
    {
        using var fs = File.OpenRead(binPath);
        using var reader = new BinaryReader(fs);

        // Magic — dispatch v7 / v8
        byte[] magicBytes = reader.ReadBytes(8);
        string magic = System.Text.Encoding.ASCII.GetString(magicBytes);

        if (magic == BinaryFormat.MagicV8)
        {
            fs.Position = 0;
            var v8layers = BinaryFormatV8.ReadLogicLayersV8(fs, LogicLayers);
            var result = new Dictionary<BinaryFormat.LayerType, List<GraphMeshGeometry>>();
            foreach (var lt in LogicLayers) result[lt] = new List<GraphMeshGeometry>();
            foreach (var kv in v8layers)
                foreach (var m in kv.Value)
                    result[kv.Key].Add(ConvertToGraph(m));
            Console.WriteLine("[InitStateBuilder]   v8 format (FORMAP04), logic-layer read DONE:");
            foreach (var kv in result)
                Console.WriteLine($"[InitStateBuilder]     {kv.Key}: {kv.Value.Count} features");
            return result;
        }

        if (magic != BinaryFormat.MagicV7)
            throw new InvalidDataException($"Expected v7 (FORMAP03) or v8 (FORMAP04) format, got: {magic}");

        BinaryFormat.ReadHeaderV7(reader, out float tileSize, out BBox globalBounds,
            out int tilesX, out int tilesY, out int totalTiles, out long indexTableOffset);

        Console.WriteLine($"[InitStateBuilder]   v7 format, {totalTiles} tiles, grid {tilesX}x{tilesY}");

        // Read tile index
        fs.Seek(indexTableOffset, SeekOrigin.Begin);
        var tileEntries = new List<BinaryFormat.TileIndexEntryV7>(totalTiles);
        for (int i = 0; i < totalTiles; i++)
            tileEntries.Add(BinaryFormat.ReadTileIndexEntryV7(reader));

        // Aggregate features
        var layers = new Dictionary<BinaryFormat.LayerType, List<GraphMeshGeometry>>();
        foreach (var lt in LogicLayers) layers[lt] = new List<GraphMeshGeometry>();

        int processed = 0, parsed = 0, skipped = 0;
        foreach (var entry in tileEntries)
        {
            processed++;
            if (processed % 500 == 0)
                Console.WriteLine($"[InitStateBuilder]   tile {processed}/{totalTiles} (parsed={parsed}, skipped={skipped})");

            int lod = 0; // LOD0 for full geometry
            var lodInfo = entry.LODs[lod];
            if (lodInfo.LayerMask == 0 || lodInfo.CompressedSize <= 0) { skipped++; continue; }

            fs.Seek(lodInfo.FileOffset, SeekOrigin.Begin);
            int actualSize = reader.ReadInt32();
            if (actualSize <= 0 || actualSize > 200 * 1024 * 1024) { skipped++; continue; }

            byte[] compressed = reader.ReadBytes(actualSize);
            byte[] decompressed = LZ4Pickler.Unpickle(compressed);

            using var ms = new MemoryStream(decompressed);
            using var tileReader = new BinaryReader(ms);
            for (int li = 0; li < BinaryFormat.LayerCount; li++)
            {
                if ((lodInfo.LayerMask & (1 << li)) == 0) continue;

                int layerTypeInt = tileReader.ReadInt32();
                var layerType = (BinaryFormat.LayerType)layerTypeInt;
                int featureCount = tileReader.ReadInt32();

                if (featureCount < 0 || featureCount > 1_000_000) { parsed++; goto nextTile; }

                if (!LogicLayers.Contains(layerType))
                {
                    for (int i = 0; i < featureCount; i++) SkipFeatureBytes(tileReader);
                    continue;
                }

                for (int i = 0; i < featureCount; i++)
                {
                    var src = MeshGeometry.Read(tileReader);
                    layers[layerType].Add(ConvertToGraph(src));
                }
            }
            parsed++;
            nextTile: ;
        }

        Console.WriteLine($"[InitStateBuilder]   tile read DONE: parsed={parsed}, skipped={skipped}");
        foreach (var kv in layers)
            Console.WriteLine($"[InitStateBuilder]     {kv.Key}: {kv.Value.Count} features");

        return layers;
    }

    /// <summary>Stream-skip a single feature without parsing (mirrors Unity's SkipFeatureBytes).</summary>
    private static void SkipFeatureBytes(BinaryReader r)
    {
        var s = r.BaseStream;
        s.Seek(16, SeekOrigin.Current); // BBox 4 floats
        int vc = r.ReadInt32(); s.Seek(vc * 8L, SeekOrigin.Current);
        int ic = r.ReadInt32(); s.Seek(ic * 4L, SeekOrigin.Current);
        int hc = r.ReadInt32(); s.Seek(hc * 4L, SeekOrigin.Current);
        int sc = r.ReadInt32(); s.Seek(sc * 4L, SeekOrigin.Current);
        int jc = r.ReadInt32(); s.Seek(jc * 4L, SeekOrigin.Current);
        int mc = r.ReadInt32();
        for (int i = 0; i < mc; i++)
        {
            int kl = r.ReadInt32(); s.Seek(kl, SeekOrigin.Current);
            int vl = r.ReadInt32(); s.Seek(vl, SeekOrigin.Current);
        }
    }

    /// <summary>formap.MeshGeometry → GraphMeshGeometry (System.Numerics.Vector2 → GraphPoint).</summary>
    private static GraphMeshGeometry ConvertToGraph(MeshGeometry src)
    {
        var dst = new GraphMeshGeometry
        {
            BoundingBox = new GraphBBox
            {
                MinX = src.BoundingBox.MinX,
                MinY = src.BoundingBox.MinY,
                MaxX = src.BoundingBox.MaxX,
                MaxY = src.BoundingBox.MaxY
            },
            Vertices = new List<GraphPoint>(src.Vertices.Count),
            Indices = new List<int>(src.Indices),
            HoleStarts = new List<int>(src.HoleStarts),
            SegmentIds = new List<int>(src.SegmentIds),
            JunctionIndices = new List<int>(src.JunctionIndices),
            Metadata = new Dictionary<string, string>(src.Metadata)
        };
        foreach (var v in src.Vertices)
            dst.Vertices.Add(new GraphPoint(v.X, v.Y));
        return dst;
    }
}
