param(
    [ValidateSet("Smoke", "WakuCc0", "Medium")]
    [string]$Set = "Smoke",

    [string]$OutputRoot = "SplatPack/experiments/data",

    [switch]$Compile,

    [int]$ChunkSize = 4096
)

$ErrorActionPreference = "Stop"

function Download-File {
    param(
        [string]$Url,
        [string]$Path
    )

    $dir = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force $dir | Out-Null

    if (Test-Path $Path) {
        $existing = Get-Item $Path
        if ($existing.Length -gt 0) {
            Write-Host "skip existing $Path"
            return
        }

        Write-Host "retry incomplete $Path"
        Remove-Item -LiteralPath $Path -Force
    }

    Write-Host "download $Url"
    Invoke-WebRequest -Uri $Url -OutFile $Path
}

function Compile-SplatPack {
    param(
        [string]$InputPath,
        [string]$OutputPath
    )

    Write-Host "compile $InputPath"
    dotnet run --project SplatPack/compiler/SplatPack.Compiler.csproj -- `
        $InputPath `
        $OutputPath `
        --chunk-size $ChunkSize
}

$assets = @()

if ($Set -eq "Smoke") {
    $assets += @{
        Name = "j0n45_point_cloud"
        Url = "https://huggingface.co/J0N45/Gaussian-Splats/resolve/d6a58cf8fd2f889dea24fdf8eb6604204c058968/point_cloud.ply?download=true"
        RelativePath = "smoke/j0n45_point_cloud.ply"
    }
}

if ($Set -eq "WakuCc0") {
    $assets += @{
        Name = "wakufactory_kadan1"
        Url = "https://wakufactory.sakura.ne.jp/assets/ply/20240324_kadan1.ply"
        RelativePath = "wakufactory/kadan1.ply"
    }
    $assets += @{
        Name = "wakufactory_kaeru"
        Url = "https://wakufactory.sakura.ne.jp/assets/ply/20240324_kaeru.ply"
        RelativePath = "wakufactory/kaeru.ply"
    }
    $assets += @{
        Name = "wakufactory_kitune_trimmed"
        Url = "https://www.wakufactory.jp/wxr/splats/data/kitune1.ply"
        RelativePath = "wakufactory/kitune_trimmed.ply"
    }
    $assets += @{
        Name = "wakufactory_sakura_trimmed"
        Url = "https://www.wakufactory.jp/wxr/splats/data/sakura1.ply"
        RelativePath = "wakufactory/sakura_trimmed.ply"
    }
}

if ($Set -eq "Medium") {
    $assets += @{
        Name = "camenduru_train"
        Url = "https://huggingface.co/camenduru/gaussian-splatting/resolve/main/train/point_cloud/iteration_30000/point_cloud.ply"
        RelativePath = "medium/camenduru_train_point_cloud.ply"
    }
}

foreach ($asset in $assets) {
    $plyPath = Join-Path $OutputRoot $asset.RelativePath
    Download-File -Url $asset.Url -Path $plyPath

    if ($Compile) {
        $splatPackPath = [System.IO.Path]::ChangeExtension($plyPath, ".splatpack")
        Compile-SplatPack -InputPath $plyPath -OutputPath $splatPackPath
    }
}
