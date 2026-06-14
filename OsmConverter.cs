using OsmSharp;
using OsmSharp.Streams;
using OsmSharp.Tags;
using System.Numerics;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;
using K4os.Compression.LZ4;
using LibTessDotNet;

namespace formap;

/// <summary>
/// Converts OSM .pbf files to custom binary format with geometric layers
/// </summary>
public class OsmConverter
{
    private readonly List<MeshGeometry> highways = new();
    private readonly List<MeshGeometry> railways = new();
    private readonly List<MeshGeometry> buildings = new();
    private readonly List<MeshGeometry> waterFeatures = new();
    private readonly List<MeshGeometry> waterways = new();  // Rivers, streams, canals as lines
    private readonly List<MeshGeometry> industrialAreas = new();
    private readonly List<MeshGeometry> militaryAreas = new();
    private readonly List<MeshGeometry> platforms = new();
    private readonly List<MeshGeometry> forests = new();
    private readonly List<MeshGeometry> pois = new();            // railway=station/halt/signal
    private readonly List<MeshGeometry> adminBoundaries = new(); // boundary=administrative (level 2/4)
    private readonly List<MeshGeometry> places = new();          // place=city/town/village
    private readonly List<MeshGeometry> coastlines = new();      // natural=coastline (lines, used for synthetic water)

    // Locks for thread-safe access to layer lists
    private readonly object buildingsLock = new();
    private readonly object waterFeaturesLock = new();
    private readonly object waterwaysLock = new();
    private readonly object forestsLock = new();
    private readonly object industrialAreasLock = new();
    private readonly object militaryAreasLock = new();
    private readonly object platformsLock = new();
    private readonly object highwaysLock = new();
    private readonly object adminBoundariesLock = new();
    private readonly object placesLock = new();
    private readonly object coastlinesLock = new();
    
    private readonly Dictionary<long, int> railwayNodeUseCount = new();
    private int nextRailSegmentId = 1;
    private readonly List<(Way way, List<Vector2> coords, TagsCollectionBase tags)> bufferedRailways = new();

    /// <summary>
    /// Mapping way_id → set of railway line refs (railway line numbers).
    /// OSM stores `ref` on the relation `route=tracks`/`route=railway`, NOT on individual ways. We parse
    /// the railway route relations and propagate `ref` onto member ways as the tag `railway:line_ref`.
    /// A multi-relation way (e.g. lines 9 and 250 on the same track) → multi-ref "9;250".
    /// </summary>
    private readonly Dictionary<long, HashSet<string>> _wayIdToLineRefs = new();
    private readonly List<(Way way, List<Vector2> coords, TagsCollectionBase tags)> bufferedPolygonWays = new();
    private readonly List<(Way way, List<Vector2> coords, TagsCollectionBase tags)> bufferedHighwayWays = new();
    private readonly List<(Way way, List<Vector2> coords, TagsCollectionBase tags)> bufferedWaterwayWays = new();
    private readonly List<(Way way, List<Vector2> coords, TagsCollectionBase tags)> bufferedCoastlineWays = new();
    private readonly Dictionary<long, (Way way, List<Vector2> coords)> wayCache = new();
    private readonly List<(Relation relation, Dictionary<long, (double lat, double lon)> nodes)> bufferedRelations = new();
    private readonly List<(Relation relation, Dictionary<long, (double lat, double lon)> nodes)> bufferedWaterwayRelations = new();
    private long skippedMultipolygons = 0;

    // Approximate conversion from lat/lon to meters in Poland
    // Using simple equirectangular projection centered on the area
    private double centerLat = 0;
    private double centerLon = 0;
    private bool centerComputed = false;
    
    // Progress tracking
    private Stopwatch? overallTimer;
    private Stopwatch? phaseTimer;

    // LibTess triangulation statistics (instance-level, not static for thread safety)
    private long libTessSuccesses = 0;
    private long libTessFailures = 0;
    
    private void LogProgress(string phase, int current, int total, string? extra = null)
    {
        double percent = total > 0 ? (current * 100.0 / total) : 0;
        var elapsed = phaseTimer?.Elapsed ?? TimeSpan.Zero;
        var rate = elapsed.TotalSeconds > 0 ? current / elapsed.TotalSeconds : 0;
        var remaining = rate > 0 && current < total ? TimeSpan.FromSeconds((total - current) / rate) : TimeSpan.Zero;
        
        var extraStr = extra != null ? $" | {extra}" : "";
        var remainingStr = remaining.TotalSeconds > 0 ? $" | ETA: {remaining:mm\\:ss}" : "";
        
        Console.WriteLine($"[{phase}] {current:N0}/{total:N0} ({percent:F1}%) | {elapsed:mm\\:ss}{remainingStr}{extraStr}");
    }
    
    private void LogInfo(string message)
    {
        Console.WriteLine($"[INFO] {message}");
    }
    
    private void LogError(string message)
    {
        Console.WriteLine($"[ERROR] {message}");
    }
    
    private void LogSummary(string message)
    {
        Console.WriteLine($"\n[{message}]");
    }

    /// <summary>
    /// Determines target layer and lock for given OSM tags.
    /// Returns (null, null) if tags don't match any polygon layer.
    /// </summary>
    private (List<MeshGeometry>? layer, object? lockObj) GetTargetLayer(TagsCollectionBase tags)
    {
        if (LayerClassifier.IsBuilding(tags))
            return (buildings, buildingsLock);
        if (LayerClassifier.IsWaterFeature(tags))
            return (waterFeatures, waterFeaturesLock);
        if (LayerClassifier.IsForest(tags))
            return (forests, forestsLock);
        if (LayerClassifier.IsIndustrialArea(tags))
            return (industrialAreas, industrialAreasLock);
        if (LayerClassifier.IsMilitaryArea(tags))
            return (militaryAreas, militaryAreasLock);
        if (LayerClassifier.IsPlatform(tags))
            return (platforms, platformsLock);
        if (LayerClassifier.IsAdminBoundary(tags))
            return (adminBoundaries, adminBoundariesLock);

        return (null, null);
    }

    /// <summary>When set, WriteBinaryV8 reads the file back and compares it bit-exact to the in-memory
    /// features from the same run (definitive losslessness check, no cross-run non-determinism). Validation only.</summary>
    public bool VerifyAfterWriteV8;

    /// <summary>v8 block compression: 0 = LZ4-HC (fast decode, default), 1 = Zstd (smaller, slower decode).</summary>
    public int CompressionTypeV8;

    /// <summary>Optional 32-byte raw Ed25519 private seed (Pillar 2). When set, WriteBinaryV8 signs the v8
    /// file: the index byte region is Ed25519-signed and a trailing 64-byte signature is appended. null = unsigned.</summary>
    public byte[]? SigningPrivateKeyV8;

