using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;
using ZstdSharp;

namespace formap;

/// <summary>
/// v8 container (FORMAP04) writer + reader. Lossless re-encoding of the v7 data; bbox derived from vertices on
/// read (not stored), vertex precision untouched. Header carries LayerCount/LODCount; one global metadata string table (Change 4); per-feature
/// structures via <see cref="FeatureCodecV8"/> (Changes 2 + 5); LZ4-HC blocks (Change 1); block-level vertex
/// pool, SoA-split + byte-shuffled (Change 3).
///
/// File layout:
///   [Header 128 B]            (BinaryFormat.WriteHeaderV8)
///   [String table]            varint count, then per string: varint byteLen + UTF-8 bytes
///   [Tile LOD blocks]         per tile, per LOD: LZ4-HC pickle of the block (located via the index).
///   [Tile index @ indexOffset]
///   [Signature]               OPTIONAL, only if header signatureLength == 64: trailing 64-byte Ed25519
///                             signature over the entire index byte region (Pillar 2).
///
/// v8 index entry (per tile): TileID(int64) GridX(int32) GridY(int32) Bounds(4×float32),
///   then LODCount × LODInfo (FileOffset int64, CompressedSize int32, UncompressedSize int32, LayerMask int32),
///   then LODCount × 32-byte SHA-256 of each LOD block's compressed on-disk bytes (Pillar 2 — always written;
///   empty LOD = 32 zero bytes), then LayerCount × int32 feature counts.
///
/// Block body (before LZ4):
///   varint structLen, then [struct section] then [shuffled X plane] then [shuffled Y plane]
///   struct section: per present layer: int32 layerType, int32 featureCount, then per feature a
///                   FeatureCodecV8 structure record (everything EXCEPT vertex coords).
///   X/Y planes: all vertices of the block in feature order, X coords and Y coords each as a contiguous
///               float32 array, byte-shuffled (stride 4). Reader sums the structure vertexCounts to size them.
/// </summary>
public static class BinaryFormatV8
{
    public const LZ4Level CompressionLevel = LZ4Level.L09_HC;
    public const int CompLZ4HC = 0;
    public const int CompZstd = 1;
    public const int ZstdLevel = 19;

    // --- Block codec (Change 3: pooled, SoA, byte-shuffled vertices) ---
    public static byte[] EncodeBlock(Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> lodFeatures,
        Func<string, int> intern, out int layerMask)
    {
        layerMask = 0;
        var structMs = new MemoryStream();
        var sw = new BinaryWriter(structMs, Encoding.UTF8, leaveOpen: true);
        var xMs = new MemoryStream();
        var yMs = new MemoryStream();
        var xw = new BinaryWriter(xMs, Encoding.UTF8, leaveOpen: true);
        var yw = new BinaryWriter(yMs, Encoding.UTF8, leaveOpen: true);

        foreach (var (layerType, features) in lodFeatures)
        {
            if (features.Count == 0) continue;
            layerMask |= 1 << (int)layerType;
            sw.Write((int)layerType);
            sw.Write(features.Count);
            foreach (var g in features)
            {
                FeatureCodecV8.WriteFeatureStructure(sw, g, intern);
                foreach (var v in g.Vertices) { xw.Write(v.X); yw.Write(v.Y); }
            }
        }
        sw.Flush(); xw.Flush(); yw.Flush();

        byte[] structBytes = structMs.ToArray();
        if (structBytes.Length == 0) return System.Array.Empty<byte>(); // empty LOD

        byte[] xShuf = FeatureCodecV8.Shuffle(xMs.ToArray(), 4);
        byte[] yShuf = FeatureCodecV8.Shuffle(yMs.ToArray(), 4);

        var blockMs = new MemoryStream(structBytes.Length + xShuf.Length + yShuf.Length + 8);
        var bw = new BinaryWriter(blockMs, Encoding.UTF8, leaveOpen: true);
        FeatureCodecV8.WriteVarint(bw, (uint)structBytes.Length);
        bw.Write(structBytes);
        bw.Write(xShuf);
        bw.Write(yShuf);
        bw.Flush();
        return blockMs.ToArray();
    }

    public static Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> DecodeBlock(byte[] body, IReadOnlyList<string> table)
    {
        var dict = new Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>();
        var order = new List<(MeshGeometry g, int vc)>();

        using var ms = new MemoryStream(body, false);
        var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        int structLen = (int)FeatureCodecV8.ReadVarint(br);
        long structEnd = ms.Position + structLen;
        while (ms.Position < structEnd)
        {
            int layerType = br.ReadInt32();
            int featCount = br.ReadInt32();
            var list = new List<MeshGeometry>(featCount);
            dict[(BinaryFormat.LayerType)layerType] = list;
            for (int f = 0; f < featCount; f++)
            {
                var g = FeatureCodecV8.ReadFeatureStructure(br, table, out int vc);
                list.Add(g);
                order.Add((g, vc));
            }
        }

        long totalVerts = 0;
        foreach (var (_, vc) in order) totalVerts += vc;

        byte[] xB = FeatureCodecV8.Unshuffle(br.ReadBytes((int)totalVerts * 4), 4);
        byte[] yB = FeatureCodecV8.Unshuffle(br.ReadBytes((int)totalVerts * 4), 4);

        int vi = 0;
        foreach (var (g, vc) in order)
        {
            for (int k = 0; k < vc; k++)
            {
                int o = (vi + k) * 4;
                g.Vertices.Add(new Vector2(BitConverter.ToSingle(xB, o), BitConverter.ToSingle(yB, o)));
            }
            vi += vc;
            g.ComputeBoundingBox(); // bbox not stored in v8 — derived from vertices (bit-exact)
        }
        return dict;
    }

