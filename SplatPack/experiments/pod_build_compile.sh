#!/usr/bin/env bash
# Runs ON THE POD. Builds the SplatPack compiler, downloads Mip-NeRF PLYs,
# compiles the 4 method variants per scene. Idempotent: skips finished steps.
set -uo pipefail
export PATH="/root/.dotnet:$PATH"
cd /root/build

LOG=build_$(date +%Y%m%d_%H%M%S).log
exec > >(tee -a "$LOG") 2>&1
echo "=== pod build start $(date) ==="

# --- 1. Extract + build compiler ---
if [ ! -f bin/Release/net8.0/SplatPackCompiler.dll ]; then
  tar xzf compiler_src.tgz
  echo "building compiler..."
  dotnet build compiler/SplatPack.Compiler.csproj -c Release -o bin/Release/net8.0 2>&1 | tail -5
fi
COMPILER="dotnet bin/Release/net8.0/SplatPackCompiler.dll"
echo "compiler ready"

# --- 2. Download + extract Mip-NeRF pretrained PLYs ---
mkdir -p plys
if [ ! -f plys/bonsai.ply ]; then
  echo "downloading Mip-NeRF pretrained models (14GB)..."
  wget -q -O /tmp/models.zip https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/datasets/pretrained/models.zip
  echo "download done; extracting 4 scenes..."
  for S in bonsai counter kitchen room; do
    # path inside zip: <scene>/point_cloud/iteration_30000/point_cloud.ply
    unzip -o -j /tmp/models.zip "$S/point_cloud/iteration_30000/point_cloud.ply" -d plys/ >/dev/null 2>&1 \
      && mv plys/point_cloud.ply "plys/$S.ply" \
      && echo "  extracted $S.ply" || echo "  FAILED extract $S (check zip path)"
  done
  rm -f /tmp/models.zip
  echo "zip removed; disk:"
  df -h / | tail -1
fi
ls -la plys/

# --- 3. Compile 4 method variants for each scene ---
mkdir -p assets
for S in bonsai counter kitchen room; do
  PLY="plys/$S.ply"
  [ -f "$PLY" ] || { echo "SKIP $S: no PLY"; continue; }
  COMMON="--format v6-research --max-splats 120000"
  declare -A FLAGS=(
    [baseline]=""
    [matched]="--field-prune"
    [injection]="--splat-injection"
    [combined]="--field-prune --splat-injection"
  )
  for M in baseline matched injection combined; do
    OUT="assets/$S.v6-$M.splatpack"
    if [ -f "$OUT" ]; then echo "[$S/$M] cached"; continue; fi
    echo "[$S/$M] compiling..."
    $COMPILER "$PLY" "$OUT" $COMMON ${FLAGS[$M]} --no-report 2>&1 | grep -oE "splats=[0-9]+/[0-9]+" | head -1
  done
done

echo "=== compiled assets ==="
ls -la assets/
echo "=== pod build done $(date) ==="
