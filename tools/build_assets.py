#!/usr/bin/env python3
import argparse
from pathlib import Path
from hypercube.pack import build
from hypercube.disc import build as disc

p = argparse.ArgumentParser(description="Build deterministic or imported static-mesh validation assets")
p.add_argument("--input",type=Path,help="Optional OBJ, glTF or GLB in the documented validation envelope")
p.add_argument("--output",type=Path,default=Path("out/assets"))
p.add_argument("--resolution",type=int,default=48)
a = p.parse_args()
if not 8 <= a.resolution <= 256:
    p.error("resolution must be 8..256")
a.output.mkdir(parents=True,exist_ok=True)
r = build(a.output/"world.hcp",a.output/"report.json",a.input,a.resolution)
disc(a.output/"world.iso",a.output/"world.hcp")
print("Assets:",r["pack"],"source triangles",r["source_triangles"],"LOD triangles",[v["triangles"] for v in r["lods"]])
