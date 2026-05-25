#!/usr/bin/env bash
# Runs on the pod after files are uploaded. Render all + metrics + log.
set -uo pipefail
cd /workspace/splatpack
LOG=run_$(date +%Y%m%d_%H%M%S).log
exec > >(tee -a "$LOG") 2>&1

echo "=== start $(date) ==="
df -h / | tail -1
nvidia-smi --query-gpu=name,memory.free --format=csv,noheader

SCENES=(bonsai counter kitchen room kadan1 j0n45)
METHODS=(baseline matched injection combined)

mkdir -p renders

for S in "${SCENES[@]}"; do
  VIEWSET="viewsets/$S-orbit16.json"
  if [ ! -f "$VIEWSET" ]; then echo "SKIP $S: no viewset"; continue; fi

  # Vanilla render
  PLY="plys/$S.ply"
  if [ -f "$PLY" ]; then
    OUT="renders/$S/vanilla"
    if [ -f "$OUT/view_015.png" ]; then
      echo "[$S/vanilla] cached, skip"
    else
      mkdir -p "$OUT"
      echo "[$S/vanilla] rendering..."
      python3 scripts/render_orbit_gsplat.py --asset "$PLY" --viewset "$VIEWSET" --out "$OUT" --image-size 256 --sh-degree 3 || echo "[$S/vanilla] FAILED"
    fi
  else
    echo "[$S] no vanilla PLY; vanilla render skipped"
  fi

  # Each SplatPack method
  for M in "${METHODS[@]}"; do
    ASSET="assets/$S.v6-$M.splatpack"
    if [ "$M" = "baseline" ]; then OUTNAME="baseline_120k"; else OUTNAME="$M"; fi
    OUT="renders/$S/$OUTNAME"
    if [ -f "$OUT/view_015.png" ]; then
      echo "[$S/$OUTNAME] cached, skip"
      continue
    fi
    if [ ! -f "$ASSET" ]; then
      echo "[$S/$OUTNAME] missing asset; skip"
      continue
    fi
    mkdir -p "$OUT"
    echo "[$S/$OUTNAME] rendering..."
    python3 scripts/render_orbit_gsplat.py --asset "$ASSET" --viewset "$VIEWSET" --out "$OUT" --image-size 256 --sh-degree 3 || echo "[$S/$OUTNAME] FAILED"
  done
done

echo "=== rendering done. computing metrics... ==="
python3 scripts/metric_compare.py --root renders --reference vanilla --out image_metrics.csv

echo "=== final state ==="
ls renders/
du -sh renders/
head image_metrics.csv
echo "=== end $(date) ==="
