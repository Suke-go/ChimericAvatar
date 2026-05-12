param(
    [string[]]$ReportPath,

    [string]$SearchRoot = "SplatPack/experiments/data",

    [string]$OutputPath = "SplatPack/experiments/results/report-metrics.csv",

    [switch]$PassThru
)

$ErrorActionPreference = "Stop"

function Resolve-ReportFiles {
    param(
        [string[]]$Path,
        [string]$Root
    )

    $files = @()

    if ($Path -and $Path.Count -gt 0) {
        foreach ($entry in $Path) {
            if (Test-Path -LiteralPath $entry -PathType Container) {
                $files += Get-ChildItem -LiteralPath $entry -Recurse -File -Filter "*.splatpack.report.json"
                continue
            }

            if (Test-Path -LiteralPath $entry -PathType Leaf) {
                $files += Get-Item -LiteralPath $entry
                continue
            }

            $matches = Get-ChildItem -Path $entry -File -ErrorAction SilentlyContinue
            if ($matches) {
                $files += $matches
                continue
            }

            throw "Report path not found: $entry"
        }
    }
    elseif (Test-Path -LiteralPath $Root -PathType Container) {
        $files += Get-ChildItem -LiteralPath $Root -Recurse -File -Filter "*.splatpack.report.json"
    }
    else {
        throw "Search root not found: $Root"
    }

    return $files | Sort-Object FullName -Unique
}

function Get-FileLengthOrNull {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        return (Get-Item -LiteralPath $Path).Length
    }

    return $null
}

function Get-Percent {
    param(
        [double]$Part,
        [double]$Whole
    )

    if ($Whole -le 0) {
        return $null
    }

    return [Math]::Round(($Part / $Whole) * 100.0, 4)
}

$reportFiles = Resolve-ReportFiles -Path $ReportPath -Root $SearchRoot
if (-not $reportFiles -or $reportFiles.Count -eq 0) {
    throw "No .splatpack.report.json files found."
}

$rows = @(foreach ($file in $reportFiles) {
    $report = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    $packagePath = $file.FullName -replace '\.report\.json$', ''
    $plyPath = [System.IO.Path]::ChangeExtension($packagePath, ".ply")
    $inputSplats = [double]$report.Counts.InputSplats
    $emittedSplats = [double]$report.Counts.EmittedSplats

    [pscustomobject]@{
        asset = [System.IO.Path]::GetFileNameWithoutExtension($packagePath)
        report_path = $file.FullName
        package_path = $packagePath
        ply_path = $(if (Test-Path -LiteralPath $plyPath -PathType Leaf) { $plyPath } else { $null })
        target_profile = $report.TargetProfile
        rotation_order = $report.RotationOrder
        input_splats = $report.Counts.InputSplats
        emitted_splats = $report.Counts.EmittedSplats
        emitted_pct = Get-Percent -Part $emittedSplats -Whole $inputSplats
        pruned_invalid_splats = $report.Counts.PrunedInvalidSplats
        pruned_low_opacity_splats = $report.Counts.PrunedLowOpacitySplats
        pruned_budget_splats = $report.Counts.PrunedBudgetSplats
        axis_clamped_splats = $report.Counts.AxisClampedSplats
        axis_clamped_pct_of_input = Get-Percent -Part ([double]$report.Counts.AxisClampedSplats) -Whole $inputSplats
        axis_ratio_clamped_splats = $report.Counts.AxisRatioClampedSplats
        axis_ratio_clamped_pct_of_input = Get-Percent -Part ([double]$report.Counts.AxisRatioClampedSplats) -Whole $inputSplats
        chunk_size = $report.Options.ChunkSize
        chunk_count = $report.Chunks.Count
        chunk_min_splats = $report.Chunks.MinSplats
        chunk_max_splats = $report.Chunks.MaxSplats
        chunk_mean_splats = $report.Chunks.MeanSplats
        axis_length_cap = $report.Options.AxisLengthCap
        max_axis_ratio = $report.Options.MaxAxisRatio
        raw_axis_length_p99 = $report.RawMaxAxisLength.P99
        raw_axis_length_max = $report.RawMaxAxisLength.Max
        emitted_axis_length_p99 = $report.EmittedMaxAxisLength.P99
        emitted_axis_length_max = $report.EmittedMaxAxisLength.Max
        raw_axis_ratio_p99 = $report.RawAxisRatio.P99
        raw_axis_ratio_max = $report.RawAxisRatio.Max
        emitted_axis_ratio_p99 = $report.EmittedAxisRatio.P99
        emitted_axis_ratio_max = $report.EmittedAxisRatio.Max
        splat_stride_bytes = $report.Memory.SplatStrideBytes
        chunk_stride_bytes = $report.Memory.ChunkStrideBytes
        splat_bytes = $report.Memory.SplatBytes
        chunk_bytes = $report.Memory.ChunkBytes
        total_uncompressed_bytes = $report.Memory.TotalUncompressedBytes
        total_uncompressed_mb = [Math]::Round(([double]$report.Memory.TotalUncompressedBytes / 1MB), 3)
        package_bytes = Get-FileLengthOrNull -Path $packagePath
        ply_bytes = Get-FileLengthOrNull -Path $plyPath
        runtime_vertices = $report.Memory.RuntimeVertices
        runtime_vertices_per_splat = $(if ($emittedSplats -gt 0) { [Math]::Round(([double]$report.Memory.RuntimeVertices / $emittedSplats), 4) } else { $null })
        bytes_per_emitted_splat = $(if ($emittedSplats -gt 0) { [Math]::Round(([double]$report.Memory.TotalUncompressedBytes / $emittedSplats), 4) } else { $null })
    }
})

$outputDir = Split-Path -Parent $OutputPath
if ($outputDir) {
    New-Item -ItemType Directory -Force $outputDir | Out-Null
}

$rows | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding UTF8
Write-Host "wrote $($rows.Count) report row(s) to $OutputPath"

if ($PassThru) {
    $rows
}
