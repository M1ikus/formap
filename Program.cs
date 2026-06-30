using OsmSharp;
using RailwayManager.GraphData;

namespace formap;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--selftest")
        {
            FeatureCodecV8.SelfTest();
            BinaryFormatV8.SelfTestFile();
            return;
        }

        if (args.Length >= 2 && args[0] == "--gen-key")
        {
            string keyPath = args[1];
            Signing.GenerateKeypair(keyPath);
            Console.WriteLine($"[GEN-KEY] private key: {keyPath}.priv ({Signing.PrivateKeySize}-byte Ed25519 seed)");
            Console.WriteLine($"[GEN-KEY] public key:  {keyPath}.pub ({Signing.PublicKeySize}-byte Ed25519 public key)");
            return;
        }

        if (args.Length >= 3 && args[0] == "--verify-sig")
        {
            string file = args[1];
            string pubPath = args[2];
            if (!File.Exists(file)) { Console.WriteLine($"Error: File {file} not found."); Environment.Exit(1); }
            if (!File.Exists(pubPath)) { Console.WriteLine($"Error: Public key {pubPath} not found."); Environment.Exit(1); }
            byte[] pubKey = File.ReadAllBytes(pubPath);
            bool pass = BinaryFormatV8.VerifySignatureV8(file, pubKey);
            if (!pass) Environment.Exit(1);
            return;
        }

        if (args.Length >= 3 && args[0] == "--sign-existing")
        {
            string file = args[1];
            string privPath = args[2];
            if (!File.Exists(file)) { Console.WriteLine($"Error: File {file} not found."); Environment.Exit(1); }
            if (!File.Exists(privPath)) { Console.WriteLine($"Error: Private key {privPath} not found."); Environment.Exit(1); }
            BinaryFormatV8.SignExistingV8(file, File.ReadAllBytes(privPath));
            return;
        }

        if (args.Length >= 2 && args[0] == "--read-v8")
        {
            BinaryFormatV8.SummarizeV8(args[1]);
            return;
        }

        // CI determinism self-check (v5): build the init-state from a v8 map TWICE and assert the content keys
        // (TrackKey, station OsmNodeId, per-edge track/station mapping, platform entries) are byte-identical —
        // i.e. baked timetables stay regen-stable. Exit 0 = deterministic, 1 = drift, 2 = error.
        if (args.Length >= 3 && args[0] == "--v5-selfcheck") // <map.v8.bin> <country>
        {
            string v8 = args[1], country = args[2];
            if (!File.Exists(v8)) { Console.WriteLine($"Error: {v8} not found."); Environment.Exit(2); }
            string isPath = InitStateBuilder.GetInitStatePath(v8, country);
            InitStateBuilder.BuildAndWrite(v8, country);
            string a = V5Fingerprint(isPath);
            InitStateBuilder.BuildAndWrite(v8, country);
            string b = V5Fingerprint(isPath);
            bool ok = a == b;
            Console.WriteLine($"[V5-SELFCHECK] {v8}");
            Console.WriteLine($"  build1 {a.Substring(0, 16)}…   build2 {b.Substring(0, 16)}…");
            Console.WriteLine(ok
                ? "[V5-SELFCHECK] PASS — v5 content keys deterministic across builds (timetables regen-stable)"
                : "[V5-SELFCHECK] FAIL — non-deterministic content keys");
            Environment.Exit(ok ? 0 : 1);
        }

        // Assert that an init-state file is valid for a given v8 map (the freshness gate the game uses):
        // matches by v8 tile-index content hash, NOT mtime. Exit 0 = VALID (game fast-paths), 1 = STALE/mismatch
        // (game would rebuild the graph), 2 = error. Use in deploy/CI to catch a mispaired init-state before shipping.
        if (args.Length >= 4 && args[0] == "--check-initstate") // <init-state.bin> <map.v8.bin> <country>
        {
            string isPath = args[1], v8Path = args[2], country = args[3];
            if (!File.Exists(isPath)) { Console.WriteLine($"Error: init-state {isPath} not found."); Environment.Exit(2); }
            if (!File.Exists(v8Path)) { Console.WriteLine($"Error: v8 map {v8Path} not found."); Environment.Exit(2); }
            byte[] mapHash;
            try { mapHash = BinaryFormatV8.ComputeMapIndexHash(v8Path); }
            catch (Exception ex) { Console.WriteLine($"[CHECK-INITSTATE] cannot hash {v8Path}: {ex.Message}"); Environment.Exit(2); return; }
            bool ok = RailwayManager.GraphData.InitStateReader.IsValidFor(isPath, country, mapHash);
            Console.WriteLine($"[CHECK-INITSTATE] {isPath} vs {v8Path} (country={country})");
            Console.WriteLine($"  v8 index hash: {Convert.ToHexString(mapHash)}");
            Console.WriteLine($"  result: {(ok ? "VALID — game uses the fast-path" : "STALE/MISMATCH — game would rebuild the graph")}");
            Environment.Exit(ok ? 0 : 1);
        }

        if (args.Length >= 2 && args[0] == "--verify-logic")
        {
            BinaryFormatV8.VerifyLogicFilterV8(args[1]);
            return;
        }

        if (args.Length >= 3 && args[0] == "--verify-v8")
        {
            BinaryFormatV8.VerifyAgainstV7(args[1], args[2]);
            return;
        }

        if (args.Length < 1)
        {
            Console.WriteLine("Usage: formap <input.osm.pbf> [output.bin] [--format v8|v7] [--country PL] [--no-init-state] [--init-state-only <existing.bin>] [--sign-key <priv>]");
            Console.WriteLine("  --format v8           Write v8 (FORMAP04) tiled multi-LOD format — DEFAULT (lossless, ~44% smaller than v7)");
            Console.WriteLine("  --format v7           Write legacy v7 (FORMAP03) format");
            Console.WriteLine("  --country <code>      ISO 3166-1 alpha-2 country code for init-state (default: PL)");
            Console.WriteLine("  --no-init-state       Skip building init-state-<country>.bin after conversion");
            Console.WriteLine("  --init-state-only X   Skip OSM conversion, build init-state from existing X.bin (v7 or v8)");
            Console.WriteLine("  --sign-key <priv>     Ed25519-sign the v8 output (Pillar 2) using a 32-byte private seed");
            Console.WriteLine();
            Console.WriteLine("  --gen-key <path>              Generate an Ed25519 keypair → <path>.priv / <path>.pub, then exit");
            Console.WriteLine("  --verify-sig <file> <pubkey>  Verify a signed v8 file's signature + per-block hashes, then exit");
            Console.WriteLine("  --sign-existing <file> <priv> Ed25519-sign an existing v8 file in place (no re-build), then exit");
            Console.WriteLine("  --check-initstate <init-state> <map.v8> <country>  Assert init-state matches the v8 map (content hash, not mtime); exit 0=valid, 1=stale");
            Console.WriteLine("  --v5-selfcheck <map.v8> <country>                  Build init-state twice; assert v5 content keys are deterministic; exit 0=ok, 1=drift");
            return;
        }

        string inputFile = args[0];
        string outputFile = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.ChangeExtension(inputFile, ".bin");
        string countryCode = "PL";
        bool buildInitState = true;
        string? initStateOnlyPath = null;
        int formatVersion = 8; // v8 (FORMAP04) is the default output; --format v7 still available as legacy
        bool verifyWrite = false;
        int compressionType = 0; // 0 = LZ4-HC, 1 = Zstd
        string? signKeyPath = null; // --sign-key <privpath> → Ed25519-sign the v8 file (Pillar 2)

        // Parse flags
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--format" && i + 1 < args.Length)
            {
                string fmt = args[i + 1].ToLower();
                if (fmt == "v8" || fmt == "8") formatVersion = 8;
                else if (fmt == "v7" || fmt == "7") formatVersion = 7;
                else Console.WriteLine($"Unknown format '{fmt}'; using v{formatVersion}.");
            }
            else if (args[i] == "--country" && i + 1 < args.Length)
            {
                countryCode = args[i + 1].ToUpper();
            }
            else if (args[i] == "--no-init-state")
            {
                buildInitState = false;
            }
            else if (args[i] == "--verify-write")
            {
                verifyWrite = true;
            }
            else if (args[i] == "--compress" && i + 1 < args.Length)
            {
                compressionType = args[i + 1].ToLower() == "zstd" ? 1 : 0;
            }
            else if (args[i] == "--init-state-only" && i + 1 < args.Length)
            {
                initStateOnlyPath = args[i + 1];
            }
            else if (args[i] == "--sign-key" && i + 1 < args.Length)
            {
                signKeyPath = args[i + 1];
            }
        }

        // --init-state-only mode: skip conversion, only build init-state from an existing .bin
        if (initStateOnlyPath != null)
        {
            if (!File.Exists(initStateOnlyPath))
            {
                Console.WriteLine($"Error: File {initStateOnlyPath} not found.");
                return;
            }
            Console.WriteLine($"[init-state-only] Building init-state-{countryCode.ToLower()}.bin from {initStateOnlyPath}...");
            try
            {
                InitStateBuilder.BuildAndWrite(initStateOnlyPath, countryCode);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"InitStateBuilder failed: {ex}");
                Environment.Exit(1);
            }
            return;
        }

        if (!File.Exists(inputFile))
        {
            Console.WriteLine($"Error: File {inputFile} not found.");
            return;
        }

        Console.WriteLine($"Reading OSM data from: {inputFile}");
        Console.WriteLine($"Output format: v{formatVersion}");

        var converter = new OsmConverter();
        converter.VerifyAfterWriteV8 = verifyWrite;
        converter.CompressionTypeV8 = compressionType;
        if (signKeyPath != null)
        {
            if (!File.Exists(signKeyPath))
            {
                Console.WriteLine($"Error: signing key {signKeyPath} not found.");
                Environment.Exit(1);
            }
            byte[] priv = File.ReadAllBytes(signKeyPath);
            if (priv.Length != Signing.PrivateKeySize)
            {
                Console.WriteLine($"Error: signing key {signKeyPath} is {priv.Length} bytes (expected {Signing.PrivateKeySize}).");
                Environment.Exit(1);
            }
            converter.SigningPrivateKeyV8 = priv;
            Console.WriteLine($"[SIGN] Ed25519-signing v8 output with {signKeyPath}");
        }
        converter.Convert(inputFile, outputFile, formatVersion);

        Console.WriteLine($"Conversion complete. Output: {outputFile}");

        // After conversion: build init-state-<country>.bin next to poland-v7.bin.
        if (buildInitState)
        {
            Console.WriteLine();
            Console.WriteLine("=== InitState build phase ===");
            // Free the conversion state (feature lists, way cache, buffers) first: the InitState phase re-reads
            // the written .bin, so this GB-scale state is dead weight that would otherwise starve it of RAM.
            converter.ReleaseConversionState();
            try
            {
                InitStateBuilder.BuildAndWrite(outputFile, countryCode);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"InitStateBuilder failed: {ex}");
                Console.Error.WriteLine("Map .bin was saved OK, but init-state.bin was not created.");
                Console.Error.WriteLine("You can try again: formap --init-state-only " + outputFile + " --country " + countryCode);
                Environment.Exit(2);
            }
        }
    }

    // v5 determinism fingerprint: hashes the content keys that baked timetables depend on — sorted TrackKeys,
    // sorted station OsmNodeIds, per-edge (TrackKey, StationOsmId, SegmentId), and platform (TrackKey, FromM, ToM).
    static string V5Fingerprint(string initStatePath)
    {
        var st = InitStateReader.Read(initStatePath);
        var g = st.PathfindingGraph;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        var tks = new List<long>(); foreach (var t in st.Tracks) tks.Add(t.TrackKey); tks.Sort();
        foreach (var k in tks) sb.Append(k).Append(',');
        sb.Append('#');
        var oids = new List<long>(); foreach (var s in st.Stations) oids.Add(s.OsmNodeId); oids.Sort();
        foreach (var o in oids) sb.Append(o).Append(',');
        sb.Append('#');
        foreach (var e in g.Edges)
        {
            long tk = e.TrackIndex >= 0 && e.TrackIndex < st.Tracks.Count ? st.Tracks[e.TrackIndex].TrackKey : 0;
            long so = e.StationId >= 0 && e.StationId < st.Stations.Count ? st.Stations[e.StationId].OsmNodeId : -1;
            sb.Append(tk).Append(':').Append(so).Append(':').Append(e.SegmentId).Append('|');
        }
        sb.Append('#');
        foreach (var p in st.Platforms)
        {
            foreach (var en in p.Entries)
            {
                long tk = en.TrackIndex >= 0 && en.TrackIndex < st.Tracks.Count ? st.Tracks[en.TrackIndex].TrackKey : 0;
                sb.Append(tk).Append(',').Append(en.FromM.ToString("F2", ci)).Append(',').Append(en.ToM.ToString("F2", ci)).Append(';');
            }
            sb.Append('/');
        }
        var h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(h);
    }
}