    /// <summary>Like <see cref="DecodeBlock"/> but materializes ONLY the layers in <paramref name="wanted"/>.
    /// Features of other layers are parsed just far enough to advance the reader and learn their vertexCount
    /// (the block vertex pool is byte-shuffled as a whole, so the whole pool is still un-shuffled), but NO
    /// MeshGeometry / index-list / metadata / Vector2 objects are built for them — their vertices are skipped
    /// in the pool. Output for the wanted layers is bit-identical to <see cref="DecodeBlock"/>; this just avoids
    /// materializing the heavy non-logic layers (Buildings/Forests/Highways/Water). Used by
    /// <see cref="ReadLogicLayersV8"/>.</summary>
    public static Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> DecodeBlockFiltered(
        byte[] body, IReadOnlyList<string> table, HashSet<BinaryFormat.LayerType> wanted)
    {
        var dict = new Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>();
        var order = new List<(MeshGeometry? g, int vc)>();

        using var ms = new MemoryStream(body, false);
        var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        int structLen = (int)FeatureCodecV8.ReadVarint(br);
        long structEnd = ms.Position + structLen;
        while (ms.Position < structEnd)
        {
            int layerType = br.ReadInt32();
            int featCount = br.ReadInt32();
            if (wanted.Contains((BinaryFormat.LayerType)layerType))
            {
                var list = new List<MeshGeometry>(featCount);
                dict[(BinaryFormat.LayerType)layerType] = list;
                for (int f = 0; f < featCount; f++)
                {
                    var g = FeatureCodecV8.ReadFeatureStructure(br, table, out int vc);
                    list.Add(g);
                    order.Add((g, vc));
                }
            }
            else
            {
                // Non-wanted layer: skip the structure (no allocation) but keep its vertexCounts so the pool
                // slices stay aligned with the wanted features that follow.
                for (int f = 0; f < featCount; f++)
                    order.Add((null, FeatureCodecV8.SkipFeatureStructure(br)));
            }
        }

        // No wanted layer present in this block → the vertex pool is irrelevant; skip un-shuffling it entirely.
        if (dict.Count == 0) return dict;

        long totalVerts = 0;
        foreach (var (_, vc) in order) totalVerts += vc;

        byte[] xB = FeatureCodecV8.Unshuffle(br.ReadBytes((int)totalVerts * 4), 4);
        byte[] yB = FeatureCodecV8.Unshuffle(br.ReadBytes((int)totalVerts * 4), 4);

        int vi = 0;
        foreach (var (g, vc) in order)
        {
            if (g != null)
            {
                for (int k = 0; k < vc; k++)
                {
                    int o = (vi + k) * 4;
                    g.Vertices.Add(new Vector2(BitConverter.ToSingle(xB, o), BitConverter.ToSingle(yB, o)));
                }
                g.ComputeBoundingBox(); // bbox not stored in v8 — derived from vertices (bit-exact)
            }
            vi += vc; // advance past non-wanted features' vertices too (their slices are not materialized)
        }
        return dict;
    }

    public static void WriteV8(Stream output, BBox globalBounds, int tilesX, int tilesY,
        int layerCount, int lodCount,
        IReadOnlyList<TileGrid.TileInfo> tiles,
        Func<TileGrid.TileInfo, int, Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>> lodFilter,
        int compressionType = CompLZ4HC,
        byte[]? signingPrivateKey = null)
    {
        var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        using var zstd = compressionType == CompZstd ? new Compressor(ZstdLevel) : null;
        int signatureLength = signingPrivateKey != null ? Signing.SignatureSize : 0;

        // Pass 1 — global string table (tile.Features = LOD0 superset → contains every string).
        var table = new List<string>();
        var map = new Dictionary<string, int>();
        int Intern(string s) { if (!map.TryGetValue(s, out var i)) { i = table.Count; table.Add(s); map[s] = i; } return i; }
        foreach (var tile in tiles)
            foreach (var kv in tile.Features)
                foreach (var g in kv.Value)
                    foreach (var m in g.Metadata) { Intern(m.Key); Intern(m.Value); }

        long headerPos = output.Position;
        BinaryFormat.WriteHeaderV8(writer, globalBounds, tilesX, tilesY, tiles.Count, 0, layerCount, lodCount, compressionType, signatureLength);

        FeatureCodecV8.WriteVarint(writer, (uint)table.Count);
        foreach (var s in table)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            FeatureCodecV8.WriteVarint(writer, (uint)bytes.Length);
            writer.Write(bytes);
        }

        var zeroHash = new byte[32]; // empty LOD → 32 zero bytes (Pillar 2)
        var index = new List<(TileGrid.TileInfo tile, BinaryFormat.LODInfo[] lods, byte[][] hashes, int[] featureCounts)>(tiles.Count);
        foreach (var tile in tiles)
        {
            var lods = new BinaryFormat.LODInfo[lodCount];
            var hashes = new byte[lodCount][];
            var featureCounts = new int[layerCount];
            for (int lod = 0; lod < lodCount; lod++)
            {
                var lodFeatures = lodFilter(tile, lod);
                byte[] body = EncodeBlock(lodFeatures, Intern, out int layerMask);
                if (lod == 0)
                    foreach (var (lt, fs) in lodFeatures)
                        if ((int)lt < layerCount) featureCounts[(int)lt] = fs.Count;

                long fileOffset = output.Position;
                int compressedSize = 0;
                byte[] hash = zeroHash;
                if (body.Length > 0)
                {
                    var pickle = compressionType == CompZstd ? zstd!.Wrap(body).ToArray() : LZ4Pickler.Pickle(body, CompressionLevel);
                    writer.Write(pickle);
                    compressedSize = pickle.Length;
                    hash = SHA256.HashData(pickle); // SHA-256 of the compressed on-disk block (Pillar 2)
                }
                hashes[lod] = hash;
                lods[lod] = new BinaryFormat.LODInfo { FileOffset = fileOffset, CompressedSize = compressedSize, UncompressedSize = body.Length, LayerMask = layerMask };
            }
            index.Add((tile, lods, hashes, featureCounts));
        }

