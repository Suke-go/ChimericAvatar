using System.Globalization;
using System.Text.Json;

namespace SplatPack.Compiler;

internal static class Program
{
    private const int DefaultChunkSize = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
            SplatPackTargetProfile targetProfile = ReadTargetProfileOption(args, "--target", SplatPackTargetProfile.StandaloneXr);
            int chunkSize = ReadIntOption(args, "--chunk-size", DefaultChunkSize);
            SplatPackRotationOrder rotationOrder = ReadRotationOrderOption(args, "--rotation-order", SplatPackRotationOrder.Auto);
            float opacityPruneThreshold = ReadFloatOption(
                args,
                "--opacity-prune",
                targetProfile == SplatPackTargetProfile.StandaloneXr ? 0.002f : 0f);
            float maxAxisLengthPercentile = ReadFloatOption(
                args,
                "--max-axis-percentile",
                targetProfile == SplatPackTargetProfile.StandaloneXr ? 99f : 0f);
            float maxAxisLengthMultiplier = ReadFloatOption(args, "--max-axis-multiplier", 1.1f);
            float maxAxisLength = ReadFloatOption(args, "--max-axis-length", 0f);
            bool writeReport = !args.Contains("--no-report");
            string reportPath = ReadStringOption(args, "--report", outputPath + ".report.json");

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Input file not found: {inputPath}");
                return 2;
            }

            byte[] plyBytes = File.ReadAllBytes(inputPath);
            PlyHeader header = PlyParser.ParseHeader(plyBytes);
            GaussianSplatStream stream = GaussianSplatStream.Read(plyBytes, header);
            var options = new SplatPackBuildOptions
            {
                ChunkSize = Math.Max(1, chunkSize),
                RotationOrder = rotationOrder,
                TargetProfile = targetProfile,
                OpacityPruneThreshold = MathF.Max(0f, opacityPruneThreshold),
                MaxAxisLength = MathF.Max(0f, maxAxisLength),
                MaxAxisLengthPercentile = Math.Clamp(maxAxisLengthPercentile, 0f, 100f),
                MaxAxisLengthMultiplier = MathF.Max(0.01f, maxAxisLengthMultiplier),
            };
            SplatPackBuildResult result = SplatPackBuilder.BuildWithReport(stream, options);
            SplatPackPackage package = result.Package;
            SplatPackRotationOrder resolvedRotationOrder = Enum.Parse<SplatPackRotationOrder>(result.Report.RotationOrder);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
            SplatPackWriter.Write(outputPath, package);
            if (writeReport)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".");
                File.WriteAllText(reportPath, JsonSerializer.Serialize(result.Report, JsonOptions));
            }

            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"SplatPack compiled: target={targetProfile}, splats={package.Splats.Length}/{stream.Count}, chunks={package.Chunks.Length}, rotation={resolvedRotationOrder}, prunedInvalid={result.Report.Counts.PrunedInvalidSplats}, prunedOpacity={result.Report.Counts.PrunedLowOpacitySplats}, axisClamped={result.Report.Counts.AxisClampedSplats}, axisCap={result.Report.Options.AxisLengthCap:0.######}, output={outputPath}, report={(writeReport ? reportPath : "off")}"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SplatPack compile failed: " + ex.Message);
            return 3;
        }
    }

    private static SplatPackTargetProfile ReadTargetProfileOption(
        string[] args,
        string name,
        SplatPackTargetProfile fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != name)
            {
                continue;
            }

            return args[i + 1].ToLowerInvariant() switch
            {
                "reference" => SplatPackTargetProfile.Reference,
                "standalone-xr" => SplatPackTargetProfile.StandaloneXr,
                "standalonexr" => SplatPackTargetProfile.StandaloneXr,
                "xr" => SplatPackTargetProfile.StandaloneXr,
                _ => throw new ArgumentException($"Unsupported {name}: {args[i + 1]}"),
            };
        }

        return fallback;
    }

    private static SplatPackRotationOrder ReadRotationOrderOption(
        string[] args,
        string name,
        SplatPackRotationOrder fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != name)
            {
                continue;
            }

            return args[i + 1].ToLowerInvariant() switch
            {
                "auto" => SplatPackRotationOrder.Auto,
                "xyzw" => SplatPackRotationOrder.XYZW,
                "wxyz" => SplatPackRotationOrder.WXYZ,
                _ => throw new ArgumentException($"Unsupported {name}: {args[i + 1]}"),
            };
        }

        return fallback;
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

    private static float ReadFloatOption(string[] args, string name, float fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static string ReadStringOption(string[] args, string name, string fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return fallback;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: SplatPackCompiler input.ply output.splatpack [--target reference|standalone-xr] [--chunk-size 4096] [--rotation-order auto|xyzw|wxyz] [--opacity-prune 0.002] [--max-axis-percentile 99] [--max-axis-multiplier 1.1] [--max-axis-length meters] [--report path] [--no-report]");
    }
}
