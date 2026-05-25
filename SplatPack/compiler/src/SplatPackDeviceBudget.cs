using System.Text.Json;

namespace SplatPack.Compiler;

public sealed class SplatPackDeviceBudget
{
    public string Name { get; init; } = "StandaloneXR-72Hz";
    public int TargetHz { get; init; } = 72;
    public int StereoEyes { get; init; } = 2;
    public int VisibleSplatBudget { get; init; } = 120_000;
    public int ProjectionScanBudget { get; init; } = 300_000;
    public int SortScanBudget { get; init; } = 300_000;
    public int RuntimeMemoryBudgetMB { get; init; } = 128;
    public float MaxPackageMB { get; init; } = 32f;
    public float MinRetainedImportance { get; init; } = 0.55f;

    public static SplatPackDeviceBudget Load(string path)
    {
        SplatPackDeviceBudget? budget = JsonSerializer.Deserialize<SplatPackDeviceBudget>(
            File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        return budget ?? throw new InvalidDataException($"Invalid device budget JSON: {path}");
    }

    public bool Fits(SplatPackParetoRow row)
    {
        return row.EstimatedResidentMegabytes <= RuntimeMemoryBudgetMB
               && row.PackageMegabytes <= MaxPackageMB
               && row.ProjectionScans <= ProjectionScanBudget
               && row.SortScans <= SortScanBudget
               && row.RuntimeVisibleSplats <= VisibleSplatBudget
               && row.RetainedImportance >= MinRetainedImportance;
    }
}