        long indexOffset = output.Position;
        foreach (var (tile, lods, hashes, featureCounts) in index)
        {
            writer.Write(tile.TileID);
            writer.Write(tile.GridX);
            writer.Write(tile.GridY);
            writer.Write(tile.Bounds.MinX); writer.Write(tile.Bounds.MinY);
            writer.Write(tile.Bounds.MaxX); writer.Write(tile.Bounds.MaxY);
            for (int lod = 0; lod < lodCount; lod++)
            {
                writer.Write(lods[lod].FileOffset);
                writer.Write(lods[lod].CompressedSize);
                writer.Write(lods[lod].UncompressedSize);
                writer.Write(lods[lod].LayerMask);
            }
            for (int lod = 0; lod < lodCount; lod++) writer.Write(hashes[lod]); // 6 × 32-byte per-LOD SHA-256 (Pillar 2)
            for (int k = 0; k < layerCount; k++) writer.Write(featureCounts[k]);
        }

        long indexEnd = output.Position;
        writer.Flush();
        writer.Seek(0, SeekOrigin.Begin);
        BinaryFormat.WriteHeaderV8(writer, globalBounds, tilesX, tilesY, tiles.Count, indexOffset, layerCount, lodCount, compressionType, signatureLength);
        writer.Flush();
        writer.Seek(0, SeekOrigin.End);