    public void Convert(string inputFile, string outputFile, int formatVersion = 7)
    {
        overallTimer = Stopwatch.StartNew();
        LogInfo($"Starting conversion: {Path.GetFileName(inputFile)}");
        
        using var fileStream = new FileInfo(inputFile).OpenRead();
        var source = new PBFOsmStreamSource(fileStream);
        
        // Compute center point for projection
        phaseTimer = Stopwatch.StartNew();
        LogInfo("Computing area center...");
        
        // First pass: collect all nodes coordinates and compute center
        var nodes = new Dictionary<long, (double lat, double lon)>();
        int nodeScanCount = 0;
        
        foreach (var element in source)
        {
            if (element is Node node && node.Latitude.HasValue && node.Longitude.HasValue && node.Id.HasValue)
            {
                nodes[node.Id.Value] = (node.Latitude.Value, node.Longitude.Value);
                nodeScanCount++;
                if (nodeScanCount % 100000 == 0)
                {
                    // Use carriage return for live progress update
                    Console.Write($"\r[SCAN NODES] Found {nodeScanCount:N0} nodes...");
                }
            }
        }
        LogInfo($"Scanned {nodeScanCount:N0} nodes total");
        
        // Compute center from collected nodes (using only Dictionary, no duplicate List)
        if (nodes.Count > 0)
        {
            centerLat = nodes.Values.Average(n => n.lat);
            centerLon = nodes.Values.Average(n => n.lon);
            centerComputed = true;
            LogInfo($"Area center: {centerLat:F6}, {centerLon:F6}");
        }
        
        LogInfo("Processing OSM features...");

        // Combined pass: buffer railways (count junctions) and buffer polygon ways for parallel processing
        railwayNodeUseCount.Clear();
        bufferedRailways.Clear();
        bufferedPolygonWays.Clear();
        bufferedHighwayWays.Clear();
        wayCache.Clear();
        bufferedRelations.Clear();
        bufferedWaterwayRelations.Clear();
        
        // Reset stream and create new source (PBFOsmStreamSource cannot be reused)
        fileStream.Position = 0;
        var source2 = new PBFOsmStreamSource(fileStream);
        
        phaseTimer = Stopwatch.StartNew();
        int processedCount = 0;
        int wayCount = 0;
        int nodeCount = 0;
        int relationCount = 0;
        
        // First, count total elements for better progress reporting (optional - skip if too slow)
        // For now, we'll just report as we go
        
        foreach (var element in source2)
        {
            processedCount++;
            // Log less frequently to reduce I/O overhead (every 100k instead of 25k)
            if (processedCount % 100000 == 0)
            {
                LogProgress("PROCESS ELEMENTS", processedCount, processedCount, $"ways: {wayCount:N0}, nodes: {nodeCount:N0}, relations: {relationCount:N0}");
            }
            
            if (element is Way way)
            {
                wayCount++;
                try
                {
                    // Just convert and cache - processing happens in parallel later
                    ProcessWayForCache(way, nodes);
                }
                catch (Exception ex)
                {
                    // Log but continue - skip problematic ways
                    LogError($"Way {way.Id}: {ex.Message}");
                }
            }
            else if (element is Node node && node.Tags != null && node.Tags.Count > 0)
            {
                nodeCount++;
                ProcessNode(node);
            }
            else if (element is Relation relation)
            {
                relationCount++;
                if (LayerClassifier.IsMultipolygon(relation))
                    bufferedRelations.Add((relation, nodes));
                else if (LayerClassifier.IsWaterwayRelation(relation))
                    bufferedWaterwayRelations.Add((relation, nodes));
                else if (LayerClassifier.IsRailwayRouteRelation(relation))
                    ProcessRailwayRouteRelation(relation);
            }
        }
        
        LogProgress("PROCESS ELEMENTS", processedCount, processedCount, $"ways: {wayCount:N0}, nodes: {nodeCount:N0}, relations: {relationCount:N0}");

        // Process buffered polygon ways in parallel (triangulation is CPU-intensive)
        if (bufferedPolygonWays.Count > 0)
        {
            phaseTimer = Stopwatch.StartNew();
            LogInfo($"Processing {bufferedPolygonWays.Count:N0} polygon ways (using {Math.Max(Environment.ProcessorCount * 2, Environment.ProcessorCount + 4)} threads)...");
            int polygonCount = 0;
            object polygonProgressLock = new();
            
            int maxParallel = Math.Max(Environment.ProcessorCount * 2, Environment.ProcessorCount + 4);
            Parallel.ForEach(Partitioner.Create(bufferedPolygonWays, loadBalance: true), 
                new ParallelOptions { MaxDegreeOfParallelism = maxParallel },
                (item, state) =>
                {
                    var (way, coords, tags) = item;

                    // Determine target layer using shared method
                    var (targetLayer, targetLock) = GetTargetLayer(tags);

                    if (targetLayer != null && targetLock != null)
                    {
                        try
                        {
                            var mesh = CreatePolygonMesh(coords, tags);
                            if (mesh != null)
                            {
                                lock (targetLock)
                                {
                                    targetLayer.Add(mesh);
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // Log error but continue - skip problematic polygons
                            // Errors here are expected for invalid OSM data
                        }
                    }
                    
                    // Thread-safe progress reporting
                    int current = System.Threading.Interlocked.Increment(ref polygonCount);
                    if (current % 10000 == 0 || current == bufferedPolygonWays.Count)
                    {
                        lock (polygonProgressLock)
                        {
                            LogProgress("POLYGON WAYS", current, bufferedPolygonWays.Count);
                        }
                    }
                });
            
            LogProgress("POLYGON WAYS", bufferedPolygonWays.Count, bufferedPolygonWays.Count);
            bufferedPolygonWays.Clear();
        }
        
        // Process buffered highway ways in parallel
        if (bufferedHighwayWays.Count > 0)
        {
            phaseTimer = Stopwatch.StartNew();
            LogInfo($"Processing {bufferedHighwayWays.Count:N0} highway ways (using {Math.Max(Environment.ProcessorCount * 2, Environment.ProcessorCount + 4)} threads)...");
            int highwayCount = 0;
            object highwayProgressLock = new();
            
            int maxParallel = Math.Max(Environment.ProcessorCount * 2, Environment.ProcessorCount + 4);
            Parallel.ForEach(Partitioner.Create(bufferedHighwayWays, loadBalance: true), 
                new ParallelOptions { MaxDegreeOfParallelism = maxParallel },
                (item, state) =>
                {
                    var (way, coords, tags) = item;
                    
                    try
                    {
                        var mesh = CreateLineMesh(coords, tags);
                        if (mesh != null)
                        {
                            lock (highwaysLock)
                            {
                                highways.Add(mesh);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Log error but continue - skip problematic highways
                        // Errors here are expected for invalid OSM data
                    }
                    
                    // Thread-safe progress reporting
                    int current = System.Threading.Interlocked.Increment(ref highwayCount);
                    if (current % 100000 == 0 || current == bufferedHighwayWays.Count)
                    {
                        lock (highwayProgressLock)
                        {
                            LogProgress("HIGHWAY WAYS", current, bufferedHighwayWays.Count);
                        }
                    }
                });
            
            LogProgress("HIGHWAY WAYS", bufferedHighwayWays.Count, bufferedHighwayWays.Count);
            bufferedHighwayWays.Clear();
        }

        // Process buffered waterway ways in parallel
        if (bufferedWaterwayWays.Count > 0)
        {
            phaseTimer = Stopwatch.StartNew();
            LogInfo($"Processing {bufferedWaterwayWays.Count:N0} waterway ways...");
            int waterwayCount = 0;
            object waterwayProgressLock = new();

            int maxParallel = Math.Max(Environment.ProcessorCount * 2, Environment.ProcessorCount + 4);
            Parallel.ForEach(Partitioner.Create(bufferedWaterwayWays, loadBalance: true),
                new ParallelOptions { MaxDegreeOfParallelism = maxParallel },
                (item, state) =>
                {
                    var (way, coords, tags) = item;

                    try
                    {
                        var mesh = CreateWaterwayMesh(coords, tags);
                        if (mesh != null)
                        {
                            lock (waterwaysLock)
                            {
                                waterways.Add(mesh);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Log error but continue - skip problematic waterways
                        // Errors here are expected for invalid OSM data
                    }

                    // Thread-safe progress reporting
                    int current = System.Threading.Interlocked.Increment(ref waterwayCount);
                    if (current % 10000 == 0 || current == bufferedWaterwayWays.Count)
                    {
                        lock (waterwayProgressLock)
                        {
                            LogProgress("WATERWAY WAYS", current, bufferedWaterwayWays.Count);
                        }
                    }
                });

            LogProgress("WATERWAY WAYS", bufferedWaterwayWays.Count, bufferedWaterwayWays.Count);
            bufferedWaterwayWays.Clear();
        }

        // Process buffered coastline ways — kept as raw lines (no triangulation),
        // used by Unity's SyntheticWaterRenderer to generate water polygons.
        if (bufferedCoastlineWays.Count > 0)
        {
            phaseTimer = Stopwatch.StartNew();
            LogInfo($"Processing {bufferedCoastlineWays.Count:N0} coastline ways...");
            int coastCount = 0;

            int maxParallel = Math.Max(Environment.ProcessorCount * 2, Environment.ProcessorCount + 4);
            Parallel.ForEach(bufferedCoastlineWays,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallel },
                (item) =>
                {
                    var (way, coords, tags) = item;
                    if (coords.Count < 2) return;

                    var mesh = new MeshGeometry
                    {
                        Vertices = new List<Vector2>(coords),
                        Indices = new List<int>(), // line: no triangulation
                        HoleStarts = new List<int>(),
                        SegmentIds = new List<int>(),
                        JunctionIndices = new List<int>(),
                        Metadata = new Dictionary<string, string>()
                    };
                    foreach (var t in tags) mesh.Metadata[t.Key] = t.Value;
                    mesh.ComputeBoundingBox();

                    lock (coastlinesLock) { coastlines.Add(mesh); }
                    System.Threading.Interlocked.Increment(ref coastCount);
                });

            LogInfo($"Coastlines DONE: {coastCount:N0} ways, {phaseTimer.Elapsed.TotalSeconds:F1}s");
            bufferedCoastlineWays.Clear();
        }

        // Materialize buffered railways after counts are complete (parallel processing)
        if (bufferedRailways.Count > 0)
        {
            phaseTimer = Stopwatch.StartNew();
            LogInfo($"Materializing {bufferedRailways.Count:N0} railways...");
            int railCount = 0;
            object railProgressLock = new();
            
            // Parallel processing of railways for better CPU utilization
            int maxParallel = Math.Max(Environment.ProcessorCount * 2, Environment.ProcessorCount + 4);
            Parallel.ForEach(bufferedRailways, new ParallelOptions 
            { 
                MaxDegreeOfParallelism = maxParallel 
            }, (br, state) =>
            {
                var mesh = CreateRailwayMesh(br.way, br.coords, br.tags);
                if (mesh != null)
                {
                    // Thread-safe addition
                    lock (railProgressLock)
                    {
                        railways.Add(mesh);
                    }
                }
                
                // Thread-safe progress reporting
                int current = System.Threading.Interlocked.Increment(ref railCount);
                if (current % 500 == 0 || current == bufferedRailways.Count)
                {
                    lock (railProgressLock)
                    {
                        LogProgress("RAILWAYS", current, bufferedRailways.Count);
                    }
                }
            });
            
            LogProgress("RAILWAYS", bufferedRailways.Count, bufferedRailways.Count);
            bufferedRailways.Clear();
        }
        
        // Process multipolygons after all ways are cached (parallel processing with better load balancing)
        if (bufferedRelations.Count > 0)
        {
            phaseTimer = Stopwatch.StartNew();
            // Use more threads than CPU cores to maximize CPU utilization (hyperthreading, I/O wait)
            // For I/O-bound or mixed workloads, 2-4x cores is optimal
            int maxParallel = Math.Max(Environment.ProcessorCount * 2, Environment.ProcessorCount + 4);
            LogInfo($"Processing {bufferedRelations.Count:N0} multipolygons (using {maxParallel} threads, {Environment.ProcessorCount} CPU cores)...");
            skippedMultipolygons = 0;
            int mpCount = 0;
            object progressLock = new();
            DateTime lastProgressUpdate = DateTime.Now;
            var lastUpdateLock = new object();
            
            // Use Partitioner with dynamic load balancing for better work distribution
            // This prevents threads from getting stuck on large multipolygons
            var partitioner = Partitioner.Create(bufferedRelations, loadBalance: true);
            
            Parallel.ForEach(partitioner, new ParallelOptions 
            { 
                MaxDegreeOfParallelism = maxParallel 
            }, (item, state) =>
            {
                var (relation, relNodes) = item;
                
                try
                {
                    ProcessMultipolygon(relation, relNodes);
                }
                catch (Exception ex)
                {
                    System.Threading.Interlocked.Increment(ref skippedMultipolygons);
                    // Only log errors for first few errors
                    if (System.Threading.Interlocked.Read(ref skippedMultipolygons) <= 5)
                    {
                        lock (progressLock)
                        {
                            LogError($"Multipolygon {relation.Id}: {ex.Message}");
                        }
                    }
                }
                
                // Thread-safe progress reporting (after processing, not before)
                int currentCount = System.Threading.Interlocked.Increment(ref mpCount);
                
                // Update progress less frequently to reduce lock contention
                bool shouldUpdate = currentCount % 50 == 0 || currentCount == bufferedRelations.Count;
                
                if (shouldUpdate)
                {
                    // Try to update progress, but don't block if lock is taken
                    bool lockTaken = false;
                    try
                    {
                        Monitor.TryEnter(lastUpdateLock, 100, ref lockTaken);
                        if (lockTaken)
                        {
                            var now = DateTime.Now;
                            if (currentCount % 50 == 0 || (now - lastProgressUpdate).TotalSeconds > 2.0)
                            {
                                var skipped = System.Threading.Interlocked.Read(ref skippedMultipolygons);
                                LogProgress("MULTIPOLYGONS", currentCount, bufferedRelations.Count, 
                                           skipped > 0 ? $"skipped: {skipped:N0}" : null);
                                lastProgressUpdate = now;
                            }
                        }
                    }
                    finally
                    {
                        if (lockTaken)
                            Monitor.Exit(lastUpdateLock);
                    }
                }
            });
            
            // Final progress update
            LogProgress("MULTIPOLYGONS", bufferedRelations.Count, bufferedRelations.Count,
                       skippedMultipolygons > 0 ? $"skipped: {skippedMultipolygons:N0}" : null);
            
            if (skippedMultipolygons > 0)
                LogInfo($"Skipped {skippedMultipolygons:N0} problematic multipolygons");

            // LibTess statistics
            LogInfo($"LibTess stats: {libTessSuccesses:N0} successes, {libTessFailures:N0} failures (fallbacks)");

            bufferedRelations.Clear();
        }

        // Process waterway relations (rivers/streams as multi-segment lines)
        if (bufferedWaterwayRelations.Count > 0)
        {
            phaseTimer = Stopwatch.StartNew();
            int maxParallel = Math.Max(Environment.ProcessorCount * 2, Environment.ProcessorCount + 4);
            LogInfo($"Processing {bufferedWaterwayRelations.Count:N0} waterway relations...");
            int waterwayRelCount = 0;
            object waterwayRelLock = new();

            Parallel.ForEach(Partitioner.Create(bufferedWaterwayRelations, loadBalance: true),
                new ParallelOptions { MaxDegreeOfParallelism = maxParallel },
                (item, state) =>
                {
                    var (relation, relNodes) = item;

                    try
                    {
                        ProcessWaterwayRelation(relation, relNodes);
                    }
                    catch (Exception)
                    {
                        // Skip problematic waterway relations
                    }

                    int current = System.Threading.Interlocked.Increment(ref waterwayRelCount);
                    if (current % 100 == 0 || current == bufferedWaterwayRelations.Count)
                    {
                        lock (waterwayRelLock)
                        {
                            LogProgress("WATERWAY RELATIONS", current, bufferedWaterwayRelations.Count);
                        }
                    }
                });

            LogProgress("WATERWAY RELATIONS", bufferedWaterwayRelations.Count, bufferedWaterwayRelations.Count);
            bufferedWaterwayRelations.Clear();
        }

        LogSummary("SUMMARY");
        Console.WriteLine($"  Highways:     {highways.Count,10:N0}");
        Console.WriteLine($"  Railways:     {railways.Count,10:N0}");
        Console.WriteLine($"  Buildings:    {buildings.Count,10:N0}");
        Console.WriteLine($"  Water:        {waterFeatures.Count,10:N0}");
        Console.WriteLine($"  Waterways:    {waterways.Count,10:N0}");
        Console.WriteLine($"  Industrial:   {industrialAreas.Count,10:N0}");
        Console.WriteLine($"  Military:     {militaryAreas.Count,10:N0}");
        Console.WriteLine($"  Platforms:    {platforms.Count,10:N0}");
        Console.WriteLine($"  Forests:      {forests.Count,10:N0}");
        Console.WriteLine($"  POIs:         {pois.Count,10:N0}");
        Console.WriteLine($"  AdminBnd:     {adminBoundaries.Count,10:N0}");
        Console.WriteLine($"  Places:       {places.Count,10:N0}");
        
        phaseTimer = Stopwatch.StartNew();
        LogInfo("Writing binary output...");

        // Write tiled format
        if (formatVersion == 8)
            WriteBinaryV8(outputFile);
        else
            WriteBinaryV7(outputFile);

        overallTimer?.Stop();
        LogSummary("COMPLETE");
        Console.WriteLine($"  Total time: {overallTimer?.Elapsed:mm\\:ss\\.ff}");
        Console.WriteLine($"  Output file: {Path.GetFileName(outputFile)}");
    }
    
    private void ProcessWayForCache(Way way, Dictionary<long, (double lat, double lon)> nodes)
    {
        if (!way.Id.HasValue)
            return;
        if (way.Nodes == null || way.Nodes.Length < 2)
            return;
        
        // Convert way nodes to coordinates
        var coords = new List<Vector2>();
        foreach (var nodeId in way.Nodes)
        {
            if (nodes.TryGetValue(nodeId, out var coord))
            {
                coords.Add(LatLonToMeters(coord.lat, coord.lon));
            }
        }
        
        if (coords.Count < 2)
            return;
        
        // Cache way for multipolygon processing
        wayCache[way.Id.Value] = (way, coords);
        
        var tags = way.Tags;
        if (tags == null)
            return;

        // Check if way is closed (first node == last node) - only closed ways can be polygons
        bool isClosedWay = way.Nodes.Length >= 3 && way.Nodes[0] == way.Nodes[way.Nodes.Length - 1];

        // Determine feature type and buffer for parallel processing
        // Buffer polygon ways (triangulation is CPU-intensive) and highways for parallel processing
        // IMPORTANT: Only closed ways should be treated as polygons!
        if (isClosedWay && (LayerClassifier.IsBuilding(tags) || LayerClassifier.IsWaterFeature(tags) || LayerClassifier.IsForest(tags) ||
            LayerClassifier.IsIndustrialArea(tags) || LayerClassifier.IsMilitaryArea(tags) || LayerClassifier.IsPlatform(tags)))
        {
            // Buffer polygon ways for parallel processing (triangulation is slow)
            bufferedPolygonWays.Add((way, coords, tags));
        }
        else if (LayerClassifier.IsHighway(tags))
        {
            // Buffer highways for parallel processing
            bufferedHighwayWays.Add((way, coords, tags));
        }
        else if (LayerClassifier.IsWaterwayLine(tags))
        {
            // Buffer waterways (rivers, streams, canals) for parallel processing
            bufferedWaterwayWays.Add((way, coords, tags));

            // Debug: Log specific waterway ID
            if (way.Id == 306138218)
            {
                string name = tags.ContainsKey("name") ? tags["name"] : "unnamed";
                Console.WriteLine($"[DEBUG] Found waterway way {way.Id}: {name}, coords: {coords.Count}");
            }
        }
        // Coastlines disabled — not used (gulfs/lagoons/Baltic are covered by natural=bay/place=sea
        // multipolygons in the Water layer + CountryOutsideMesh for areas outside PL).
        // else if (LayerClassifier.IsCoastline(tags)) { bufferedCoastlineWays.Add((way, coords, tags)); }
        else if (LayerClassifier.IsRailway(tags))
        {
            // Buffer and count unique node usages for junction detection
            bufferedRailways.Add((way, coords, tags));
            var uniq = new HashSet<long>();
            foreach (var nodeId in way.Nodes)
            {
                if (nodes.ContainsKey(nodeId)) uniq.Add(nodeId);
            }
            foreach (var nid in uniq)
            {
                if (!railwayNodeUseCount.TryGetValue(nid, out var c)) c = 0;
                railwayNodeUseCount[nid] = c + 1;
            }
        }
    }
    
    /// <summary>
    /// Propagate `ref` from a railway route relation onto its member ways.
    /// Stores the mapping way_id → set of refs in `_wayIdToLineRefs`. A multi-ref ("9;204")
    /// on the relation is split into separate refs. A member way may belong to multiple relations
    /// (e.g. lines 9 + 250 on the same track) → multi-ref join.
    /// </summary>
    private void ProcessRailwayRouteRelation(Relation relation)
    {
        if (relation.Tags == null || relation.Members == null) return;
        if (!relation.Tags.ContainsKey("ref")) return;
        var refTag = relation.Tags["ref"];
        if (string.IsNullOrEmpty(refTag)) return;

        // Split multi-ref ("9;204" → ["9", "204"])
        var refs = new List<string>();
        foreach (var r in refTag.Split(';'))
        {
            var trimmed = r.Trim();
            if (!string.IsNullOrEmpty(trimmed)) refs.Add(trimmed);
        }
        if (refs.Count == 0) return;

        // Iteruj members typu Way, dodaj refs do mappingu
        foreach (var member in relation.Members)
        {
            if (member.Type != OsmGeoType.Way) continue;
            long wayId = member.Id;
            if (!_wayIdToLineRefs.TryGetValue(wayId, out var set))
            {
                set = new HashSet<string>();
                _wayIdToLineRefs[wayId] = set;
            }
            foreach (var r in refs) set.Add(r);
        }
    }

    private void ProcessMultipolygon(Relation relation, Dictionary<long, (double lat, double lon)> nodes)
    {
        if (relation.Members == null || relation.Tags == null)
            return;

        bool isAdmin = LayerClassifier.IsAdminBoundary(relation.Tags);

        // Collect outer, inner, and unassigned ways
        var outerWays = new List<List<Vector2>>();
        var innerWays = new List<List<Vector2>>();
        var unassignedWays = new List<List<Vector2>>();

        foreach (var member in relation.Members)
        {
            if (member.Type != OsmGeoType.Way)
                continue;

            long memberId = member.Id;
            if (!wayCache.TryGetValue(memberId, out var cached))
                continue;

            var coords = cached.coords;
            if (coords.Count < 2)
                continue;

            string role = member.Role ?? "";
            if (role == "outer")
            {
                outerWays.Add(coords);
            }
            else if (role == "inner")
            {
                innerWays.Add(coords);
            }
            else
            {
                // No role specified - will be auto-assigned later
                unassignedWays.Add(coords);
            }
        }


        // If no explicit outer ways, treat unassigned as potential outers
        if (outerWays.Count == 0 && unassignedWays.Count > 0)
        {
            outerWays.AddRange(unassignedWays);
            unassignedWays.Clear();
        }

        if (outerWays.Count == 0)
            return;
        
        // Determine layer type from relation tags using shared method
        var tags = relation.Tags;
        var (targetLayer, targetLock) = GetTargetLayer(tags);

        if (targetLayer == null || targetLock == null)
            return;

        // Build closed rings from all way groups.
        // For water (natural=water/bay, place=sea, water=*) we raise maxCloseGap to 200km — the outer ring
        // may be cropped at the country border (Gulf of Gdańsk, Vistula Lagoon, Baltic on the RU side).
        // The auto-close gap between the first/last vertex creates an "artificial" closure along the PL
        // border — acceptable, because the Baltic/gulf is covered by CountryOutsideMesh outside PL anyway.
        bool isWaterMP = LayerClassifier.IsWaterFeature(tags);
        float gapOverride = isWaterMP ? 200000f : 30f;
        var closedOuterRings = BuildClosedRings(outerWays, gapOverride);
        var closedInnerRings = BuildClosedRings(innerWays, gapOverride);
        var closedUnassignedRings = BuildClosedRings(unassignedWays, gapOverride);

        // Auto-classify unassigned rings based on area and containment
        if (closedUnassignedRings.Count > 0)
        {
            PolygonUtils.ClassifyRings(closedUnassignedRings, closedOuterRings, closedInnerRings);
        }

        if (closedOuterRings.Count == 0)
            return;

        // Administrative boundaries (voivodeship, country) are very large polygons with high
        // vertex counts — pre-simplify aggressively to fit under CreateSinglePolygonMesh limits.
        // For gameplay detection ("in which voivodeship is station X") ~250m precision is plenty.
        // PREVIOUSLY: 100m tolerance dropped Poland (>8000 verts after 100m), Mazowieckie, Wielkopolskie.
        // 250m tolerance: Poland ~3-5k verts, voivodeships ~500-1500 verts — everything fits.
        if (isAdmin)
        {
            var simplifiedOuter = new List<List<Vector2>>(closedOuterRings.Count);
            foreach (var ring in closedOuterRings)
            {
                var s = SimplifyPolygon(ring, tolerance: 250f);
                if (s.Count >= 3) simplifiedOuter.Add(s);
            }
            closedOuterRings = simplifiedOuter;

            var simplifiedInner = new List<List<Vector2>>(closedInnerRings.Count);
            foreach (var ring in closedInnerRings)
            {
                var s = SimplifyPolygon(ring, tolerance: 250f);
                if (s.Count >= 3) simplifiedInner.Add(s);
            }
            closedInnerRings = simplifiedInner;
        }

        // Diagnostics for admin features — what made it into the build
        if (isAdmin)
        {
            tags.TryGetValue("name", out var adminName);
            tags.TryGetValue("admin_level", out var adminLvl);
            int outerVertsTotal = 0;
            foreach (var r in closedOuterRings) outerVertsTotal += r.Count;
            LogInfo($"[Admin] '{adminName}' lvl={adminLvl}: {closedOuterRings.Count} outer rings ({outerVertsTotal} verts total), {closedInnerRings.Count} holes");
        }

        // Process EACH outer ring separately to avoid stretched triangles
        int adminMeshesAdded = 0;
        int adminMeshesNull = 0;
        foreach (var outerRing in closedOuterRings)
        {
            try
            {
                // Find inner rings (holes) that are inside this outer ring
                var holesForThisRing = new List<List<Vector2>>();
                foreach (var innerRing in closedInnerRings)
                {
                    if (PolygonUtils.IsRingInsideRing(innerRing, outerRing))
                    {
                        holesForThisRing.Add(innerRing);
                    }
                }

                var mesh = CreateSinglePolygonMesh(outerRing, holesForThisRing, tags);
                if (mesh != null)
                {
                    lock (targetLock)
                    {
                        targetLayer.Add(mesh);
                    }
                    if (isAdmin) adminMeshesAdded++;
                }
                else
                {
                    if (isAdmin) adminMeshesNull++;
                }
            }
            catch (Exception)
            {
                System.Threading.Interlocked.Increment(ref skippedMultipolygons);
                if (isAdmin) adminMeshesNull++;
            }
        }

        if (isAdmin && (adminMeshesAdded > 0 || adminMeshesNull > 0))
        {
            tags.TryGetValue("name", out var adminName);
            LogInfo($"[Admin]   '{adminName}' result: {adminMeshesAdded} meshes added, {adminMeshesNull} null/exception");
        }
    }

    /// <summary>
    /// Processes a waterway relation by merging all member ways into a continuous line
    /// and creating a triangle strip mesh for rendering
    /// </summary>
    private void ProcessWaterwayRelation(Relation relation, Dictionary<long, (double lat, double lon)> nodes)
    {
        if (relation.Members == null || relation.Tags == null)
            return;

        string name = relation.Tags.ContainsKey("name") ? relation.Tags["name"] : "unnamed";

        // Collect all way segments from relation members
        var waySegments = new List<List<Vector2>>();

        foreach (var member in relation.Members)
        {
            if (member.Type != OsmGeoType.Way)
                continue;

            // Try to get from cache first
            if (wayCache.TryGetValue(member.Id, out var cached))
            {
                waySegments.Add(new List<Vector2>(cached.coords));
            }
        }

        if (waySegments.Count == 0)
        {
            Console.WriteLine($"[DEBUG] Waterway relation {relation.Id} ({name}): no way segments found in cache");
            return;
        }

        Console.WriteLine($"[DEBUG] Processing waterway relation {relation.Id} ({name}): {waySegments.Count} segments");

        // Merge all segments into a single continuous line
        var mergedLine = MergeWaySegments(waySegments);

        if (mergedLine == null || mergedLine.Count < 2)
        {
            Console.WriteLine($"[DEBUG] Waterway relation {relation.Id} ({name}): merge failed");
            return;
        }

        Console.WriteLine($"[DEBUG] Waterway relation {relation.Id} ({name}): merged {mergedLine.Count} points");

        // Create waterway mesh from merged line
        var mesh = CreateWaterwayMesh(mergedLine, relation.Tags);

        if (mesh != null)
        {
            Console.WriteLine($"[DEBUG] Waterway relation {relation.Id} ({name}): mesh created with {mesh.Vertices.Count} vertices");
            lock (waterwaysLock)
            {
                waterways.Add(mesh);
            }
        }
        else
        {
            Console.WriteLine($"[DEBUG] Waterway relation {relation.Id} ({name}): mesh creation failed");
        }
    }

    /// <summary>
    /// Merges multiple way segments into a single continuous line.
    /// Attempts to connect segments in the correct order.
    /// </summary>
    private List<Vector2>? MergeWaySegments(List<List<Vector2>> segments)
    {
        if (segments.Count == 0)
            return null;

        if (segments.Count == 1)
            return segments[0];

        // Start with first segment
        var result = new List<Vector2>(segments[0]);
        var used = new HashSet<int> { 0 };

        const float eps = 10f; // 10 meters tolerance for connecting segments

        // Keep trying to extend the line by finding connecting segments
        bool foundConnection = true;
        while (foundConnection && used.Count < segments.Count)
        {
            foundConnection = false;
            var first = result[0];
            var last = result[result.Count - 1];

            for (int i = 0; i < segments.Count; i++)
            {
                if (used.Contains(i))
                    continue;

                var seg = segments[i];
                var segFirst = seg[0];
                var segLast = seg[seg.Count - 1];

                float distLastToFirst = Vector2.Distance(last, segFirst);
                float distLastToLast = Vector2.Distance(last, segLast);
                float distFirstToFirst = Vector2.Distance(first, segFirst);
                float distFirstToLast = Vector2.Distance(first, segLast);

                // Try to append to end
                if (distLastToFirst < eps)
                {
                    // Append segment (skip first point to avoid duplication)
                    for (int j = 1; j < seg.Count; j++)
                        result.Add(seg[j]);
                    used.Add(i);
                    foundConnection = true;
                    break;
                }
                else if (distLastToLast < eps)
                {
                    // Append reversed segment (skip last point)
                    for (int j = seg.Count - 2; j >= 0; j--)
                        result.Add(seg[j]);
                    used.Add(i);
                    foundConnection = true;
                    break;
                }
                // Try to prepend to beginning
                else if (distFirstToLast < eps)
                {
                    // Prepend segment (skip last point)
                    var temp = new List<Vector2>();
                    for (int j = 0; j < seg.Count - 1; j++)
                        temp.Add(seg[j]);
                    temp.AddRange(result);
                    result = temp;
                    used.Add(i);
                    foundConnection = true;
                    break;
                }
                else if (distFirstToFirst < eps)
                {
                    // Prepend reversed segment (skip first point)
                    var temp = new List<Vector2>();
                    for (int j = seg.Count - 1; j > 0; j--)
                        temp.Add(seg[j]);
                    temp.AddRange(result);
                    result = temp;
                    used.Add(i);
                    foundConnection = true;
                    break;
                }
            }
        }

        // Return merged line (even if not all segments were connected)
        return result.Count >= 2 ? result : null;
    }

    /// <summary>
    /// Creates a mesh from a single outer ring with optional holes
    /// </summary>
    private MeshGeometry? CreateSinglePolygonMesh(List<Vector2> outerRing, List<List<Vector2>> holes, TagsCollectionBase tags)
    {
        if (outerRing.Count < 3)
            return null;

        var outerCoords = new List<Vector2>(outerRing);

        // Simplify outer polygon if needed - LibTessDotNet handles large polygons well.
        // PREVIOUSLY: an 8000 limit dropped the Poland country polygon (~30k verts raw, >8k after simplification).
        // 30000 limit: Poland after 250m pre-simplification is ~3-5k, Mazowieckie ~1.5k — everything fits.
        const int MAX_OUTER_VERTICES = 30000;
        if (outerCoords.Count > MAX_OUTER_VERTICES)
        {
            // Iterative simplification with increasing tolerance (5m → 10m → 50m → 200m)
            // until the polygon fits or we degenerate to 0.
            float[] tolerances = { 5f, 10f, 50f, 200f, 1000f };
            foreach (var tol in tolerances)
            {
                outerCoords = SimplifyPolygon(outerCoords, tolerance: tol);
                if (outerCoords.Count <= MAX_OUTER_VERTICES) break;
            }
        }

        if (outerCoords.Count < 3)
            return null;

        float outerTolerance = outerCoords.Count > 2000 ? 10.0f : (outerCoords.Count > 1000 ? 5.0f : (outerCoords.Count > 500 ? 2.0f : 0.1f));
        outerCoords = SimplifyPolygon(outerCoords, tolerance: outerTolerance);

        if (outerCoords.Count < 3 || outerCoords.Count > MAX_OUTER_VERTICES)
            return null;


        // Build vertices: outer polygon first, then inner holes
        var vertices = new List<Vector2>(outerCoords);
        var holeStarts = new List<int>();

        // Add holes - increased limits for large multipolygons
        const int MAX_HOLES = 100;           // Was 10 - now supports complex multipolygons
        const int MAX_HOLE_VERTICES = 2000;  // Was 1000 - allows larger holes
        const int MAX_TOTAL_VERTICES = 15000; // Was 5000 - LibTessDotNet handles large polygons well
        int holesAdded = 0;

        foreach (var hole in holes)
        {
            if (hole.Count >= 3 && holesAdded < MAX_HOLES)
            {
                float holeTolerance = hole.Count > 500 ? 10.0f : (hole.Count > 200 ? 5.0f : 0.1f);
                var simplifiedHole = SimplifyPolygon(hole, tolerance: holeTolerance);

                // Remove duplicate closing vertex from hole
                if (simplifiedHole.Count > 1 && NearlyEqual(simplifiedHole[0], simplifiedHole[^1]))
                {
                    simplifiedHole.RemoveAt(simplifiedHole.Count - 1);
                }

                if (simplifiedHole.Count >= 3 && simplifiedHole.Count < MAX_HOLE_VERTICES)
                {
                    if (vertices.Count + simplifiedHole.Count > MAX_TOTAL_VERTICES)
                        break;

                    holeStarts.Add(vertices.Count);
                    vertices.AddRange(simplifiedHole);
                    holesAdded++;
                }
            }
        }

        // Use LibTessDotNet for robust triangulation with holes
        var (finalVertices, indices) = TriangulateWithLibTess(vertices, holeStarts);

        if (indices.Count < 3)
            return null;

        var geom = new MeshGeometry
        {
            Vertices = finalVertices,
            HoleStarts = new List<int>(), // Holes are merged into vertices, no longer needed
            Indices = indices
        };

        ExtractMetadata(geom, tags);
        geom.ComputeBoundingBox();

        return geom;
    }

    /// <summary>
    /// Triangulates a polygon with holes using LibTessDotNet.
    /// LibTess is robust and handles complex polygons well.
    /// </summary>
    private (List<Vector2> vertices, List<int> indices) TriangulateWithLibTess(
        List<Vector2> vertices, List<int> holeStarts)
    {
        // Get outer polygon vertices (before first hole)
        int outerEnd = holeStarts.Count > 0 ? holeStarts[0] : vertices.Count;

        if (outerEnd < 3)
            return (new List<Vector2>(), new List<int>());

        // Extract outer polygon
        var outerPoly = new List<Vector2>();
        for (int i = 0; i < outerEnd; i++)
            outerPoly.Add(vertices[i]);

        // Remove duplicate closing vertex if present
        if (outerPoly.Count > 2 && NearlyEqual(outerPoly[0], outerPoly[^1]))
            outerPoly.RemoveAt(outerPoly.Count - 1);

        if (outerPoly.Count < 3)
            return (new List<Vector2>(), new List<int>());

        try
        {
            var tess = new Tess();

            // Ensure outer is CCW (counter-clockwise) - LibTess expects this for outer contours
            if (!PolygonUtils.IsPolygonCCW(outerPoly))
                outerPoly.Reverse();

            // Add outer contour
            var outerContour = new ContourVertex[outerPoly.Count];
            for (int i = 0; i < outerPoly.Count; i++)
            {
                outerContour[i] = new ContourVertex
                {
                    Position = new Vec3(outerPoly[i].X, outerPoly[i].Y, 0)
                };
            }
            tess.AddContour(outerContour);

            // Add hole contours (must be CW - clockwise, opposite to outer)
            if (holeStarts.Count > 0)
            {
                var sortedHoleStarts = holeStarts.OrderBy(h => h).ToList();
                for (int hIdx = 0; hIdx < sortedHoleStarts.Count; hIdx++)
                {
                    int holeStart = sortedHoleStarts[hIdx];
                    int holeEnd = (hIdx < sortedHoleStarts.Count - 1)
                        ? sortedHoleStarts[hIdx + 1]
                        : vertices.Count;

                    int holeSize = holeEnd - holeStart;
                    if (holeSize < 3) continue;

                    var holePoly = new List<Vector2>();
                    for (int i = holeStart; i < holeEnd; i++)
                        holePoly.Add(vertices[i]);

                    // Remove duplicate closing vertex
                    if (holePoly.Count > 2 && NearlyEqual(holePoly[0], holePoly[^1]))
                        holePoly.RemoveAt(holePoly.Count - 1);

                    if (holePoly.Count < 3) continue;

                    // Ensure hole is CW (clockwise) - opposite to outer
                    if (PolygonUtils.IsPolygonCCW(holePoly))
                        holePoly.Reverse();

                    var holeContour = new ContourVertex[holePoly.Count];
                    for (int i = 0; i < holePoly.Count; i++)
                    {
                        holeContour[i] = new ContourVertex
                        {
                            Position = new Vec3(holePoly[i].X, holePoly[i].Y, 0)
                        };
                    }
                    tess.AddContour(holeContour);
                }
            }

            // Tessellate - NonZero works well when orientations are correct
            tess.Tessellate(WindingRule.NonZero, ElementType.Polygons, 3);

            if (tess.ElementCount == 0)
            {
                // Try with different winding rules
                tess = new Tess();
                for (int i = 0; i < outerPoly.Count; i++)
                {
                    outerContour[i] = new ContourVertex
                    {
                        Position = new Vec3(outerPoly[i].X, outerPoly[i].Y, 0)
                    };
                }
                tess.AddContour(outerContour);
                tess.Tessellate(WindingRule.Positive, ElementType.Polygons, 3);
            }

            if (tess.ElementCount > 0)
            {
                var resultVertices = new List<Vector2>();
                var resultIndices = new List<int>();

                for (int i = 0; i < tess.VertexCount; i++)
                {
                    resultVertices.Add(new Vector2(tess.Vertices[i].Position.X, tess.Vertices[i].Position.Y));
                }

                for (int i = 0; i < tess.ElementCount; i++)
                {
                    int idx0 = tess.Elements[i * 3];
                    int idx1 = tess.Elements[i * 3 + 1];
                    int idx2 = tess.Elements[i * 3 + 2];

                    if (idx0 >= 0 && idx0 < resultVertices.Count &&
                        idx1 >= 0 && idx1 < resultVertices.Count &&
                        idx2 >= 0 && idx2 < resultVertices.Count)
                    {
                        resultIndices.Add(idx0);
                        resultIndices.Add(idx1);
                        resultIndices.Add(idx2);
                    }
                }

                if (resultIndices.Count >= 3)
                {
                    libTessSuccesses++;
                    return (resultVertices, resultIndices);
                }
            }

            // LibTess produced no output - use ear clipping fallback
        }
        catch (Exception)
        {
            // LibTess failed - use ear clipping fallback
        }

        // Fallback: ear-clipping on outer polygon only
        libTessFailures++;
        var fallbackIndices = Triangulation.Triangulate(outerPoly);
        return (outerPoly, fallbackIndices);
    }

    /// <summary>
    /// Simplifies a polygon by removing points that are too close together.
    /// Simple and fast - good enough for most cases.
    /// </summary>
    private static List<Vector2> SimplifyPolygon(List<Vector2> polygon, float tolerance = 0.1f)
    {
        if (polygon.Count < 3) return polygon;

        // For very large polygons, use more aggressive simplification
        if (polygon.Count > 5000)
            tolerance = Math.Max(tolerance, 20.0f);
        else if (polygon.Count > 3000)
            tolerance = Math.Max(tolerance, 10.0f);
        else if (polygon.Count > 2000)
            tolerance = Math.Max(tolerance, 5.0f);
        else if (polygon.Count > 1000)
            tolerance = Math.Max(tolerance, 2.0f);
        else if (polygon.Count > 500)
            tolerance = Math.Max(tolerance, 1.0f);

        float toleranceSq = tolerance * tolerance;
        var simplified = new List<Vector2> { polygon[0] };

        for (int i = 1; i < polygon.Count; i++)
        {
            var last = simplified[^1];
            float dx = polygon[i].X - last.X;
            float dy = polygon[i].Y - last.Y;
            float distSq = dx * dx + dy * dy;
            if (distSq > toleranceSq)
                simplified.Add(polygon[i]);
        }

        // Ensure we have at least 3 points
        if (simplified.Count < 3 && polygon.Count >= 3)
        {
            simplified.Clear();
            simplified.Add(polygon[0]);
            simplified.Add(polygon[polygon.Count / 3]);
            simplified.Add(polygon[polygon.Count * 2 / 3]);
        }

        return simplified;
    }

    /// <summary>
    /// Builds closed rings from a list of ways. Ways that form a closed loop together
    /// are merged into single rings. Disconnected ways form separate rings.
    /// </summary>
    private List<List<Vector2>> BuildClosedRings(List<List<Vector2>> ways, float maxCloseGapOverride = 30.0f)
    {
        var result = new List<List<Vector2>>();
        if (ways.Count == 0)
            return result;

        // Tolerance for merging way endpoints
        const float mergeEps = 3.0f;

        // Maximum gap to auto-close a ring (in meters).
        // Default 30m for small polygons (buildings, lakes).
        // For water/bay/sea relations at the country border (e.g. Gulf of Gdańsk 8259509, Vistula Lagoon 8140878)
        // the outer ring also runs through the Baltic and is CROPPED at the RU/DE border in the Poland PBF.
        // The caller may raise the override for water features (e.g. 100000 = 100km).
        float maxCloseGap = maxCloseGapOverride;

        bool nearlyEqual(Vector2 a, Vector2 b) =>
            Math.Abs(a.X - b.X) < mergeEps && Math.Abs(a.Y - b.Y) < mergeEps;

        float distance(Vector2 a, Vector2 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        var remaining = new List<List<Vector2>>(ways);

        while (remaining.Count > 0)
        {
            // Start a new ring with the first remaining way
            var ring = new List<Vector2>(remaining[0]);
            remaining.RemoveAt(0);

            // Try to extend the ring by finding connecting ways
            bool extended = true;
            while (extended && remaining.Count > 0)
            {
                extended = false;

                for (int i = 0; i < remaining.Count; i++)
                {
                    var way = remaining[i];
                    if (way.Count < 2)
                    {
                        remaining.RemoveAt(i);
                        i--;
                        continue;
                    }

                    var ringFirst = ring[0];
                    var ringLast = ring[ring.Count - 1];
                    var wayFirst = way[0];
                    var wayLast = way[way.Count - 1];

                    if (nearlyEqual(ringLast, wayFirst))
                    {
                        // Append way to ring
                        for (int j = 1; j < way.Count; j++)
                            ring.Add(way[j]);
                        remaining.RemoveAt(i);
                        extended = true;
                        break;
                    }
                    else if (nearlyEqual(ringLast, wayLast))
                    {
                        // Append reversed way to ring
                        for (int j = way.Count - 2; j >= 0; j--)
                            ring.Add(way[j]);
                        remaining.RemoveAt(i);
                        extended = true;
                        break;
                    }
                    else if (nearlyEqual(ringFirst, wayLast))
                    {
                        // Prepend way to ring
                        var newRing = new List<Vector2>(way);
                        newRing.RemoveAt(newRing.Count - 1); // Remove last (duplicate)
                        newRing.AddRange(ring);
                        ring = newRing;
                        remaining.RemoveAt(i);
                        extended = true;
                        break;
                    }
                    else if (nearlyEqual(ringFirst, wayFirst))
                    {
                        // Prepend reversed way to ring
                        var newRing = new List<Vector2>();
                        for (int j = way.Count - 1; j >= 1; j--)
                            newRing.Add(way[j]);
                        newRing.AddRange(ring);
                        ring = newRing;
                        remaining.RemoveAt(i);
                        extended = true;
                        break;
                    }
                }
            }

            if (ring.Count < 3)
                continue;

            // Check if ring is already closed (first point == last point)
            float gap = distance(ring[0], ring[ring.Count - 1]);

            if (gap < mergeEps)
            {
                // Ring is perfectly closed - remove duplicate closing point
                ring.RemoveAt(ring.Count - 1);
                if (ring.Count >= 3)
                    result.Add(ring);
            }
            else if (gap < maxCloseGap)
            {
                // Ring has a small gap but is nearly closed.
                // For ear-clipping triangulation to work correctly, the polygon
                // should NOT have the closing vertex duplicated at the end.
                // The triangulation implicitly closes the polygon from last to first vertex.
                // So just accept this ring as-is (without adding closing point).
                if (ring.Count >= 3)
                    result.Add(ring);
            }
            // If gap >= maxCloseGap, the ring is too broken to use - skip it
            // This prevents stretched triangles from connecting distant points
        }

        return result;
    }

    private void ProcessNode(Node node)
    {
        if (node.Latitude == null || node.Longitude == null)
            return;

        var tags = node.Tags;
        if (tags == null)
            return;

        // Railway POIs (stations, halts, signals) — POIs layer
        bool isStation = tags.ContainsKey("railway") &&
                        (tags["railway"] == "station" || tags["railway"] == "halt");
        bool isSignal = tags.ContainsKey("railway") && tags["railway"] == "signal";

        if (isStation || isSignal)
        {
            var poi = CreatePointMesh(node.Latitude.Value, node.Longitude.Value, tags);
            if (poi != null)
                pois.Add(poi);
            return;
        }

        // Cities, towns, villages — a separate Places layer (for detecting agglomerations,
        // validating station names, and future population density)
        bool isPlace = tags.ContainsKey("place") &&
                       (tags["place"] == "city" || tags["place"] == "town" || tags["place"] == "village");
        if (isPlace)
        {
            var place = CreatePointMesh(node.Latitude.Value, node.Longitude.Value, tags);
            if (place != null)
                places.Add(place);
        }
    }
    
    private MeshGeometry? CreatePointMesh(double lat, double lon, TagsCollectionBase tags)
    {
        var geom = new MeshGeometry
        {
            Vertices = new List<Vector2> { LatLonToMeters(lat, lon) }
        };
        
        // Extract metadata
        ExtractMetadata(geom, tags);
        
        // Compute bounding box
        geom.ComputeBoundingBox();
        
        return geom;
    }
    
    private MeshGeometry? CreatePolygonMesh(List<Vector2> coords, TagsCollectionBase tags)
    {
        if (coords.Count < 3)
            return null;

        // Simplify very large polygons
        const int MAX_POLYGON_VERTICES = 3000;
        if (coords.Count > MAX_POLYGON_VERTICES)
        {
            coords = SimplifyPolygon(coords, tolerance: 5.0f);
            if (coords.Count > MAX_POLYGON_VERTICES)
                coords = SimplifyPolygon(coords, tolerance: 10.0f);
            if (coords.Count < 3)
                return null;
        }

        // Remove duplicate closing vertex if present
        if (coords.Count > 1 && NearlyEqual(coords[0], coords[^1]))
        {
            coords = new List<Vector2>(coords);
            coords.RemoveAt(coords.Count - 1);
        }

        if (coords.Count < 3)
            return null;

        // Triangulate the polygon
        var indices = Triangulation.Triangulate(coords);

        if (indices.Count < 3)
            return null;

        var geom = new MeshGeometry
        {
            Vertices = new List<Vector2>(coords),
            Indices = indices
        };

        ExtractMetadata(geom, tags);
        geom.ComputeBoundingBox();

        return geom;
    }
    
    private static bool NearlyEqual(Vector2 a, Vector2 b)
    {
        const float eps = 1e-6f;
        return Math.Abs(a.X - b.X) < eps && Math.Abs(a.Y - b.Y) < eps;
    }
    
    /// <summary>
    /// Returns the half-width of the highway in meters based on its category.
    /// Returns 0 for paths/footways which should remain as thin lines.
    /// </summary>
    private static float GetHighwayHalfWidth(string? highwayType)
    {
        if (string.IsNullOrEmpty(highwayType))
            return 1.5f; // Default thin line

        return highwayType switch
        {
            // Motorways and expressways - thickest
            "motorway" or "motorway_link" or "trunk" or "trunk_link" => 6.0f,

            // Main roads
            "primary" or "primary_link" => 5.0f,

            // Secondary roads
            "secondary" or "secondary_link" => 4.0f,

            // Local roads
            "tertiary" or "tertiary_link" => 3.0f,

            // Residential roads
            "residential" or "living_street" or "unclassified" or "road" => 2.5f,

            // Service roads
            "service" => 2.0f,

            // Paths - thinnest
            "footway" or "path" or "cycleway" or "pedestrian" or "steps" or "track" or "bridleway" => 1.0f,

            // Default
            _ => 1.5f
        };
    }

    private MeshGeometry? CreateLineMesh(List<Vector2> coords, TagsCollectionBase tags)
    {
        if (coords.Count < 2)
            return null;

        // Get highway type to determine width
        tags.TryGetValue("highway", out var highwayType);
        float halfWidth = GetHighwayHalfWidth(highwayType);

        var vertices = new List<Vector2>();
        var indices = new List<int>();

        // Generate continuous triangle strip - vertices are shared between segments
        // For each point we create 2 vertices (left and right of the line)
        for (int i = 0; i < coords.Count; i++)
        {
            Vector2 normal;

            if (i == 0)
            {
                // First point - use direction to next point
                var dir = coords[1] - coords[0];
                float len = dir.Length();
                if (len < 0.001f) dir = new Vector2(1, 0);
                else dir = dir / len;
                normal = new Vector2(-dir.Y, dir.X);
            }
            else if (i == coords.Count - 1)
            {
                // Last point - use direction from previous point
                var dir = coords[i] - coords[i - 1];
                float len = dir.Length();
                if (len < 0.001f) dir = new Vector2(1, 0);
                else dir = dir / len;
                normal = new Vector2(-dir.Y, dir.X);
            }
            else
            {
                // Middle point - average normals of both segments for smooth join
                var dir1 = coords[i] - coords[i - 1];
                var dir2 = coords[i + 1] - coords[i];
                float len1 = dir1.Length();
                float len2 = dir2.Length();

                if (len1 < 0.001f && len2 < 0.001f)
                {
                    normal = new Vector2(0, 1);
                }
                else if (len1 < 0.001f)
                {
                    dir2 = dir2 / len2;
                    normal = new Vector2(-dir2.Y, dir2.X);
                }
                else if (len2 < 0.001f)
                {
                    dir1 = dir1 / len1;
                    normal = new Vector2(-dir1.Y, dir1.X);
                }
                else
                {
                    dir1 = dir1 / len1;
                    dir2 = dir2 / len2;

                    // Average the two normals
                    var n1 = new Vector2(-dir1.Y, dir1.X);
                    var n2 = new Vector2(-dir2.Y, dir2.X);
                    normal = n1 + n2;
                    float nLen = normal.Length();
                    if (nLen < 0.001f)
                    {
                        normal = n1;
                    }
                    else
                    {
                        normal = normal / nLen;
                        // Adjust width for the miter join
                        float dot = Vector2.Dot(n1, normal);
                        if (dot > 0.1f)
                        {
                            // Scale to maintain consistent width at corners
                            normal = normal / dot;
                        }
                    }
                }
            }

            // Add left and right vertices
            vertices.Add(coords[i] - normal * halfWidth);  // Right
            vertices.Add(coords[i] + normal * halfWidth);  // Left
        }

        // Create triangles connecting consecutive vertex pairs
        for (int i = 0; i < coords.Count - 1; i++)
        {
            int idx = i * 2;

            // Two triangles for the quad between point i and i+1
            // Triangle 1: right[i], left[i], right[i+1]
            indices.Add(idx + 0);
            indices.Add(idx + 1);
            indices.Add(idx + 2);

            // Triangle 2: left[i], left[i+1], right[i+1]
            indices.Add(idx + 1);
            indices.Add(idx + 3);
            indices.Add(idx + 2);
        }

        if (vertices.Count < 3)
            return null;

        var geom = new MeshGeometry
        {
            Vertices = vertices,
            Indices = indices
        };

        if (!geom.ValidateAndFixIndices())
            return null;

        ExtractMetadata(geom, tags);
        geom.ComputeBoundingBox();

        return geom;
    }

    /// <summary>
    /// Returns the half-width of the waterway in meters based on its type.
    /// Rivers are wider, streams/ditches are thinner.
    /// </summary>
    private static float GetWaterwayHalfWidth(string? waterwayType)
    {
        if (string.IsNullOrEmpty(waterwayType))
            return 3.0f;

        return waterwayType switch
        {
            "river" => 8.0f,      // Rivers are wide
            "canal" => 6.0f,      // Canals are moderately wide
            "stream" => 3.0f,     // Streams are narrower
            "brook" => 2.0f,      // Brooks are thin
            "drain" => 2.0f,      // Drains are thin
            "ditch" => 1.5f,      // Ditches are very thin
            _ => 3.0f
        };
    }

    /// <summary>
    /// Creates a waterway mesh (triangle strip) similar to highways but with waterway-specific widths
    /// </summary>
    private MeshGeometry? CreateWaterwayMesh(List<Vector2> coords, TagsCollectionBase tags)
    {
        if (coords.Count < 2)
        {
            Console.WriteLine($"[DEBUG] CreateWaterwayMesh: insufficient coords ({coords.Count})");
            return null;
        }

        // Get waterway type to determine width
        tags.TryGetValue("waterway", out var waterwayType);
        float halfWidth = GetWaterwayHalfWidth(waterwayType);

        string name = tags.ContainsKey("name") ? tags["name"] : "unnamed";
        Console.WriteLine($"[DEBUG] CreateWaterwayMesh: {name}, type={waterwayType}, coords={coords.Count}, halfWidth={halfWidth}");

        var vertices = new List<Vector2>();
        var indices = new List<int>();

        // Generate continuous triangle strip - same algorithm as highways
        for (int i = 0; i < coords.Count; i++)
        {
            Vector2 normal;

            if (i == 0)
            {
                var dir = coords[1] - coords[0];
                float len = dir.Length();
                if (len < 0.001f) dir = new Vector2(1, 0);
                else dir = dir / len;
                normal = new Vector2(-dir.Y, dir.X);
            }
            else if (i == coords.Count - 1)
            {
                var dir = coords[i] - coords[i - 1];
                float len = dir.Length();
                if (len < 0.001f) dir = new Vector2(1, 0);
                else dir = dir / len;
                normal = new Vector2(-dir.Y, dir.X);
            }
            else
            {
                var dir1 = coords[i] - coords[i - 1];
                var dir2 = coords[i + 1] - coords[i];
                float len1 = dir1.Length();
                float len2 = dir2.Length();

                if (len1 < 0.001f && len2 < 0.001f)
                {
                    normal = new Vector2(0, 1);
                }
                else if (len1 < 0.001f)
                {
                    dir2 = dir2 / len2;
                    normal = new Vector2(-dir2.Y, dir2.X);
                }
                else if (len2 < 0.001f)
                {
                    dir1 = dir1 / len1;
                    normal = new Vector2(-dir1.Y, dir1.X);
                }
                else
                {
                    dir1 = dir1 / len1;
                    dir2 = dir2 / len2;

                    var n1 = new Vector2(-dir1.Y, dir1.X);
                    var n2 = new Vector2(-dir2.Y, dir2.X);
                    normal = n1 + n2;
                    float nLen = normal.Length();
                    if (nLen < 0.001f)
                    {
                        normal = n1;
                    }
                    else
                    {
                        normal = normal / nLen;
                        float dot = Vector2.Dot(n1, normal);
                        if (dot > 0.1f)
                        {
                            normal = normal / dot;
                        }
                    }
                }
            }

            vertices.Add(coords[i] - normal * halfWidth);
            vertices.Add(coords[i] + normal * halfWidth);
        }

        for (int i = 0; i < coords.Count - 1; i++)
        {
            int idx = i * 2;
            indices.Add(idx + 0);
            indices.Add(idx + 1);
            indices.Add(idx + 2);
            indices.Add(idx + 1);
            indices.Add(idx + 3);
            indices.Add(idx + 2);
        }

        if (vertices.Count < 3)
            return null;

        var geom = new MeshGeometry
        {
            Vertices = vertices,
            Indices = indices
        };

        if (!geom.ValidateAndFixIndices())
            return null;

        ExtractMetadata(geom, tags);
        geom.ComputeBoundingBox();

        return geom;
    }

    private MeshGeometry? CreateRailwayMesh(Way way, List<Vector2> coords, TagsCollectionBase tags)
    {
        if (coords.Count < 2)
            return null;
        
        var geom = new MeshGeometry
        {
            Vertices = new List<Vector2>(coords)
        };
        
        // Create line segments: 0-1, 1-2, 2-3, ...
        geom.Indices.Clear();
        var segmentIds = new List<int>();
        for (int i = 0; i < coords.Count - 1; i++)
        {
            geom.Indices.Add(i);
            geom.Indices.Add(i + 1);
            segmentIds.Add(nextRailSegmentId++);
        }
        
        // Validate indices (should always be valid for railways, but check anyway)
        if (!geom.ValidateAndFixIndices())
        {
            return null;
        }
        
        // Detect junction vertex indices (nodes shared by 2+ railway ways)
        var junctionIndices = new List<int>();
        for (int i = 0; i < way.Nodes.Length && i < coords.Count; i++)
        {
            var nodeId = way.Nodes[i];
            if (railwayNodeUseCount.TryGetValue(nodeId, out var c) && c > 1)
            {
                junctionIndices.Add(i);
            }
        }
        
        // Extract metadata + add railway graph info
        ExtractMetadata(geom, tags);
        // Propagate railway:line_ref from railway route relations.
        // OSM `ref` on ways is rare; the railway line number (e.g. "9", "204") is on the relation
        // `route=tracks`. The wayId → refs mapping is built in ProcessRailwayRouteRelation.
        if (way.Id.HasValue && _wayIdToLineRefs.TryGetValue(way.Id.Value, out var lineRefs) && lineRefs.Count > 0)
        {
            // Sort numerically ascending (1, 2, 9, 204) → string join ";"
            var sortedRefs = new List<string>(lineRefs);
            sortedRefs.Sort((a, b) =>
            {
                bool aNum = int.TryParse(a, out int ai);
                bool bNum = int.TryParse(b, out int bi);
                if (aNum && bNum) return ai.CompareTo(bi);
                if (aNum) return -1;
                if (bNum) return 1;
                return string.Compare(a, b, StringComparison.Ordinal);
            });
            geom.Metadata["railway:line_ref"] = string.Join(";", sortedRefs);
        }
        // store segment ids and junction indices in binary (MeshGeometry)
        geom.SegmentIds = segmentIds;
        if (junctionIndices.Count > 0)
            geom.JunctionIndices = junctionIndices;
        
        // Compute bounding box
        geom.ComputeBoundingBox();
        
        return geom;
    }
    
    private static void ExtractMetadata(MeshGeometry geom, TagsCollectionBase tags)
    {
        // Extract key OSM tags for rendering
        string[] relevantTags = { "highway", "building", "waterway", "natural", "railway", "name",
                                  "landuse", "military", "public_transport", "amenity", "place",
                                  "bridge", "tunnel", "layer", "population",
                                  "maxspeed", "gauge", "electrified", "voltage", "frequency",
                                  "usage", "service", "tracks", "railway:track_ref",
                                  "boundary", "admin_level", "ISO3166-1", "ISO3166-2",
                                  "name:pl", "name:en", "ref", "int_name" };
        
        foreach (var tag in tags)
        {
            if (relevantTags.Contains(tag.Key) || tag.Key.StartsWith("building:") || 
                tag.Key.StartsWith("surface") || tag.Key.StartsWith("industrial") ||
                tag.Key.Contains("railway"))
            {
                geom.Metadata[tag.Key] = tag.Value;
            }
        }
    }
    
    private Vector2 LatLonToMeters(double lat, double lon)
    {
        if (!centerComputed)
        {
            // Fallback if center not computed
            centerLat = lat;
            centerLon = lon;
        }
        
        // Simple equirectangular projection (good for small areas)
        // Convert to local coordinates in meters
        double latDiff = lat - centerLat;
        double lonDiff = lon - centerLon;
        
        const double earthRadius = 6371000; // meters
        double metersPerDegreeLat = Math.PI * earthRadius / 180.0;
        double metersPerDegreeLon = metersPerDegreeLat * Math.Cos(centerLat * Math.PI / 180.0);
        
        double x = lonDiff * metersPerDegreeLon;
        double y = latDiff * metersPerDegreeLat;
        
        return new Vector2((float)x, (float)y);
    }
    
    // --- Format v7 (multi-LOD) ---

    /// <summary>
    /// Writes map data in v7 format with 6 LOD levels per tile
    /// </summary>
    private void WriteBinaryV7(string outputFile)
    {
        phaseTimer = Stopwatch.StartNew();
        LogInfo("Building tile grid for v7 (multi-LOD)...");

        var globalBounds = BBox.Empty;
        foreach (var geom in highways) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in railways) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in buildings) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in waterFeatures) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in waterways) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in industrialAreas) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in militaryAreas) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in platforms) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in forests) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in pois) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in adminBoundaries) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in places) globalBounds.Expand(geom.BoundingBox);

        var grid = new TileGrid();
        grid.Initialize(globalBounds);

        LogInfo("Distributing features to tiles...");
        int totalFeatures = 0;
        foreach (var geom in highways) { grid.AddFeature(BinaryFormat.LayerType.Highways, geom); totalFeatures++; }
        foreach (var geom in railways) { grid.AddFeature(BinaryFormat.LayerType.Railways, geom); totalFeatures++; }
        foreach (var geom in buildings) { grid.AddFeature(BinaryFormat.LayerType.Buildings, geom); totalFeatures++; }
        foreach (var geom in waterFeatures) { grid.AddFeature(BinaryFormat.LayerType.Water, geom); totalFeatures++; }
        foreach (var geom in waterways) { grid.AddFeature(BinaryFormat.LayerType.Waterways, geom); totalFeatures++; }
        foreach (var geom in industrialAreas) { grid.AddFeature(BinaryFormat.LayerType.Industrial, geom); totalFeatures++; }
        foreach (var geom in militaryAreas) { grid.AddFeature(BinaryFormat.LayerType.Military, geom); totalFeatures++; }
        foreach (var geom in platforms) { grid.AddFeature(BinaryFormat.LayerType.Platforms, geom); totalFeatures++; }
        foreach (var geom in forests) { grid.AddFeature(BinaryFormat.LayerType.Forests, geom); totalFeatures++; }
        foreach (var geom in pois) { grid.AddFeature(BinaryFormat.LayerType.POIs, geom); totalFeatures++; }
        foreach (var geom in adminBoundaries) { grid.AddFeature(BinaryFormat.LayerType.AdminBoundaries, geom); totalFeatures++; }
        foreach (var geom in places) { grid.AddFeature(BinaryFormat.LayerType.Places, geom); totalFeatures++; }
        foreach (var geom in coastlines) { grid.AddFeature(BinaryFormat.LayerType.Coastlines, geom); totalFeatures++; }

        LogInfo($"Distributed {totalFeatures:N0} features to {grid.Tiles.Count:N0} tiles");
        grid.PrintStatistics();

        LogInfo("Writing v7 format (6 LOD levels per tile)...");
        using var fileStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1024 * 1024, useAsync: false);
        using var writer = new BinaryWriter(fileStream);

        long headerPos = fileStream.Position;
        BinaryFormat.WriteHeaderV7(writer, globalBounds, grid.TilesX, grid.TilesY, grid.Tiles.Count, 0);

        var allTiles = grid.Tiles.Values.OrderBy(t => t.TileID).ToList();
        var indexEntries = new List<BinaryFormat.TileIndexEntryV7>(allTiles.Count);

        int tileIdx = 0;
        foreach (var tile in allTiles)
        {
            tileIdx++;
            if (tileIdx % 50 == 0 || tileIdx == allTiles.Count)
                LogProgress("WRITE TILES", tileIdx, allTiles.Count);

            var entry = new BinaryFormat.TileIndexEntryV7
            {
                TileID = tile.TileID,
                GridX = tile.GridX,
                GridY = tile.GridY,
                Bounds = tile.Bounds,
                LODs = new BinaryFormat.LODInfo[BinaryFormat.LODCount],
                FeatureCounts = new int[BinaryFormat.LayerCount]
            };

            // Write 6 LOD blocks for this tile
            for (int lod = 0; lod < BinaryFormat.LODCount; lod++)
            {
                var lodFeatures = lod switch
                {
                    0 => tile.Features,                          // LOD0 (<1000): everything
                    1 => LodFilter.CreateLODLevel1(tile.Features),         // LOD1 (1000-2000): residential+, all buildings
                    2 => LodFilter.CreateLODLevel2(tile.Features),         // LOD2 (2000-4000): residential+, big buildings
                    3 => LodFilter.CreateLODLevel3(tile.Features),         // LOD3 (4000-8000): motorway-tertiary, no buildings
                    4 => LodFilter.CreateLODLevel4(tile.Features),         // LOD4 (8000-16000): motorway/trunk/primary
                    5 => LodFilter.CreateLODLevel5(tile.Features),         // LOD5 (>16000): no roads
                    _ => tile.Features
                };

                long lodOffset = fileStream.Position;
                byte[] tileBody;
                int layerMask = 0;

                using (var ms = new MemoryStream())
                using (var tileWriter = new BinaryWriter(ms))
                {
                    foreach (var (layerType, features) in lodFeatures)
                    {
                        if (features.Count == 0)
                            continue;

                        layerMask |= (1 << (int)layerType);
                        BinaryFormat.WriteLayerHeader(tileWriter, layerType, features.Count);

                        foreach (var geom in features)
                        {
                            tileWriter.Write(geom.BoundingBox.MinX);
                            tileWriter.Write(geom.BoundingBox.MinY);
                            tileWriter.Write(geom.BoundingBox.MaxX);
                            tileWriter.Write(geom.BoundingBox.MaxY);
                            geom.WriteBody(tileWriter);
                        }
                    }
                    tileBody = ms.ToArray();
                }

                var compressed = LZ4Pickler.Pickle(tileBody, LZ4Level.L00_FAST);

                writer.Write(compressed.Length);
                writer.Write(compressed);

                entry.LODs[lod] = new BinaryFormat.LODInfo
                {
                    FileOffset = lodOffset,
                    CompressedSize = compressed.Length,
                    UncompressedSize = tileBody.Length,
                    LayerMask = layerMask
                };

                // Save LOD0 feature counts for stats
                if (lod == 0)
                {
                    foreach (BinaryFormat.LayerType lt in Enum.GetValues(typeof(BinaryFormat.LayerType)))
                    {
                        entry.FeatureCounts[(int)lt] = lodFeatures.TryGetValue(lt, out var f) ? f.Count : 0;
                    }
                }
            }

            indexEntries.Add(entry);
        }

        LogProgress("WRITE TILES", allTiles.Count, allTiles.Count);

        // Write index table
        long indexTableOffset = fileStream.Position;
        LogInfo($"Writing tile index table at offset {indexTableOffset:N0}...");
        foreach (var entry in indexEntries)
        {
            BinaryFormat.WriteTileIndexEntryV7(writer, entry);
        }

        // Update header
        fileStream.Seek(headerPos, SeekOrigin.Begin);
        BinaryFormat.WriteHeaderV7(writer, globalBounds, grid.TilesX, grid.TilesY, allTiles.Count, indexTableOffset);
        fileStream.Seek(0, SeekOrigin.End);

        int nonEmptyTiles = allTiles.Count(t => t.Features.Values.Any(list => list.Count > 0));

        LogInfo("v7 format write complete!");
        LogInfo($"  File size: {fileStream.Length / (1024.0 * 1024.0):F2} MB");
        LogInfo($"  Total tiles: {allTiles.Count:N0}");
        LogInfo($"  Non-empty tiles: {nonEmptyTiles:N0}");
    }

    /// <summary>
    /// Writes v8 (FORMAP04): the same tiled multi-LOD data as v7, losslessly re-encoded
    /// (uint16 indices + per-feature int32 fallback, presence bitfield + varint counts, one global
    /// metadata string table, LZ4-HC). bbox kept; coordinate precision untouched. Grid construction
    /// mirrors WriteBinaryV7 — to be de-duplicated when OsmConverter is split (see docs/format-v8.md).
    /// </summary>
    private void WriteBinaryV8(string outputFile)
    {
        phaseTimer = Stopwatch.StartNew();
        LogInfo("Building tile grid for v8 (multi-LOD)...");

        var globalBounds = BBox.Empty;
        foreach (var geom in highways) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in railways) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in buildings) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in waterFeatures) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in waterways) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in industrialAreas) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in militaryAreas) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in platforms) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in forests) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in pois) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in adminBoundaries) globalBounds.Expand(geom.BoundingBox);
        foreach (var geom in places) globalBounds.Expand(geom.BoundingBox);

        var grid = new TileGrid();
        grid.Initialize(globalBounds);

        LogInfo("Distributing features to tiles...");
        foreach (var geom in highways) grid.AddFeature(BinaryFormat.LayerType.Highways, geom);
        foreach (var geom in railways) grid.AddFeature(BinaryFormat.LayerType.Railways, geom);
        foreach (var geom in buildings) grid.AddFeature(BinaryFormat.LayerType.Buildings, geom);
        foreach (var geom in waterFeatures) grid.AddFeature(BinaryFormat.LayerType.Water, geom);
        foreach (var geom in waterways) grid.AddFeature(BinaryFormat.LayerType.Waterways, geom);
        foreach (var geom in industrialAreas) grid.AddFeature(BinaryFormat.LayerType.Industrial, geom);
        foreach (var geom in militaryAreas) grid.AddFeature(BinaryFormat.LayerType.Military, geom);
        foreach (var geom in platforms) grid.AddFeature(BinaryFormat.LayerType.Platforms, geom);
        foreach (var geom in forests) grid.AddFeature(BinaryFormat.LayerType.Forests, geom);
        foreach (var geom in pois) grid.AddFeature(BinaryFormat.LayerType.POIs, geom);
        foreach (var geom in adminBoundaries) grid.AddFeature(BinaryFormat.LayerType.AdminBoundaries, geom);
        foreach (var geom in places) grid.AddFeature(BinaryFormat.LayerType.Places, geom);
        foreach (var geom in coastlines) grid.AddFeature(BinaryFormat.LayerType.Coastlines, geom);

        grid.PrintStatistics();

        LogInfo("Writing v8 format (FORMAP04, LZ4-HC)...");
        var tiles = grid.Tiles.Values.OrderBy(t => t.TileID).ToList();

        Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> LodFilter(TileGrid.TileInfo tile, int lod) => lod switch
        {
            0 => tile.Features,
            1 => formap.LodFilter.CreateLODLevel1(tile.Features),
            2 => formap.LodFilter.CreateLODLevel2(tile.Features),
            3 => formap.LodFilter.CreateLODLevel3(tile.Features),
            4 => formap.LodFilter.CreateLODLevel4(tile.Features),
            5 => formap.LodFilter.CreateLODLevel5(tile.Features),
            _ => tile.Features
        };

        long fileBytes;
        // ReadWrite: signing reads the index region back to hash it (Pillar 2); the v8 writer already seeks to
        // rewrite the header. Harmless for the unsigned path.
        using (var fileStream = new FileStream(outputFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 1024 * 1024, useAsync: false))
        {
            BinaryFormatV8.WriteV8(fileStream, globalBounds, grid.TilesX, grid.TilesY,
                BinaryFormat.LayerCount, BinaryFormat.LODCount, tiles, LodFilter, CompressionTypeV8, SigningPrivateKeyV8);
            fileBytes = fileStream.Length;
        }

        LogInfo("v8 format write complete!");
        LogInfo($"  File size: {fileBytes / (1024.0 * 1024.0):F2} MB");
        LogInfo($"  Total tiles: {tiles.Count:N0}");

        if (VerifyAfterWriteV8)
        {
            LogInfo("Verifying v8 round-trip (same-run, bit-exact incl. segmentIds)...");
            using var rfs = File.OpenRead(outputFile);
            var decoded = BinaryFormatV8.ReadV8(rfs);
            long compared = 0, fails = 0;
            for (int i = 0; i < tiles.Count && i < decoded.Count; i++)
                for (int lod = 0; lod < BinaryFormat.LODCount; lod++)
                {
                    var exp = LodFilter(tiles[i], lod);
                    var got = decoded[i].Lods[lod];
                    foreach (var (lt, list) in exp)
                    {
                        if (list.Count == 0) continue;
                        if (!got.TryGetValue(lt, out var glist) || glist.Count != list.Count) { fails++; continue; }
                        for (int f = 0; f < list.Count; f++)
                        {
                            var d = FeatureCodecV8.Compare(list[f], glist[f]);
                            compared++;
                            if (d != null) { fails++; if (fails <= 8) LogError($"  v8 mismatch tile{i} lod{lod} {lt} feat{f}: {d}"); }
                        }
                    }
                }
            LogInfo(fails == 0
                ? $"  v8 round-trip BIT-EXACT: {compared:N0} features compared, 0 mismatches"
                : $"  v8 round-trip FAILED: {fails:N0} mismatches");
        }
    }

}
