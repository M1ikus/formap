using OsmSharp;

namespace formap;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: formap <input.osm.pbf> [output.bin] [--format v7] [--country PL] [--no-init-state] [--init-state-only <existing.bin>]");
            Console.WriteLine("  --format v7           Write multi-LOD tiled format (default; only v7 is supported)");
            Console.WriteLine("  --country <code>      ISO 3166-1 alpha-2 country code dla init-state (default: PL)");
            Console.WriteLine("  --no-init-state       Skip building init-state-<country>.bin po konwersji");
            Console.WriteLine("  --init-state-only X   Skip OSM conversion, build init-state z istniejącego X.bin");
            return;
        }

        string inputFile = args[0];
        string outputFile = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.ChangeExtension(inputFile, ".bin");
        string countryCode = "PL";
        bool buildInitState = true;
        string? initStateOnlyPath = null;

        // Parse flags
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--format" && i + 1 < args.Length)
            {
                string fmt = args[i + 1].ToLower();
                if (fmt != "v7" && fmt != "7")
                    Console.WriteLine("Only v7 output is supported; using v7.");
            }
            else if (args[i] == "--country" && i + 1 < args.Length)
            {
                countryCode = args[i + 1].ToUpper();
            }
            else if (args[i] == "--no-init-state")
            {
                buildInitState = false;
            }
            else if (args[i] == "--init-state-only" && i + 1 < args.Length)
            {
                initStateOnlyPath = args[i + 1];
            }
        }

        // Tryb --init-state-only: pomijamy konwersję, budujemy tylko init-state z istniejącego .bin
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
        Console.WriteLine("Output format: v7");

        var converter = new OsmConverter();
        converter.Convert(inputFile, outputFile, 7);

        Console.WriteLine($"Conversion complete. Output: {outputFile}");

        // Po konwersji: zbuduj init-state-<country>.bin obok poland-v7.bin.
        if (buildInitState)
        {
            Console.WriteLine();
            Console.WriteLine("=== InitState build phase ===");
            try
            {
                InitStateBuilder.BuildAndWrite(outputFile, countryCode);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"InitStateBuilder failed: {ex}");
                Console.Error.WriteLine("Map .bin został zapisany OK, ale init-state.bin nie powstał.");
                Console.Error.WriteLine("Możesz spróbować ponownie: formap --init-state-only " + outputFile + " --country " + countryCode);
                Environment.Exit(2);
            }
        }
    }
}
