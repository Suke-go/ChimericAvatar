param(
    [string]$Destination = "SplatPack/experiments/external"
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force $Destination | Out-Null

$repos = @(
    "https://github.com/aras-p/UnityGaussianSplatting.git",
    "https://github.com/graphdeco-inria/gaussian-splatting.git",
    "https://github.com/playcanvas/supersplat.git",
    "https://github.com/playcanvas/supersplat-viewer.git",
    "https://github.com/antimatter15/splat.git"
)

foreach ($repo in $repos) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($repo)
    $target = Join-Path $Destination $name
    if (Test-Path $target) {
        Write-Host "skip existing $target"
        continue
    }

    git clone --depth 1 $repo $target
}