        // Pillar 2: Ed25519-sign the entire index byte region and append the 64-byte signature at end of file.
        if (signingPrivateKey != null)
        {
            output.Position = indexOffset;
            var indexBytes = new byte[indexEnd - indexOffset];
            int read = 0;
            while (read < indexBytes.Length)
            {
                int n = output.Read(indexBytes, read, indexBytes.Length - read);
                if (n <= 0) throw new EndOfStreamException("Failed to read back index region for signing");
                read += n;
            }
            byte[] sig = Signing.Sign(signingPrivateKey, indexBytes);
            output.Position = output.Length;
            writer.Write(sig);
            writer.Flush();
        }
    }

    public sealed class DecodedTileV8
    {
        public int GridX;
        public int GridY;
        public BBox Bounds;
        public Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>[] Lods =
            System.Array.Empty<Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>>();
    }

    public static List<DecodedTileV8> ReadV8(Stream input)
    {
        var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        input.Position = 0;

        string magic = Encoding.ASCII.GetString(reader.ReadBytes(8));
        if (magic != BinaryFormat.MagicV8)
            throw new InvalidDataException($"Expected {BinaryFormat.MagicV8}, got '{magic}'");
        BinaryFormat.ReadHeaderV8(reader, out _, out _, out _, out _, out int totalTiles,
            out long indexOffset, out int layerCount, out int lodCount, out int compressionType, out _);
        using var zstd = compressionType == CompZstd ? new Decompressor() : null;

        int strCount = (int)FeatureCodecV8.ReadVarint(reader);
        var table = new string[strCount];
        for (int i = 0; i < strCount; i++)
        {
            int len = (int)FeatureCodecV8.ReadVarint(reader);
            table[i] = Encoding.UTF8.GetString(reader.ReadBytes(len));
        }

        input.Position = indexOffset;
        var entries = new List<(int gx, int gy, BBox bounds, BinaryFormat.LODInfo[] lods)>(totalTiles);
        for (int t = 0; t < totalTiles; t++)
        {
            reader.ReadInt64();
            int gx = reader.ReadInt32(), gy = reader.ReadInt32();
            var bounds = new BBox { MinX = reader.ReadSingle(), MinY = reader.ReadSingle(), MaxX = reader.ReadSingle(), MaxY = reader.ReadSingle() };
            var lods = new BinaryFormat.LODInfo[lodCount];
            for (int lod = 0; lod < lodCount; lod++)
                lods[lod] = new BinaryFormat.LODInfo
                {
                    FileOffset = reader.ReadInt64(),
                    CompressedSize = reader.ReadInt32(),
                    UncompressedSize = reader.ReadInt32(),
                    LayerMask = reader.ReadInt32()
                };
            for (int lod = 0; lod < lodCount; lod++) reader.ReadBytes(32); // per-LOD SHA-256 hashes (Pillar 2 — kept for verify, ignored here)
            for (int k = 0; k < layerCount; k++) reader.ReadInt32();
            entries.Add((gx, gy, bounds, lods));
        }

        var result = new List<DecodedTileV8>(totalTiles);
        foreach (var e in entries)
        {
            var dt = new DecodedTileV8
            {
                GridX = e.gx,
                GridY = e.gy,
                Bounds = e.bounds,
                Lods = new Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>[lodCount]
            };
            for (int lod = 0; lod < lodCount; lod++)
            {
                var li = e.lods[lod];
                if (li.CompressedSize <= 0) { dt.Lods[lod] = new Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>(); continue; }
                input.Position = li.FileOffset;
                var comp = reader.ReadBytes(li.CompressedSize);
                byte[] body = compressionType == CompZstd ? zstd!.Unwrap(comp).ToArray() : LZ4Pickler.Unpickle(comp);
                dt.Lods[lod] = DecodeBlock(body, table);
            }
            result.Add(dt);
        }
        return result;
    }

    /// <summary>Reads ONLY the requested layers' LOD0 features from a v8 file, accumulated across all tiles.
    /// Memory-bounded: decodes one tile's LOD0 block at a time and keeps only the wanted layers (used by
    /// InitStateBuilder, which needs Railways/AdminBoundaries/Places/POIs/Platforms/Coastlines at LOD0).</summary>
    public static Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> ReadLogicLayersV8(
        Stream input, HashSet<BinaryFormat.LayerType> wanted)
    {
        var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        input.Position = 0;
        string magic = Encoding.ASCII.GetString(reader.ReadBytes(8));
        if (magic != BinaryFormat.MagicV8)
            throw new InvalidDataException($"Expected {BinaryFormat.MagicV8}, got '{magic}'");
        BinaryFormat.ReadHeaderV8(reader, out _, out _, out _, out _, out int totalTiles,
            out long indexOffset, out int layerCount, out int lodCount, out int compressionType, out _);
        using var zstd = compressionType == CompZstd ? new Decompressor() : null;

        int strCount = (int)FeatureCodecV8.ReadVarint(reader);
        var table = new string[strCount];
        for (int i = 0; i < strCount; i++)
        {
            int len = (int)FeatureCodecV8.ReadVarint(reader);
            table[i] = Encoding.UTF8.GetString(reader.ReadBytes(len));
        }

        // Bitmask of the wanted layers — lets us skip whole blocks (no decompress, no decode) when a tile's
        // LOD0 contains none of them (e.g. a rural tile with only Buildings/Forests/Water/Highways).
        int wantedMask = 0;
        foreach (var lt in wanted) wantedMask |= 1 << (int)lt;

        // Index — keep only each tile's LOD0 offset/size + LayerMask.
        input.Position = indexOffset;
        var lod0 = new List<(long off, int csz, int mask)>(totalTiles);
        for (int t = 0; t < totalTiles; t++)
        {
            reader.ReadInt64(); reader.ReadInt32(); reader.ReadInt32();
            reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle();
            long off0 = 0; int csz0 = 0, mask0 = 0;
            for (int lod = 0; lod < lodCount; lod++)
            {
                long fo = reader.ReadInt64(); int cs = reader.ReadInt32(); reader.ReadInt32(); int lm = reader.ReadInt32();
                if (lod == 0) { off0 = fo; csz0 = cs; mask0 = lm; }
            }
            for (int lod = 0; lod < lodCount; lod++) reader.ReadBytes(32); // per-LOD SHA-256 hashes (Pillar 2 — skipped here)
            for (int k = 0; k < layerCount; k++) reader.ReadInt32();
            lod0.Add((off0, csz0, mask0));
        }

        var acc = new Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>();
        foreach (var lt in wanted) acc[lt] = new List<MeshGeometry>();

        foreach (var (off, csz, mask) in lod0)
        {
            if (csz <= 0 || (mask & wantedMask) == 0) continue; // empty LOD0, or no wanted layer in this tile
            input.Position = off;
            var comp = reader.ReadBytes(csz);
            byte[] body = compressionType == CompZstd ? zstd!.Unwrap(comp).ToArray() : LZ4Pickler.Unpickle(comp);
            // Filtered decode: materializes ONLY the wanted layers; non-wanted features (Buildings/Forests/…)
            // are skipped in the pool without building objects (bit-identical output for the wanted layers).
            var dict = DecodeBlockFiltered(body, table, wanted);
            foreach (var lt in wanted)
                if (dict.TryGetValue(lt, out var list) && list.Count > 0)
                    acc[lt].AddRange(list);
        }
        return acc;
    }

    /// <summary>Signs an already-written v8 file IN PLACE (no re-build): Ed25519 over the index byte region
    /// (identical region to WriteV8/VerifySignatureV8), sets the header signatureLength, and appends the 64-byte
    /// signature. Lets you build the (expensive) map unsigned and sign it cheaply later with your key.
    /// Re-signing an already-signed file overwrites the trailing signature.</summary>
    public static void SignExistingV8(string path, byte[] privateKey)
    {
        BBox bounds; long indexOffset; int tilesX, tilesY, totalTiles, layerCount, lodCount, compressionType, signatureLength;
        using (var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
        {
            string magic = Encoding.ASCII.GetString(r.ReadBytes(8));
            if (magic != BinaryFormat.MagicV8) throw new InvalidDataException($"Expected {BinaryFormat.MagicV8}, got '{magic}'");
            BinaryFormat.ReadHeaderV8(r, out _, out bounds, out tilesX, out tilesY, out totalTiles,
                out indexOffset, out layerCount, out lodCount, out compressionType, out signatureLength);
        }

        long fileLen = new FileInfo(path).Length;
        long indexEnd = signatureLength > 0 ? fileLen - signatureLength : fileLen; // exclude any existing trailing sig
        if (indexEnd < indexOffset) throw new InvalidDataException($"Corrupt v8 file: indexEnd {indexEnd} < indexOffset {indexOffset}");

        byte[] indexBytes = new byte[indexEnd - indexOffset];
        using (var fs = File.OpenRead(path)) { fs.Position = indexOffset; fs.ReadExactly(indexBytes); }

        byte[] sig = Signing.Sign(privateKey, indexBytes);

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
            fs.Position = 0; // rewrite header with signatureLength set (avoids a hard-coded field offset)
            BinaryFormat.WriteHeaderV8(w, bounds, tilesX, tilesY, totalTiles, indexOffset, layerCount, lodCount, compressionType, Signing.SignatureSize);
            w.Flush();
            fs.Position = indexEnd; // append (was unsigned) or overwrite the existing trailing signature (re-sign)
            fs.Write(sig, 0, sig.Length);
            fs.Flush();
        }
        Console.WriteLine($"[SIGN-EXISTING] {path}: signed index region {indexBytes.Length:N0} B → {sig.Length}-byte Ed25519 signature at offset {indexEnd:N0}.");
    }

    /// <summary>SHA-256 over the v8 tile-index byte region [indexOffset, indexEnd) — the SAME bytes the Ed25519
    /// signature covers, so it transitively fingerprints every block (each block's SHA-256 lives in the index).
    /// Stable across copy / deploy / mtime change, and signing-independent (works on unsigned files too —
    /// indexEnd = fileLen when signatureLength == 0). Used to gate init-state freshness by content, not timestamp.
    /// Both formap (at build) and the game (at the freshness check) must hash this exact region.</summary>
    public static byte[] ComputeMapIndexHash(string path)
    {
        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
        fs.Position = 0;
        string magic = Encoding.ASCII.GetString(reader.ReadBytes(8));
        if (magic != BinaryFormat.MagicV8) throw new InvalidDataException($"Expected {BinaryFormat.MagicV8}, got '{magic}'");
        BinaryFormat.ReadHeaderV8(reader, out _, out _, out _, out _, out _,
            out long indexOffset, out _, out _, out _, out int signatureLength);
        long fileLen = fs.Length;
        long indexEnd = signatureLength > 0 ? fileLen - signatureLength : fileLen;
        if (indexEnd < indexOffset) throw new InvalidDataException($"Corrupt v8 file: indexEnd {indexEnd} < indexOffset {indexOffset}");
        fs.Position = indexOffset;
        byte[] indexBytes = new byte[indexEnd - indexOffset];
        fs.ReadExactly(indexBytes);
        return SHA256.HashData(indexBytes);
    }

    /// <summary>Verifies a signed v8 file (Pillar 2): (1) re-reads the entire index byte region and Ed25519-verifies
    /// it against the trailing signature, then (2) for each tile/LOD with CompressedSize&gt;0, SHA-256s the compressed
    /// on-disk block and compares to the per-LOD hash stored in the index. Prints SIGNATURE OK/FAIL, the block-hash
    /// summary, and a final [VERIFY-SIG] PASS/FAIL line. Returns true on full pass.</summary>
    public static bool VerifySignatureV8(string path, byte[] publicKey32)
    {
        using var input = File.OpenRead(path);
        var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        input.Position = 0;
        string magic = Encoding.ASCII.GetString(reader.ReadBytes(8));
        if (magic != BinaryFormat.MagicV8)
        {
            Console.WriteLine($"[VERIFY-SIG] file magic '{magic}' (expected {BinaryFormat.MagicV8})");
            Console.WriteLine("[VERIFY-SIG] FAIL (not a v8 file)");
            return false;
        }
        BinaryFormat.ReadHeaderV8(reader, out _, out _, out _, out _, out int totalTiles,
            out long indexOffset, out int layerCount, out int lodCount, out int compressionType, out int signatureLength);

        long fileLen = input.Length;
        bool signatureFailed = false;
        bool unsigned = signatureLength <= 0;

        // --- (1) Signature over the entire index byte region ---
        if (signatureLength > 0)
        {
            // The signature is the trailing signatureLength bytes; the index region runs indexOffset..(signature start).
            long indexEnd = fileLen - signatureLength;
            if (indexEnd < indexOffset)
            {
                Console.WriteLine($"[VERIFY-SIG] SIGNATURE FAIL (file truncated: indexEnd {indexEnd} < indexOffset {indexOffset})");
                signatureFailed = true;
            }
            else
            {
                input.Position = indexOffset;
                var indexBytes = reader.ReadBytes((int)(indexEnd - indexOffset));
                input.Position = indexEnd;
                var sig = reader.ReadBytes(signatureLength);
                bool sigOk = Signing.Verify(publicKey32, indexBytes, sig);
                Console.WriteLine(sigOk ? "[VERIFY-SIG] SIGNATURE OK" : "[VERIFY-SIG] SIGNATURE FAIL");
                if (!sigOk) signatureFailed = true;
            }
        }
        else
        {
            Console.WriteLine("[VERIFY-SIG] SIGNATURE NONE (file is unsigned, signatureLength=0)");
        }

        // --- (2) Per-LOD block hashes ---
        // Re-parse the index, this time capturing each LOD's (offset, compressedSize, hash).
        input.Position = indexOffset;
        var blocks = new List<(int tile, int lod, long off, int csz, byte[] hash)>();
        for (int t = 0; t < totalTiles; t++)
        {
            reader.ReadInt64(); reader.ReadInt32(); reader.ReadInt32();
            reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle();
            var offs = new long[lodCount];
            var cszs = new int[lodCount];
            for (int lod = 0; lod < lodCount; lod++)
            {
                offs[lod] = reader.ReadInt64();
                cszs[lod] = reader.ReadInt32();
                reader.ReadInt32(); // uncompressedSize
                reader.ReadInt32(); // layerMask
            }
            for (int lod = 0; lod < lodCount; lod++)
            {
                var h = reader.ReadBytes(32);
                if (cszs[lod] > 0) blocks.Add((t, lod, offs[lod], cszs[lod], h));
            }
            for (int k = 0; k < layerCount; k++) reader.ReadInt32();
        }

        int checkedBlocks = 0, mismatches = 0;
        foreach (var (tile, lod, off, csz, expected) in blocks)
        {
            input.Position = off;
            var comp = reader.ReadBytes(csz);
            var actual = SHA256.HashData(comp);
            checkedBlocks++;
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                mismatches++;
                if (mismatches <= 8) Console.WriteLine($"  HASH MISMATCH (tile {tile}, lod {lod})");
            }
        }
        Console.WriteLine($"[VERIFY-SIG] block hashes: {checkedBlocks:N0} checked, {mismatches:N0} mismatch(es)");

        bool ok = !signatureFailed && mismatches == 0 && !unsigned;
        if (ok)
        {
            Console.WriteLine("[VERIFY-SIG] PASS");
        }
        else
        {
            var reasons = new List<string>();
            if (signatureFailed) reasons.Add("signature invalid");
            if (mismatches > 0) reasons.Add($"{mismatches} block hash mismatch(es)");
            if (unsigned) reasons.Add("file unsigned");
            Console.WriteLine($"[VERIFY-SIG] FAIL ({string.Join("; ", reasons)})");
        }
        return ok;
    }

    /// <summary>Reads a v8 file and prints all-LOD feature/vertex totals per layer — for cross-checking
    /// losslessness against the v7 file built from the same input (compare with fmstat's v7 report).</summary>
    public static void SummarizeV8(string path)
    {
        using var fs = File.OpenRead(path);
        var tiles = ReadV8(fs);
        long totalFeatures = 0, totalVerts = 0;
        var perLayer = new long[BinaryFormat.LayerCount];
        var perLayerVerts = new long[BinaryFormat.LayerCount];
        foreach (var t in tiles)
            for (int lod = 0; lod < t.Lods.Length; lod++)
                foreach (var (lt, list) in t.Lods[lod])
                    foreach (var g in list)
                    {
                        perLayer[(int)lt]++; perLayerVerts[(int)lt] += g.Vertices.Count;
                        totalFeatures++; totalVerts += g.Vertices.Count;
                    }

        Console.WriteLine($"[READ-V8] {path}");
        Console.WriteLine($"  tiles: {tiles.Count:N0}   features (all LODs): {totalFeatures:N0}   vertices (all LODs): {totalVerts:N0}");
        for (int i = 0; i < perLayer.Length; i++)
            if (perLayer[i] > 0)
                Console.WriteLine($"    {(BinaryFormat.LayerType)i,-16}: {perLayer[i],12:N0} feat  {perLayerVerts[i],14:N0} verts");
    }

    /// <summary>Read-side regression check covering BOTH halves of the InitState fast-path. For EVERY LOD0
    /// block it (1) decodes both with the unfiltered <see cref="DecodeBlock"/> and the filtered
    /// <see cref="DecodeBlockFiltered"/> (logic layers only) and asserts the wanted-layer features are
    /// bit-identical, and (2) recomputes the block's LayerMask from the layers actually present and asserts it
    /// equals the stored index mask — which is what <see cref="ReadLogicLayersV8"/>'s (mask&amp;wantedMask)==0
    /// whole-block skip relies on, so a match across all blocks proves the skip can never drop a wanted layer.
    /// Run via `formap --verify-logic &lt;v8&gt;`.</summary>
    public static void VerifyLogicFilterV8(string path)
    {
        var wanted = new HashSet<BinaryFormat.LayerType>
        {
            BinaryFormat.LayerType.Railways, BinaryFormat.LayerType.AdminBoundaries,
            BinaryFormat.LayerType.Places, BinaryFormat.LayerType.POIs,
            BinaryFormat.LayerType.Platforms, BinaryFormat.LayerType.Coastlines
        };

        using var input = File.OpenRead(path);
        var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        input.Position = 0;
        string magic = Encoding.ASCII.GetString(reader.ReadBytes(8));
        if (magic != BinaryFormat.MagicV8) throw new InvalidDataException($"Expected {BinaryFormat.MagicV8}, got '{magic}'");
        BinaryFormat.ReadHeaderV8(reader, out _, out _, out _, out _, out int totalTiles,
            out long indexOffset, out int layerCount, out int lodCount, out int compressionType, out _);
        using var zstd = compressionType == CompZstd ? new Decompressor() : null;

        int strCount = (int)FeatureCodecV8.ReadVarint(reader);
        var table = new string[strCount];
        for (int i = 0; i < strCount; i++)
        {
            int len = (int)FeatureCodecV8.ReadVarint(reader);
            table[i] = Encoding.UTF8.GetString(reader.ReadBytes(len));
        }

        int wantedMask = 0;
        foreach (var lt in wanted) wantedMask |= 1 << (int)lt;

        input.Position = indexOffset;
        var lod0 = new List<(long off, int csz, int mask)>(totalTiles);
        for (int t = 0; t < totalTiles; t++)
        {
            reader.ReadInt64(); reader.ReadInt32(); reader.ReadInt32();
            reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle();
            long off0 = 0; int csz0 = 0, mask0 = 0;
            for (int lod = 0; lod < lodCount; lod++)
            {
                long fo = reader.ReadInt64(); int cs = reader.ReadInt32(); reader.ReadInt32(); int lm = reader.ReadInt32();
                if (lod == 0) { off0 = fo; csz0 = cs; mask0 = lm; }
            }
            for (int lod = 0; lod < lodCount; lod++) reader.ReadBytes(32);
            for (int k = 0; k < layerCount; k++) reader.ReadInt32();
            lod0.Add((off0, csz0, mask0));
        }

        long compared = 0, fails = 0; int blocks = 0, maskMismatches = 0, skippableBlocks = 0;
        var perLayer = new long[BinaryFormat.LayerCount];
        foreach (var (off, csz, mask) in lod0)
        {
            if (csz <= 0) continue;
            input.Position = off;
            var comp = reader.ReadBytes(csz);
            byte[] body = compressionType == CompZstd ? zstd!.Unwrap(comp).ToArray() : LZ4Pickler.Unpickle(comp);
            var full = DecodeBlock(body, table);
            var filt = DecodeBlockFiltered(body, table, wanted);
            blocks++;

            // Cover the OTHER half of the optimization — the (mask & wantedMask)==0 whole-block skip in
            // ReadLogicLayersV8. Recompute the mask from the layers actually present in the full decode (mirrors
            // EncodeBlock: a bit is set iff that layer has features) and assert it equals the stored index mask.
            // If this holds for every block, then mask&wantedMask==0 can NEVER skip a block that holds a wanted
            // layer, so the fast-path skip is output-identical.
            int recomputed = 0;
            foreach (var (lt, list) in full) if (list.Count > 0) recomputed |= 1 << (int)lt;
            if (recomputed != mask)
            {
                maskMismatches++;
                if (maskMismatches <= 8) Console.WriteLine($"  block@{off}: stored mask 0x{mask:X} != present-layer mask 0x{recomputed:X}");
            }
            if ((mask & wantedMask) == 0) skippableBlocks++; // would be skipped by the fast path (no decompress/decode)

            foreach (var lt in wanted)
            {
                full.TryGetValue(lt, out var a);
                filt.TryGetValue(lt, out var b);
                int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
                if (ca != cb) { fails++; if (fails <= 8) Console.WriteLine($"  block@{off} {lt}: count {ca} vs {cb}"); continue; }
                for (int f = 0; f < ca; f++)
                {
                    var diff = FeatureCodecV8.Compare(a![f], b![f]);
                    compared++; perLayer[(int)lt]++;
                    if (diff != null) { fails++; if (fails <= 8) Console.WriteLine($"  block@{off} {lt} feat {f}: {diff}"); }
                }
            }
        }
        fails += maskMismatches;

        Console.WriteLine($"[VERIFY-LOGIC] {path}");
        Console.WriteLine($"  blocks decoded both ways: {blocks:N0}   wanted-layer features compared: {compared:N0}");
        Console.WriteLine($"  index mask vs present layers: {maskMismatches:N0} mismatch(es)   blocks the fast path skips (no wanted layer): {skippableBlocks:N0}");
        for (int i = 0; i < perLayer.Length; i++)
            if (perLayer[i] > 0) Console.WriteLine($"    {(BinaryFormat.LayerType)i,-16}: {perLayer[i],12:N0}");
        Console.WriteLine(fails == 0
            ? "[VERIFY-LOGIC] PASS — filtered read is bit-identical to the full decode AND every index mask matches the layers present (mask-skip proven safe)"
            : $"[VERIFY-LOGIC] FAIL — {fails:N0} mismatches");
        if (fails != 0) Environment.Exit(1);
    }

    private static (int, int, int, float, float, float, float, float) FeatKey(MeshGeometry g) =>
        (g.Vertices.Count, g.Indices.Count, g.Metadata.Count,
         g.BoundingBox.MinX, g.BoundingBox.MinY, g.BoundingBox.MaxX, g.BoundingBox.MaxY,
         g.Vertices.Count > 0 ? g.Vertices[0].X : 0f);

    /// <summary>Gold-standard losslessness check on REAL data: decodes a v7 file and a v8 file built from the
    /// same input and compares EVERY feature bit-exact (vertices, indices, hole/segment/junction ids, metadata,
    /// bbox). Run via `formap --verify-v8 &lt;v7.bin&gt; &lt;v8.bin&gt;`.</summary>
    public static void VerifyAgainstV7(string v7path, string v8path)
    {
        using var v8fs = File.OpenRead(v8path);
        var v8tiles = ReadV8(v8fs);

        using var fs = File.OpenRead(v7path);
        using var br = new BinaryReader(fs);
        string magic = Encoding.ASCII.GetString(br.ReadBytes(8));
        if (magic != BinaryFormat.MagicV7) { Console.WriteLine($"[VERIFY] v7 file magic '{magic}' (expected {BinaryFormat.MagicV7})"); return; }
        BinaryFormat.ReadHeaderV7(br, out _, out _, out _, out _, out int totalTiles, out long indexOffset);
        fs.Position = indexOffset;
        var entries = new List<BinaryFormat.TileIndexEntryV7>(totalTiles);
        for (int t = 0; t < totalTiles; t++) entries.Add(BinaryFormat.ReadTileIndexEntryV7(br));

        if (v8tiles.Count != totalTiles) Console.WriteLine($"[VERIFY] tile count v7={totalTiles} v8={v8tiles.Count} MISMATCH");
        int lodCount = BinaryFormat.LODCount;
        long compared = 0, fails = 0;

        for (int t = 0; t < totalTiles && t < v8tiles.Count; t++)
        {
            var e = entries[t];
            var v8t = v8tiles[t];
            for (int lod = 0; lod < lodCount; lod++)
            {
                var v7layers = new Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>();
                var li = e.LODs[lod];
                if (li.CompressedSize > 0)
                {
                    fs.Position = li.FileOffset + 4; // skip v7's int32 block-length prefix
                    var body = LZ4Pickler.Unpickle(br.ReadBytes(li.CompressedSize));
                    using var ms = new MemoryStream(body, false);
                    using var rb = new BinaryReader(ms);
                    while (ms.Position < body.Length)
                    {
                        int ltype = rb.ReadInt32(); int cnt = rb.ReadInt32();
                        var list = new List<MeshGeometry>(cnt);
                        for (int f = 0; f < cnt; f++) list.Add(MeshGeometry.Read(rb));
                        v7layers[(BinaryFormat.LayerType)ltype] = list;
                    }
                }
                var v8layers = v8t.Lods[lod];
                var lv7 = v7layers.Keys.OrderBy(x => x).ToList();
                var lv8 = v8layers.Keys.OrderBy(x => x).ToList();
                if (!lv7.SequenceEqual(lv8)) { fails++; if (fails <= 8) Console.WriteLine($"  tile {t} lod {lod}: layer set v7=[{string.Join(",", lv7)}] v8=[{string.Join(",", lv8)}]"); continue; }
                foreach (var lt in lv7)
                {
                    // Feature order within a layer is non-deterministic (parallel parse), so compare as an
                    // order-independent multiset: sort both sides by a strong content key, then pairwise.
                    var a = v7layers[lt].OrderBy(FeatKey).ToList();
                    var b = v8layers[lt].OrderBy(FeatKey).ToList();
                    if (a.Count != b.Count) { fails++; if (fails <= 8) Console.WriteLine($"  tile {t} lod {lod} {lt}: count {a.Count} vs {b.Count}"); continue; }
                    for (int f = 0; f < a.Count; f++)
                    {
                        var diff = FeatureCodecV8.Compare(a[f], b[f]);
                        compared++;
                        if (diff != null) { fails++; if (fails <= 8) Console.WriteLine($"  tile {t} lod {lod} {lt} feat {f}: {diff}"); }
                    }
                }
            }
            if ((t & 127) == 0) Console.Write($"\r  verifying tile {t}/{totalTiles}...   ");
        }
        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? $"[VERIFY] v7 vs v8 BIT-EXACT on real data — {compared:N0} features compared, 0 mismatches"
            : $"[VERIFY] FAILED — {fails:N0} mismatches over {compared:N0} features compared");
        if (fails != 0) Environment.Exit(1);
    }

    // --- Full-file round-trip self-test on synthetic tiles (run via `formap --selftest`) ---
    public static void SelfTestFile()
    {
        int layerCount = BinaryFormat.LayerCount;
        int lodCount = BinaryFormat.LODCount;

        var tileA = new TileGrid.TileInfo { TileID = TileGrid.GetTileID(0, 0), GridX = 0, GridY = 0, Bounds = TileGrid.GetTileBounds(0, 0) };
        tileA.Features[BinaryFormat.LayerType.Buildings].Add(Poly());
        tileA.Features[BinaryFormat.LayerType.Railways].Add(Rail());
        tileA.Features[BinaryFormat.LayerType.POIs].Add(Point());
        var tileB = new TileGrid.TileInfo { TileID = TileGrid.GetTileID(1, 0), GridX = 1, GridY = 0, Bounds = TileGrid.GetTileBounds(1, 0) };
        tileB.Features[BinaryFormat.LayerType.Forests].Add(Poly());
        tileB.Features[BinaryFormat.LayerType.Water].Add(PolyHole());
        var tiles = new List<TileGrid.TileInfo> { tileA, tileB };

        Dictionary<BinaryFormat.LayerType, List<MeshGeometry>> Filter(TileGrid.TileInfo t, int lod)
        {
            var d = new Dictionary<BinaryFormat.LayerType, List<MeshGeometry>>();
            foreach (var (lt, fs) in t.Features)
            {
                if (fs.Count == 0) continue;
                if (lod == 0) d[lt] = fs;
                else if (lod == 1) { if (lt != BinaryFormat.LayerType.POIs) d[lt] = fs; }
                else if (lt == BinaryFormat.LayerType.Railways || lt == BinaryFormat.LayerType.Water) d[lt] = fs;
            }
            return d;
        }

        var globalBounds = new BBox { MinX = 0, MinY = 0, MaxX = 20000, MaxY = 10000 };
        using var ms = new MemoryStream();
        WriteV8(ms, globalBounds, 2, 1, layerCount, lodCount, tiles, Filter);
        long size = ms.Length;
        var decoded = ReadV8(ms);

        int fails = 0;
        if (decoded.Count != tiles.Count) { Console.WriteLine($"  tile count {tiles.Count}->{decoded.Count} FAIL"); fails++; }
        for (int ti = 0; ti < tiles.Count && ti < decoded.Count; ti++)
            for (int lod = 0; lod < lodCount; lod++)
            {
                var expected = Filter(tiles[ti], lod);
                var got = decoded[ti].Lods[lod];
                var expLayers = expected.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).OrderBy(x => x).ToList();
                var gotLayers = got.Keys.OrderBy(x => x).ToList();
                if (!expLayers.SequenceEqual(gotLayers)) { Console.WriteLine($"  tile{ti} lod{lod}: layer set differs FAIL"); fails++; continue; }
                foreach (var lt in expLayers)
                {
                    var ef = expected[lt]; var gf = got[lt];
                    if (ef.Count != gf.Count) { Console.WriteLine($"  tile{ti} lod{lod} {lt}: feat count {ef.Count}->{gf.Count} FAIL"); fails++; continue; }
                    for (int f = 0; f < ef.Count; f++)
                    {
                        var diff = FeatureCodecV8.Compare(ef[f], gf[f]);
                        if (diff != null) { Console.WriteLine($"  tile{ti} lod{lod} {lt} feat{f}: {diff} FAIL"); fails++; }
                    }
                }
            }

        Console.WriteLine(fails == 0
            ? $"[SELFTEST] BinaryFormatV8 file round-trip PASS ({tiles.Count} tiles x {lodCount} LODs, {size} B)"
            : $"[SELFTEST] BinaryFormatV8 file round-trip FAILED ({fails})");
        if (fails != 0) Environment.Exit(1);
    }

    private static MeshGeometry Poly()
    {
        var g = new MeshGeometry();
        g.Vertices.AddRange(new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) });
        g.Indices.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
        g.Metadata["building"] = "yes"; g.Metadata["building:levels"] = "4";
        g.ComputeBoundingBox(); return g;
    }
    private static MeshGeometry Rail()
    {
        var g = new MeshGeometry();
        g.Vertices.AddRange(new[] { new Vector2(0, 0), new Vector2(5, 1), new Vector2(9, 2) });
        g.Indices.AddRange(new[] { 0, 1, 1, 2 });
        g.SegmentIds.AddRange(new[] { 42, 42 });
        g.JunctionIndices.AddRange(new[] { 0, 2 });
        g.Metadata["railway"] = "rail"; g.Metadata["maxspeed"] = "120";
        g.ComputeBoundingBox(); return g;
    }
    private static MeshGeometry Point()
    {
        var g = new MeshGeometry();
        g.Vertices.Add(new Vector2(3.3f, 4.4f));
        g.Metadata["railway"] = "station"; g.Metadata["name"] = "Test";
        g.ComputeBoundingBox(); return g;
    }
    private static MeshGeometry PolyHole()
    {
        var g = new MeshGeometry();
        g.Vertices.AddRange(new[] { new Vector2(0, 0), new Vector2(8, 0), new Vector2(8, 8), new Vector2(0, 8), new Vector2(2, 2), new Vector2(4, 2), new Vector2(4, 4), new Vector2(2, 4) });
        g.Indices.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
        g.HoleStarts.AddRange(new[] { 4 });
        g.Metadata["natural"] = "water";
        g.ComputeBoundingBox(); return g;
    }
}
