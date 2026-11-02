"""Bake a vertex animation texture (VAT) for GPU-instanced crowd meshes.

Usage (headless)::

    blender -b crowd.blend -P Tools/Blender/dd_vat_bake.py -- --object ST_Shared_CrowdA --start 1 --end 30
        --out Assets/DunkDuel/Art/Crowd --name crowd_cheer [--normals] [--max-width 2048] [--space stage|character]
        [--no-uv2] [--save]

For every frame in [start, end] the evaluated mesh (armature, shape keys, deform modifiers) is sampled.
Positions are stored in object space, converted to Unity axes (``--space``, default ``stage``) and normalized
to the animation's bounds; normals are stored as ``n * 0.5 + 0.5``. Layout: one column per vertex and one row
per frame; when the vertex count exceeds ``--max-width`` each frame wraps over ``rowsPerFrame`` rows.

Outputs, written to ``--out``:

* ``<name>_pos.exr`` (RGBA float, A = 1) and optionally ``<name>_nrm.exr``;
* ``<name>.vat.json`` sidecar: frames, vertexCount, fps, textureWidth/Height, rowsPerFrame, boundsMin/Max (Unity
  axes), space, files. The Unity shader decodes ``pos = lerp(boundsMin, boundsMax, tex.rgb)``;
* a UV map named ``VAT_ID`` added to the source mesh (second UV set -> Unity ``uv2``) holding each vertex's texel
  center at frame 0: ``uv2 = ((v % W + 0.5) / W, (v // W + 0.5) / H)``. The shader samples
  ``float2(uv2.x, uv2.y + frame * rowsPerFrame / H)``. Export the mesh after baking (``--save``).

Topology must not change across frames (deform-only modifiers).
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from dataclasses import asdict, dataclass
from typing import List, Optional, Sequence, Tuple

import bpy  # type: ignore[import-not-found]

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import dd_bpy  # noqa: E402
import dd_conventions as dc  # noqa: E402

try:  # numpy ships with Blender; keep a pure-Python fallback for odd builds.
    import numpy as np  # type: ignore[import-not-found]
except ImportError:  # pragma: no cover
    np = None  # type: ignore[assignment]

UV_LAYER_NAME = "VAT_ID"


@dataclass
class VatInfo:
    name: str
    object: str
    frames: int
    frameStart: int
    frameEnd: int
    vertexCount: int
    fps: float
    textureWidth: int
    textureHeight: int
    rowsPerFrame: int
    boundsMin: List[float]
    boundsMax: List[float]
    space: str
    positionTexture: str
    normalTexture: Optional[str]
    encoding: str = "position = lerp(boundsMin, boundsMax, rgb); normal = rgb * 2 - 1"
    uvLayer: str = UV_LAYER_NAME


layout = dc.vat_layout  # (width, height, rows_per_frame); kept here for callers of the old name


def _convert(space: str, x: float, y: float, z: float) -> Tuple[float, float, float]:
    return dc.blender_to_unity_character(x, y, z) if space == "character" else dc.blender_to_unity_stage(x, y, z)


def sample_frames(obj: "bpy.types.Object", start: int, end: int, space: str) -> Tuple[List[List[Tuple[float, float, float]]], List[List[Tuple[float, float, float]]]]:
    """Per frame: vertex positions and normals of the evaluated mesh, in Unity axes."""
    scene = bpy.context.scene
    positions: List[List[Tuple[float, float, float]]] = []
    normals: List[List[Tuple[float, float, float]]] = []
    count: Optional[int] = None
    for frame in range(start, end + 1):
        scene.frame_set(frame)
        depsgraph = bpy.context.evaluated_depsgraph_get()
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh()
        try:
            n = len(mesh.vertices)
            if count is None:
                count = n
            elif n != count:
                raise RuntimeError(f"{obj.name}: vertex count changed from {count} to {n} at frame {frame} (use deform-only modifiers)")
            positions.append([_convert(space, *v.co) for v in mesh.vertices])
            normals.append([_convert(space, *v.normal) for v in mesh.vertices])
        finally:
            evaluated.to_mesh_clear()
    return positions, normals


def _write_exr(path: str, width: int, height: int, rgba: Sequence[float]) -> None:
    img = bpy.data.images.new(os.path.basename(path), width=width, height=height, alpha=True, float_buffer=True)
    try:
        img.colorspace_settings.name = "Non-Color"
        if np is not None:
            img.pixels.foreach_set(np.asarray(rgba, dtype=np.float32))
        else:
            img.pixels[:] = list(rgba)
        img.filepath_raw = path
        img.file_format = "OPEN_EXR"
        img.save()
    finally:
        bpy.data.images.remove(img)


def bake(obj: "bpy.types.Object", start: int, end: int, out_dir: str, name: str, *, normals: bool = False,
         max_width: int = 2048, space: str = "stage", write_uv2: bool = True) -> VatInfo:
    if obj.type != "MESH":
        raise TypeError(f"{obj.name} is not a mesh")
    if end < start:
        raise ValueError("end frame before start frame")
    frame_before = bpy.context.scene.frame_current
    try:
        pos, nrm = sample_frames(obj, start, end, space)
    finally:
        bpy.context.scene.frame_set(frame_before)
    frames = len(pos)
    vcount = len(pos[0])
    width, height, rows = layout(vcount, frames, max_width)

    lo = [min(p[a] for f in pos for p in f) for a in range(3)]
    hi = [max(p[a] for f in pos for p in f) for a in range(3)]
    span = [max(hi[a] - lo[a], 1e-8) for a in range(3)]

    def texel(f: int, v: int) -> int:
        x, y = dc.vat_texel(v, f, width, rows)
        return (y * width + x) * 4

    pos_px = [0.0] * (width * height * 4)
    nrm_px = [0.0] * (width * height * 4) if normals else None
    for f in range(frames):
        for v in range(vcount):
            i = texel(f, v)
            p = pos[f][v]
            pos_px[i:i + 4] = [(p[0] - lo[0]) / span[0], (p[1] - lo[1]) / span[1], (p[2] - lo[2]) / span[2], 1.0]
            if nrm_px is not None:
                n = nrm[f][v]
                nrm_px[i:i + 4] = [n[0] * 0.5 + 0.5, n[1] * 0.5 + 0.5, n[2] * 0.5 + 0.5, 1.0]

    os.makedirs(out_dir, exist_ok=True)
    pos_file = f"{name}_pos.exr"
    _write_exr(os.path.join(out_dir, pos_file), width, height, pos_px)
    nrm_file = None
    if nrm_px is not None:
        nrm_file = f"{name}_nrm.exr"
        _write_exr(os.path.join(out_dir, nrm_file), width, height, nrm_px)

    if write_uv2:
        write_vertex_id_uvs(obj, width, height)

    scene = bpy.context.scene
    info = VatInfo(
        name=name, object=obj.name, frames=frames, frameStart=start, frameEnd=end, vertexCount=vcount,
        fps=scene.render.fps / scene.render.fps_base, textureWidth=width, textureHeight=height, rowsPerFrame=rows,
        boundsMin=[round(c, 6) for c in lo], boundsMax=[round(c, 6) for c in hi], space=space,
        positionTexture=pos_file, normalTexture=nrm_file,
    )
    with open(os.path.join(out_dir, f"{name}.vat.json"), "w", encoding="utf-8") as fh:
        json.dump(asdict(info), fh, indent=2, sort_keys=True)
    return info


def write_vertex_id_uvs(obj: "bpy.types.Object", width: int, height: int) -> None:
    """Add (or overwrite) the VAT_ID UV map: each loop gets the texel center of its vertex at frame 0."""
    mesh = obj.data
    if len(mesh.uv_layers) == 0:
        mesh.uv_layers.new(name="UVMap")  # keep VAT_ID as the second set (Unity uv2)
    layer = mesh.uv_layers.get(UV_LAYER_NAME) or mesh.uv_layers.new(name=UV_LAYER_NAME)
    for loop in mesh.loops:
        layer.data[loop.index].uv = dc.vat_uv2(loop.vertex_index, width, height)


def main(argv: Sequence[str]) -> int:
    p = argparse.ArgumentParser(prog="dd_vat_bake", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--object", required=True)
    p.add_argument("--start", type=int, help="first frame (default: scene start)")
    p.add_argument("--end", type=int, help="last frame (default: scene end)")
    p.add_argument("--out", required=True, help="output folder (e.g. Assets/DunkDuel/Art/Crowd)")
    p.add_argument("--name", help="texture/sidecar base name (default: object name)")
    p.add_argument("--normals", action="store_true", help="also bake a normal texture")
    p.add_argument("--max-width", type=int, default=2048)
    p.add_argument("--space", choices=("stage", "character"), default="stage")
    p.add_argument("--no-uv2", action="store_true", help="do not write the VAT_ID UV map")
    p.add_argument("--save", action="store_true", help="save the .blend (keeps the VAT_ID UVs for export)")
    args = p.parse_args(list(argv))

    obj = bpy.data.objects.get(args.object)
    if obj is None:
        print(f"dd_vat_bake: object '{args.object}' not found")
        return 1
    scene = bpy.context.scene
    start = args.start if args.start is not None else scene.frame_start
    end = args.end if args.end is not None else scene.frame_end
    try:
        info = bake(obj, start, end, args.out, args.name or obj.name, normals=args.normals, max_width=args.max_width,
                    space=args.space, write_uv2=not args.no_uv2)
    except (RuntimeError, ValueError, TypeError) as e:
        print(f"dd_vat_bake: {e}")
        return 1
    if args.save and bpy.data.filepath:
        bpy.ops.wm.save_mainfile()
    print(f"dd_vat_bake: {info.frames} frames x {info.vertexCount} vertices -> {info.textureWidth}x{info.textureHeight} "
          f"({info.rowsPerFrame} row(s)/frame) in {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main(dd_bpy.args_from_sys()))
