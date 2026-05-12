using System.Globalization;

namespace SplatPack.Compiler;

internal static class Program
{
    private const int DefaultChunkSize = 4096;

    public static int Main(string[] args)
    {
        try
        {
            bool wantsHelp = args.Contains("--help") || args.Contains("-h");
            if (args.Length < 2 || wantsHelp)
            {
                PrintUsage();
                return wantsHelp ? 0 : 1;
            }

            string inputPath = args[0];
            string outputPath = args[1];
            int chunkSize = ReadIntOption(args, "--chunk-size", DefaultChunkSize);

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Input file not found: {inputPath}");
                return 2;
            }

            byte[] plyBytes = File.ReadAllBytes(inputPath);
            PlyHeader header = PlyParser.ParseHeader(plyBytes);
            GaussianSplatStream stream = GaussianSplatStream.Read(plyBytes, header);
            SplatPackPackage package = SplatPackBuilder.Build(stream, Math.Max(1, chunkSize));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
            SplatPackWriter.Write(outputPath, package);

            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"SplatPack compiled: splats={package.Splats.Length}, chunks={package.Chunks.Length}, output={outputPath}"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SplatPack compile failed: " + ex.Message);
            return 3;
        }
    }

    private static int ReadIntOption(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: SplatPackCompiler input.ply output.splatpack [--chunk-size 4096]");
    }
}
