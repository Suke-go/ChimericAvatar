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
            SplatPackOutputFormat outputFormat = ReadOutputFormatOption(args, "--format", SplatPackOutputFormat.Auto);
            SplatPackShMode shMode = ReadShModeOption(args, "--sh", targetProfile == SplatPackTargetProfile.Reference ? SplatPackShMode.Sh3 : SplatPackShMode.Sh3);
            SplatPackFilterMode filterMode = ReadFilterModeOption(args, "--filter", targetProfile == SplatPackTargetProfile.Reference ? SplatPackFilterMode.None : SplatPackFilterMode.Mip2D);
            SplatPackPruningMode pruningMode = ReadPruningModeOption(args, "--pruning", targetProfile == SplatPackTargetProfile.Reference ? SplatPackPruningMode.Heuristic : SplatPackPruningMode.ResearchXrSensitivity);
            SplatPackSortMode sortMode = ReadSortModeOption(args, "--sort", targetProfile == SplatPackTargetProfile.Reference ? SplatPackSortMode.StableRadixPerEye : SplatPackSortMode.StableRadixPerEye);
            // Artifact repair is intentionally opt-in. ShellSurfaceV1 is the cheaper
            // source-splat reinsert pass; Gaussian pullback is the heavier probe pass.
            // Keep standalone-xr defaults fast and make repair mode explicit in scripts.
            SplatPackArtifactRepairMode artifactRepairMode = ReadArtifactRepairModeOption(
                args,
                "--artifact-repair",
                SplatPackArtifactRepairMode.None);
            int maxSplats = ReadIntOption(args, "--max-splats", targetProfile == SplatPackTargetProfile.StandaloneXr ? 120_000 : 0);
            int chunkSize = ReadIntOption(args, "--chunk-size", DefaultChunkSize);
            SplatPackRotationOrder rotationOrder = ReadRotationOrderOption(args, "--rotation-order", SplatPackRotationOrder.Auto);
            float opacityPruneThreshold = ReadFloatOption(
                args,
                "--opacity-prune",
                targetProfile == SplatPackTargetProfile.StandaloneXr ? 0.002f : 0f);
            float maxAxisLengthPercentile = ReadFloatOption(
                args,
                "--max-axis-percentile",
                targetProfile == SplatPackTargetProfile.StandaloneXr ? 90f : 0f);
            float maxAxisLengthMultiplier = ReadFloatOption(args, "--max-axis-multiplier", 1.1f);
            float maxAxisLength = ReadFloatOption(args, "--max-axis-length", 0f);
            float maxAxisRatio = ReadFloatOption(args, "--max-axis-ratio", targetProfile == SplatPackTargetProfile.StandaloneXr ? 8f : 0f);
            float contributionAxisPower = ReadFloatOption(args, "--contribution-axis-power", 0.5f);
            float contributionPruneThreshold = ReadFloatOption(
                args,
                "--contribution-prune",
                targetProfile == SplatPackTargetProfile.StandaloneXr ? 0.0015f : 0f);
            float maxImportanceError = ReadFloatOption(args, "--max-importance-error", 0f);
            bool enableCoverageCompensation = !args.Contains("--no-coverage-compensation") && targetProfile == SplatPackTargetProfile.StandaloneXr;
            float maxCoverageBoost = ReadFloatOption(args, "--max-coverage-boost", targetProfile == SplatPackTargetProfile.StandaloneXr ? 2.4f : 1f);
            float coverageAxisPower = ReadFloatOption(args, "--coverage-axis-power", targetProfile == SplatPackTargetProfile.StandaloneXr ? 1.0f : 0f);
            float farLodKeepFraction = ReadFloatOption(args, "--far-lod-keep", targetProfile == SplatPackTargetProfile.StandaloneXr ? 0.35f : 1f);
            float midLodKeepFraction = ReadFloatOption(args, "--mid-lod-keep", targetProfile == SplatPackTargetProfile.StandaloneXr ? 0.68f : 1f);
            bool enableAttributeQuantization = !args.Contains("--no-attribute-quantization") && targetProfile == SplatPackTargetProfile.StandaloneXr;
            int positionQuantizationBits = ReadIntOption(args, "--position-bits", targetProfile == SplatPackTargetProfile.StandaloneXr ? 16 : 0);
            int axisQuantizationBits = ReadIntOption(args, "--axis-bits", targetProfile == SplatPackTargetProfile.StandaloneXr ? 14 : 0);
            int colorQuantizationBits = ReadIntOption(args, "--color-bits", targetProfile == SplatPackTargetProfile.StandaloneXr ? 8 : 0);
            int opacityQuantizationBits = ReadIntOption(args, "--opacity-bits", targetProfile == SplatPackTargetProfile.StandaloneXr ? 10 : 0);
            bool enableViewSampledImportance = !args.Contains("--no-view-importance");
            string viewsetPath = ReadStringOption(args, "--viewset", string.Empty);
            string probeViewsetPath = ReadStringOption(args, "--probe-viewset", string.Empty);
            int probeResolution = ReadIntOption(args, "--probe-resolution", 256);
            int probeViewCount = ReadIntOption(args, "--probe-view-count", 16);
            float repairBudgetRatio = ReadFloatOption(args, "--repair-budget-ratio", 0.12f);
            int repairIterations = ReadIntOption(args, "--repair-iterations", 3);
            int probeCellSize = ReadIntOption(args, "--probe-cell-size", 4);
            float probeFovY = ReadFloatOption(args, "--probe-fov-y", 70f);
            float probeAspect = ReadFloatOption(args, "--probe-aspect", 1f);
            float probeIpd = ReadFloatOption(args, "--probe-ipd", 0.064f);
            int probeLayerBins = ReadIntOption(args, "--probe-layer-bins", 2);
            SplatPackPullbackWeights pullbackWeights = ReadPullbackWeightsOption(args, "--pullback-weight", SplatPackPullbackWeights.Default);
            bool enableStreamingLayout = !args.Contains("--no-streaming-layout");
            bool enableSpatialOutlierPrune = !args.Contains("--no-spatial-outlier-prune") && targetProfile == SplatPackTargetProfile.StandaloneXr;
            int spatialOutlierGrid = ReadIntOption(args, "--spatial-outlier-grid", 0);
            int spatialOutlierMinNeighbors = ReadIntOption(args, "--spatial-outlier-min-neighbors", targetProfile == SplatPackTargetProfile.StandaloneXr ? 5 : 0);
            float spatialOutlierKeepImportance = ReadFloatOption(args, "--spatial-outlier-keep-importance", 0.55f);

            // Direction C MVP: post-pass class drop. Removes splats classified as
            // free-space / floater / layer-risk after the standard pipeline so the
            // resulting SplatPack permanently lacks background haze contributors.
            bool dropFreeSpaceClass = args.Contains("--drop-free-class");
            bool dropFloaterClass = args.Contains("--drop-floater-class");
            bool dropLayerRiskClass = args.Contains("--drop-layer-class");
            if (args.Contains("--drop-haze-classes"))
            {
                dropFreeSpaceClass = true;
                dropFloaterClass = true;
            }
            float classDropSupportProtect = ReadFloatOption(args, "--class-drop-support-protect", 0f);

            // Direction C: field-aware pruning. Voxelizes splat density and drops the
            // lowest-field-support fraction, catching floaters the class drop misses.
            bool enableFieldAwarePruning = args.Contains("--field-prune");
            int fieldVoxelGrid = ReadIntOption(args, "--field-voxel-grid", 64);
            float fieldPruneFraction = ReadFloatOption(args, "--field-prune-fraction", 0.10f);
            float fieldNeighborhoodRadiusVoxels = ReadFloatOption(args, "--field-neighborhood-voxels", 1.5f);
            float fieldProtectAlpha = ReadFloatOption(args, "--field-protect-alpha", 0.5f);
            bool enableSplatInjection = args.Contains("--splat-injection") || args.Contains("--inject-splats");
            int injectionVoxelGrid = ReadIntOption(args, "--injection-voxel-grid", 64);
            int injectionTargetCount = ReadIntOption(args, "--injection-target-count", 0);
            float injectionMaxFraction = ReadFloatOption(args, "--injection-max-fraction", 0.10f);
            int injectionPasses = ReadIntOption(args, "--injection-passes", 1);
            bool enableSceneClassifier = args.Contains("--scene-classifier");
            float sceneClassifierCoreFraction = ReadFloatOption(args, "--scene-classifier-core-fraction", 0.45f);
            bool enableSurfaceAwareInjection = args.Contains("--injection-surface-aware");
            float surfaceAwareThicknessScale = ReadFloatOption(args, "--injection-surface-thickness", 0.35f);
            float injectionNeighborhoodRadiusVoxels = ReadFloatOption(args, "--injection-neighborhood-voxels", 1.5f);
            float injectionDeficitThreshold = ReadFloatOption(args, "--injection-deficit-threshold", 0.5f);
            float injectionAlphaScale = ReadFloatOption(args, "--injection-alpha-scale", 0.6f);
            float injectionScaleFactor = ReadFloatOption(args, "--injection-scale-factor", 1.0f);
            bool writeReport = !args.Contains("--no-report");
            string reportPath = ReadStringOption(args, "--report", outputPath + ".report.json");
            string paretoPath = ReadStringOption(args, "--pareto", string.Empty);
            int[] paretoBudgets = ReadIntListOption(args, "--pareto-budgets", DefaultParetoBudgets(maxSplats));
            float[] paretoCoverageBoosts = ReadFloatListOption(args, "--pareto-coverage", [1.0f, 1.4f, maxCoverageBoost]);
            float[] paretoImportanceErrors = ReadFloatListOption(args, "--pareto-errors", [0f]);
            string deviceBudgetPath = ReadStringOption(args, "--device-budget", string.Empty);
            string paretoSelectionPath = ReadStringOption(args, "--pareto-selection", string.Empty);

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Input file not found: {inputPath}");
                return 2;
            }

            SplatPackViewSample[] viewSamples = Array.Empty<SplatPackViewSample>();
            string viewSampleSource = "default-octant";
            if (!string.IsNullOrWhiteSpace(viewsetPath))
            {
                if (!File.Exists(viewsetPath))
                {
                    Console.Error.WriteLine($"Viewset file not found: {viewsetPath}");
                    return 2;
                }

                viewSamples = SplatPackViewSet.Load(viewsetPath);
                viewSampleSource = viewsetPath;
            }

            SplatPackViewSample[] probeViewSamples = Array.Empty<SplatPackViewSample>();
            string probeViewSource = "default-probe";
            if (!string.IsNullOrWhiteSpace(probeViewsetPath))
            {
                if (!File.Exists(probeViewsetPath))
                {
                    Console.Error.WriteLine($"Probe viewset file not found: {probeViewsetPath}");
                    return 2;
                }

                probeViewSamples = SplatPackViewSet.Load(probeViewsetPath);
                probeViewSource = probeViewsetPath;
            }

            SplatPackDeviceBudget deviceBudget = string.IsNullOrWhiteSpace(deviceBudgetPath)
                ? new SplatPackDeviceBudget()
                : SplatPackDeviceBudget.Load(deviceBudgetPath);

            byte[] plyBytes = File.ReadAllBytes(inputPath);
            PlyHeader header = PlyParser.ParseHeader(plyBytes);
            GaussianSplatStream stream = GaussianSplatStream.Read(plyBytes, header);
            SplatPackBuildOptions options = new SplatPackBuildOptions
            {
                ChunkSize = Math.Max(1, chunkSize),
                RotationOrder = rotationOrder,
                TargetProfile = targetProfile,
                OutputFormat = outputFormat,
                ShMode = shMode,
                FilterMode = filterMode,
                PruningMode = pruningMode,
                SortMode = sortMode,
                MaxSplats = Math.Max(0, maxSplats),
                OpacityPruneThreshold = MathF.Max(0f, opacityPruneThreshold),
                MaxAxisLength = MathF.Max(0f, maxAxisLength),
                MaxAxisLengthPercentile = Math.Clamp(maxAxisLengthPercentile, 0f, 100f),
                MaxAxisLengthMultiplier = MathF.Max(0.01f, maxAxisLengthMultiplier),
                MaxAxisRatio = MathF.Max(0f, maxAxisRatio),
                ContributionAxisPower = Math.Clamp(contributionAxisPower, 0f, 2f),
                ContributionPruneThreshold = MathF.Max(0f, contributionPruneThreshold),
                EnableViewSampledImportance = enableViewSampledImportance,
                ViewSampleSource = viewSampleSource,
                ViewSamples = viewSamples,
                MaxImportanceError = Math.Clamp(maxImportanceError, 0f, 0.95f),
                EnableCoverageCompensation = enableCoverageCompensation,
                MaxCoverageBoost = MathF.Max(1f, maxCoverageBoost),
                CoverageAxisPower = Math.Clamp(coverageAxisPower, 0f, 2f),
                FarLodKeepFraction = Math.Clamp(farLodKeepFraction, 0.01f, 1f),
                MidLodKeepFraction = Math.Clamp(midLodKeepFraction, 0.01f, 1f),
                EnableAttributeQuantization = enableAttributeQuantization,
                PositionQuantizationBits = Math.Clamp(positionQuantizationBits, 0, 24),
                AxisQuantizationBits = Math.Clamp(axisQuantizationBits, 0, 24),
                ColorQuantizationBits = Math.Clamp(colorQuantizationBits, 0, 16),
                OpacityQuantizationBits = Math.Clamp(opacityQuantizationBits, 0, 16),
                EnableStreamingLayout = enableStreamingLayout,
                EnableSpatialOutlierPrune = enableSpatialOutlierPrune,
                SpatialOutlierGridResolution = Math.Clamp(spatialOutlierGrid, 0, 128),
                SpatialOutlierMinNeighbors = Math.Max(1, spatialOutlierMinNeighbors),
                SpatialOutlierKeepImportance = Math.Clamp(spatialOutlierKeepImportance, 0f, 1f),
                ArtifactRepairMode = artifactRepairMode,
                ProbeResolution = Math.Clamp(probeResolution, 16, 1024),
                ProbeViewCount = Math.Clamp(probeViewCount, 1, 128),
                ProbeViewSource = probeViewSource,
                ProbeViewSamples = probeViewSamples,
                RepairBudgetRatio = Math.Clamp(repairBudgetRatio, 0f, 0.5f),
                RepairIterations = Math.Clamp(repairIterations, 1, 8),
                ProbeCellSize = Math.Clamp(probeCellSize, 1, 32),
                ProbeFovYDegrees = Math.Clamp(probeFovY, 20f, 140f),
                ProbeAspect = Math.Clamp(probeAspect, 0.25f, 4f),
                ProbeIpdMeters = Math.Clamp(probeIpd, 0f, 0.2f),
                ProbeLayerBins = Math.Clamp(probeLayerBins, 1, 4),
                PullbackWeights = pullbackWeights,
                DropFreeSpaceClass = dropFreeSpaceClass,
                DropFloaterClass = dropFloaterClass,
                DropLayerRiskClass = dropLayerRiskClass,
                ClassDropSupportProtect = Math.Clamp(classDropSupportProtect, 0f, 1f),
                EnableFieldAwarePruning = enableFieldAwarePruning,
                FieldVoxelGrid = Math.Clamp(fieldVoxelGrid, 4, 256),
                FieldPruneFraction = Math.Clamp(fieldPruneFraction, 0f, 0.95f),
                FieldNeighborhoodRadiusVoxels = MathF.Max(0.25f, fieldNeighborhoodRadiusVoxels),
                FieldProtectAlpha = Math.Clamp(fieldProtectAlpha, 0f, 1f),
                EnableSplatInjection = enableSplatInjection,
                InjectionVoxelGrid = Math.Clamp(injectionVoxelGrid, 4, 256),
                InjectionTargetCount = Math.Max(0, injectionTargetCount),
                InjectionMaxFraction = Math.Clamp(injectionMaxFraction, 0f, 1f),
                InjectionPasses = Math.Clamp(injectionPasses, 1, 8),
                InjectionNeighborhoodRadiusVoxels = MathF.Max(0.25f, injectionNeighborhoodRadiusVoxels),
                InjectionDeficitThreshold = MathF.Max(0f, injectionDeficitThreshold),
                InjectionAlphaScale = Math.Clamp(injectionAlphaScale, 0f, 4f),
                InjectionScaleFactor = MathF.Max(0.01f, injectionScaleFactor),
                EnableSceneClassifier = enableSceneClassifier,
                SceneClassifierCoreFraction = Math.Clamp(sceneClassifierCoreFraction, 0.05f, 0.95f),
                EnableSurfaceAwareInjection = enableSurfaceAwareInjection,
                SurfaceAwareThicknessScale = Math.Clamp(surfaceAwareThicknessScale, 0.05f, 1.0f),
            }.NormalizeForBuild();

            if (!string.IsNullOrWhiteSpace(paretoPath) && targetProfile == SplatPackTargetProfile.Reference)
            {
                Console.Error.WriteLine("SplatPack reference target ignores Pareto sweeps to preserve a non-destructive baseline.");
            }
            else if (!string.IsNullOrWhiteSpace(paretoPath))
            {
                SplatPackParetoSweepOptions sweepOptions = new()
                {
                    MaxSplatBudgets = paretoBudgets,
                    MaxCoverageBoosts = paretoCoverageBoosts,
                    MaxImportanceErrors = paretoImportanceErrors,
                    DeviceBudget = deviceBudget,
                };
                IReadOnlyList<SplatPackParetoRow> rows = SplatPackParetoOptimizer.Sweep(stream, options, sweepOptions);
                SplatPackParetoOptimizer.WriteCsv(paretoPath, rows);
                SplatPackParetoRow? best = SplatPackParetoOptimizer.SelectBest(rows);
                if (best is not null)
                {
                    options = ApplyParetoSelection(options, best);
                }

                string selectionPath = string.IsNullOrWhiteSpace(paretoSelectionPath)
                    ? Path.ChangeExtension(paretoPath, ".selection.json")
                    : paretoSelectionPath;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(selectionPath)) ?? ".");
                File.WriteAllText(
                    selectionPath,
                    JsonSerializer.Serialize(
                        new
                        {
                            DeviceBudget = deviceBudget,
                            Best = best,
                            CandidateCount = rows.Count,
                            FitsDeviceBudgetCount = rows.Count(row => row.FitsDeviceBudget),
                            OutputPackageUsesBestSettings = best is not null,
                        },
                        JsonOptions));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"SplatPack Pareto sweep: rows={rows.Count}, fits={rows.Count(row => row.FitsDeviceBudget)}, budget={deviceBudget.Name}, output={paretoPath}, selection={selectionPath}"));
                if (best is not null)
                {
                    Console.WriteLine(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"SplatPack Pareto selected: fits={best.FitsDeviceBudget}, score={best.SelectionScore:0.###}, maxSplats={best.MaxSplats}, emitted={best.EmittedSplats}, retainedImportance={best.RetainedImportance:0.###}, packageMB={best.PackageMegabytes:0.##}, residentMB={best.EstimatedResidentMegabytes:0.##}, runtimeVisible={best.RuntimeVisibleSplats}"));
                }
            }

            SplatPackBuildResult result = SplatPackBuilder.BuildWithReport(stream, options);
            SplatPackPackage package = result.Package;
            SplatPackRotationOrder resolvedRotationOrder = Enum.Parse<SplatPackRotationOrder>(result.Report.RotationOrder);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
            SplatPackWriter.Write(outputPath, package, SplatPackWriter.FormatForOptions(options));
            if (writeReport)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".");
                File.WriteAllText(reportPath, JsonSerializer.Serialize(result.Report, JsonOptions));
            }

            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"SplatPack compiled: target={targetProfile}, format={result.Report.Options.OutputFormat}, sh={result.Report.Options.ShMode}, filter={result.Report.Options.FilterMode}, pruning={result.Report.Options.PruningMode}, sort={result.Report.Options.SortMode}, splats={package.Splats.Length}/{stream.Count}, chunks={package.Chunks.Length}, rotation={resolvedRotationOrder}, prunedInvalid={result.Report.Counts.PrunedInvalidSplats}, prunedOpacity={result.Report.Counts.PrunedLowOpacitySplats}, prunedContribution={result.Report.Counts.PrunedLowContributionSplats}, prunedOutlier={result.Report.Counts.PrunedSpatialOutlierSplats}, prunedBudget={result.Report.Counts.PrunedBudgetSplats}, classDrop=total/{result.ClassDrop.TotalDropped},free/{result.ClassDrop.DroppedFreeSpace},float/{result.ClassDrop.DroppedFloater},layer/{result.ClassDrop.DroppedLayerRisk},sspfProtected/{result.ClassDrop.ProtectedBySupport}, fieldPrune=enabled/{result.FieldPrune.Enabled},dropped/{result.FieldPrune.DroppedSplats},lowKept/{result.FieldPrune.LowestRetainedScore:0.####},highDrop/{result.FieldPrune.HighestDroppedScore:0.####},mean/{result.FieldPrune.MeanScore:0.####}, injection=enabled/{result.Injection.Enabled},added/{result.Injection.InjectedSplats},under/{result.Injection.UnderServedVoxels},dead/{result.Injection.DeadVoxels}, retainedImportance={result.Report.Optimization.RetainedImportanceFraction:0.###}, coverageBoost={result.Report.Optimization.MeanCoverageBoost:0.###}/{result.Report.Optimization.MaxCoverageBoost:0.###}, lod={result.Report.Optimization.LodTier0Splats}/{result.Report.Optimization.LodTier1Splats}/{result.Report.Optimization.LodTier2Splats}, hints=surface/{result.Report.RenderHints.SurfaceSplats},micro/{result.Report.RenderHints.MicroDetailSplats},broad/{result.Report.RenderHints.BroadSurfaceSplats},free/{result.Report.RenderHints.FreeSpaceSplats},float/{result.Report.RenderHints.FloaterSplats},fg/{result.Report.RenderHints.ForegroundSplats},layer/{result.Report.RenderHints.LayerRiskSplats}, layerError=macro/{result.Report.LayerStats.MacroErrorProxy:0.###},micro/{result.Report.LayerStats.MicroErrorProxy:0.###},free/{result.Report.LayerStats.FreeSpaceFraction:0.###}, shellRepair=enabled/{result.Report.ShellSurfaceRepair.Enabled},reinsert/{result.Report.ShellSurfaceRepair.ReinsertedSplats},residual/{result.Report.ShellSurfaceRepair.ResidualBefore:0.###}->{result.Report.ShellSurfaceRepair.ResidualAfter:0.###},weak/{result.Report.ShellSurfaceRepair.WeakShellCellsBefore}->{result.Report.ShellSurfaceRepair.WeakShellCellsAfter}, spherical=mode/{result.Report.SphericalInformation.Mode},coverage/{result.Report.SphericalInformation.CoverageResidual:0.###},surface/{result.Report.SphericalInformation.SurfaceResidual:0.###},micro/{result.Report.SphericalInformation.MicroDetailResidual:0.###},highBand/{result.Report.SphericalInformation.HighBandResidual:0.###},particle/{result.Report.SphericalInformation.ParticleRisk:0.###},blur/{result.Report.SphericalInformation.BlurRisk:0.###},eta/{result.Report.SphericalInformation.AngularFootprintMean:0.###},sspf/{result.Report.SphericalInformation.SupportFieldRecords},angularDeficit/{result.Report.SphericalInformation.AngularDeficit:0.###}, quantized={result.Report.Optimization.QuantizedSplats}, axisClamped={result.Report.Counts.AxisClampedSplats}, axisRatioClamped={result.Report.Counts.AxisRatioClampedSplats}, axisCap={result.Report.Options.AxisLengthCap:0.######}, axisRatioCap={result.Report.Options.MaxAxisRatio:0.###}, output={outputPath}, report={(writeReport ? reportPath : "off")}"));
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

    private static SplatPackOutputFormat ReadOutputFormatOption(string[] args, string name, SplatPackOutputFormat fallback)
    {
        string value = ReadStringOption(args, name, string.Empty).ToLowerInvariant();
        return value switch
        {
            "" => fallback,
            "auto" => SplatPackOutputFormat.Auto,
            "v4" or "packed-v4" => SplatPackOutputFormat.PackedV4,
            "v5-reference" or "reference-v5" or "reference" => SplatPackOutputFormat.ReferenceFloatV5,
            "v6-research" or "research-v6" or "v6" => SplatPackOutputFormat.ResearchSectionedV6,
            _ => throw new ArgumentException($"Unsupported {name}: {value}"),
        };
    }

    private static SplatPackShMode ReadShModeOption(string[] args, string name, SplatPackShMode fallback)
    {
        string value = ReadStringOption(args, name, string.Empty).ToLowerInvariant();
        return value switch
        {
            "" => fallback,
            "dc" => SplatPackShMode.Dc,
            "sh1" => SplatPackShMode.Sh1,
            "sh3" => SplatPackShMode.Sh3,
            _ => throw new ArgumentException($"Unsupported {name}: {value}"),
        };
    }

    private static SplatPackFilterMode ReadFilterModeOption(string[] args, string name, SplatPackFilterMode fallback)
    {
        string value = ReadStringOption(args, name, string.Empty).ToLowerInvariant();
        return value switch
        {
            "" => fallback,
            "none" => SplatPackFilterMode.None,
            "mip2d" or "mip-2d" => SplatPackFilterMode.Mip2D,
            "analytic-pixel" or "analyticpixel" => SplatPackFilterMode.AnalyticPixel,
            _ => throw new ArgumentException($"Unsupported {name}: {value}"),
        };
    }

    private static SplatPackPruningMode ReadPruningModeOption(string[] args, string name, SplatPackPruningMode fallback)
    {
        string value = ReadStringOption(args, name, string.Empty).ToLowerInvariant();
        return value switch
        {
            "" => fallback,
            "heuristic" => SplatPackPruningMode.Heuristic,
            "research-xr-sensitivity" or "sensitivity" => SplatPackPruningMode.ResearchXrSensitivity,
            _ => throw new ArgumentException($"Unsupported {name}: {value}"),
        };
    }

    private static SplatPackSortMode ReadSortModeOption(string[] args, string name, SplatPackSortMode fallback)
    {
        string value = ReadStringOption(args, name, string.Empty).ToLowerInvariant();
        return value switch
        {
            "" => fallback,
            "depth-bins" or "depthbins" => SplatPackSortMode.DepthBins,
            "stable-radix-per-eye" or "stable" => SplatPackSortMode.StableRadixPerEye,
            _ => throw new ArgumentException($"Unsupported {name}: {value}"),
        };
    }

    private static SplatPackArtifactRepairMode ReadArtifactRepairModeOption(string[] args, string name, SplatPackArtifactRepairMode fallback)
    {
        string value = ReadStringOption(args, name, string.Empty).ToLowerInvariant();
        return value switch
        {
            "" => fallback,
            "none" => SplatPackArtifactRepairMode.None,
            "gaussian-pullback-v1" or "gaussianpullbackv1" or "pullback-v1" => SplatPackArtifactRepairMode.GaussianPullbackV1,
            "gaussian-pullback-v2" or "gaussianpullbackv2" or "pullback-v2" => SplatPackArtifactRepairMode.GaussianPullbackV2,
            "shell-surface-v1" or "shellsurfacev1" or "view-shell-v1" or "viewshellv1" or "surface-shell-v1" or "surfaceshellv1" => SplatPackArtifactRepairMode.ShellSurfaceV1,
            _ => throw new ArgumentException($"Unsupported {name}: {value}"),
        };
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

    private static int[] ReadIntListOption(string[] args, string name, int[] fallback)
    {
        string text = ReadStringOption(args, name, string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0)
            .Where(value => value > 0)
            .DefaultIfEmpty(fallback.Length > 0 ? fallback[0] : 1)
            .ToArray();
    }

    private static float[] ReadFloatListOption(string[] args, string name, float[] fallback)
    {
        string text = ReadStringOption(args, name, string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => float.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : float.NaN)
            .Where(float.IsFinite)
            .DefaultIfEmpty(fallback.Length > 0 ? fallback[0] : 0f)
            .ToArray();
    }

    private static SplatPackPullbackWeights ReadPullbackWeightsOption(string[] args, string name, SplatPackPullbackWeights fallback)
    {
        string text = ReadStringOption(args, name, string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        float coverage = fallback.Coverage;
        float edge = fallback.Edge;
        float texture = fallback.Texture;
        float layer = fallback.Layer;
        float stereo = fallback.Stereo;
        foreach (string item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = item.Split('=', 2, StringSplitOptions.TrimEntries);
            string key = pair[0].ToLowerInvariant();
            float value = pair.Length == 2 && float.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? MathF.Max(0f, parsed)
                : 1f;
            switch (key)
            {
                case "coverage":
                case "cov":
                    coverage = value;
                    break;
                case "edge":
                    edge = value;
                    break;
                case "texture":
                case "tex":
                    texture = value;
                    break;
                case "layer":
                    layer = value;
                    break;
                case "stereo":
                    stereo = value;
                    break;
            }
        }

        return new SplatPackPullbackWeights(coverage, edge, texture, layer, stereo);
    }

    private static int[] DefaultParetoBudgets(int maxSplats)
    {
        int budget = Math.Max(1, maxSplats);
        return new[]
        {
            Math.Max(1, budget / 2),
            Math.Max(1, budget * 3 / 4),
            budget,
        }.Distinct().Order().ToArray();
    }

    private static SplatPackBuildOptions ApplyParetoSelection(
        SplatPackBuildOptions source,
        SplatPackParetoRow selected)
    {
        return new SplatPackBuildOptions
        {
            ChunkSize = source.ChunkSize,
            RotationOrder = source.RotationOrder,
            TargetProfile = source.TargetProfile,
            OutputFormat = source.OutputFormat,
            ShMode = source.ShMode,
            FilterMode = source.FilterMode,
            PruningMode = source.PruningMode,
            SortMode = source.SortMode,
            MaxSplats = selected.MaxSplats,
            OpacityPruneThreshold = source.OpacityPruneThreshold,
            MaxAxisLength = source.MaxAxisLength,
            MaxAxisLengthPercentile = source.MaxAxisLengthPercentile,
            MaxAxisLengthMultiplier = source.MaxAxisLengthMultiplier,
            MaxAxisRatio = source.MaxAxisRatio,
            ContributionAxisPower = source.ContributionAxisPower,
            ContributionPruneThreshold = source.ContributionPruneThreshold,
            EnableViewSampledImportance = source.EnableViewSampledImportance,
            ViewSampleSource = source.ViewSampleSource,
            ViewSamples = source.ViewSamples,
            MaxImportanceError = selected.MaxImportanceError,
            EnableCoverageCompensation = source.EnableCoverageCompensation,
            MaxCoverageBoost = selected.MaxCoverageBoost,
            CoverageAxisPower = source.CoverageAxisPower,
            FarLodKeepFraction = source.FarLodKeepFraction,
            MidLodKeepFraction = source.MidLodKeepFraction,
            EnableAttributeQuantization = source.EnableAttributeQuantization,
            PositionQuantizationBits = source.PositionQuantizationBits,
            AxisQuantizationBits = source.AxisQuantizationBits,
            ColorQuantizationBits = source.ColorQuantizationBits,
            OpacityQuantizationBits = source.OpacityQuantizationBits,
            EnableStreamingLayout = source.EnableStreamingLayout,
            EnableSpatialOutlierPrune = source.EnableSpatialOutlierPrune,
            SpatialOutlierGridResolution = source.SpatialOutlierGridResolution,
            SpatialOutlierMinNeighbors = source.SpatialOutlierMinNeighbors,
            SpatialOutlierKeepImportance = source.SpatialOutlierKeepImportance,
            ArtifactRepairMode = source.ArtifactRepairMode,
            ProbeResolution = source.ProbeResolution,
            ProbeViewCount = source.ProbeViewCount,
            ProbeViewSource = source.ProbeViewSource,
            ProbeViewSamples = source.ProbeViewSamples,
            RepairBudgetRatio = source.RepairBudgetRatio,
            RepairIterations = source.RepairIterations,
            ProbeCellSize = source.ProbeCellSize,
            ProbeFovYDegrees = source.ProbeFovYDegrees,
            ProbeAspect = source.ProbeAspect,
            ProbeIpdMeters = source.ProbeIpdMeters,
            ProbeLayerBins = source.ProbeLayerBins,
            PullbackWeights = source.PullbackWeights,
            DropFreeSpaceClass = source.DropFreeSpaceClass,
            DropFloaterClass = source.DropFloaterClass,
            DropLayerRiskClass = source.DropLayerRiskClass,
            ClassDropSupportProtect = source.ClassDropSupportProtect,
            EnableFieldAwarePruning = source.EnableFieldAwarePruning,
            FieldVoxelGrid = source.FieldVoxelGrid,
            FieldPruneFraction = source.FieldPruneFraction,
            FieldNeighborhoodRadiusVoxels = source.FieldNeighborhoodRadiusVoxels,
            FieldProtectAlpha = source.FieldProtectAlpha,
        }.NormalizeForBuild();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: SplatPackCompiler input.ply output.splatpack [--target reference|standalone-xr] [--format v4|v5-reference|v6-research] [--sh dc|sh1|sh3] [--filter none|mip2d|analytic-pixel] [--pruning heuristic|research-xr-sensitivity] [--sort depth-bins|stable-radix-per-eye] [--artifact-repair none|gaussian-pullback-v1|gaussian-pullback-v2|shell-surface-v1] [--probe-resolution 256] [--probe-cell-size 4] [--probe-view-count 16] [--probe-viewset path.json] [--probe-fov-y 70] [--probe-aspect 1] [--probe-ipd 0.064] [--probe-layer-bins 2] [--repair-budget-ratio 0.12] [--repair-iterations 3] [--pullback-weight coverage=1,edge=1,texture=1,layer=1,stereo=1] [--max-splats 120000] [--chunk-size 4096] [--rotation-order auto|xyzw|wxyz] [--opacity-prune 0.002] [--contribution-prune 0.0015] [--max-axis-percentile 90] [--max-axis-multiplier 1.1] [--max-axis-length meters] [--max-axis-ratio 8] [--contribution-axis-power 0.5] [--viewset path.json] [--max-importance-error 0.03] [--max-coverage-boost 2.4] [--coverage-axis-power 1.0] [--far-lod-keep 0.35] [--mid-lod-keep 0.68] [--position-bits 16] [--axis-bits 14] [--color-bits 8] [--opacity-bits 10] [--spatial-outlier-grid 0] [--spatial-outlier-min-neighbors 5] [--spatial-outlier-keep-importance 0.55] [--pareto path.csv] [--pareto-budgets 60000,90000,120000] [--pareto-coverage 1,1.4,2.4] [--pareto-errors 0,0.03] [--device-budget path.json] [--pareto-selection path.json] [--no-view-importance] [--no-coverage-compensation] [--no-attribute-quantization] [--no-streaming-layout] [--no-spatial-outlier-prune] [--drop-free-class] [--drop-floater-class] [--drop-layer-class] [--drop-haze-classes] [--class-drop-support-protect 0.0] [--field-prune] [--field-voxel-grid 64] [--field-prune-fraction 0.10] [--field-neighborhood-voxels 1.5] [--field-protect-alpha 0.5] [--splat-injection] [--injection-voxel-grid 64] [--injection-target-count 0] [--injection-max-fraction 0.10] [--injection-passes 1] [--injection-neighborhood-voxels 1.5] [--injection-deficit-threshold 0.5] [--injection-alpha-scale 0.6] [--injection-scale-factor 1.0] [--report path] [--no-report]");
    }
}
