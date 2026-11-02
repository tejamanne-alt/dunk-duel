"""Generate _LOD1 / _LOD2 meshes for stage props with Decimate (collapse), keeping UVs and materials.

Usage (headless)::

    blender -b props.blend -P Tools/Blender/dd_lod.py -- [--objects ST_TheTracks_Bench ...] [--collection ST_TheTracks]
                                                       [--ratios 1.0 0.5 0.25] [--keep-existing] [--save]

The source mesh is renamed ``<base>_LOD0`` if it has no suffix; each lower LOD is a separate object
(``<base>_LOD1``, ``<base>_LOD2``) with the same transform, collections and material slots, so Unity
builds a LODGroup from a single FBX. Fighters' LODs are authored by hand and are skipped unless named
explicitly with --objects. The decimated meshes are applied (no live modifiers are exported).
"""

from __future__ import annotations

import argparse
import os
import sys
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Sequence

import bpy  # type: ignore[import-not-found]

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import dd_bpy  # noqa: E402
import dd_conventions as dc  # noqa: E402


@dataclass
class LodResult:
    base: str
    tris: Dict[int, int] = field(default_factory=dict)
    created: List[str] = field(default_factory=list)
    skipped: Optional[str] = None   # nothing to do (already minimal); not an error
    failed: Optional[str] = None    # could not process (bad name)


def _remove_object(name: str) -> None:
    obj = bpy.data.objects.get(name)
    if obj is None:
        return
    mesh = obj.data if obj.type == "MESH" else None
    bpy.data.objects.remove(obj, do_unlink=True)
    if mesh is not None and mesh.users == 0:
        bpy.data.meshes.remove(mesh)


def generate_lods(obj: "bpy.types.Object", ratios: Sequence[float] = dc.PROP_LOD_RATIOS, *, keep_existing: bool = False) -> LodResult:
    """Create LOD1..LODn for one mesh object. ``ratios[0]`` must be 1.0 (the source)."""
    if obj.type != "MESH":
        raise TypeError(f"{obj.name} is not a mesh")
    if len(ratios) < 2 or abs(ratios[0] - 1.0) > 1e-6 or len(ratios) > dc.MAX_LOD + 1:
        raise ValueError(f"ratios must start at 1.0 and have 2..{dc.MAX_LOD + 1} entries")
    if any(not 0.0 < r <= 1.0 for r in ratios) or any(b >= a for a, b in zip(ratios, ratios[1:])):
        raise ValueError("ratios must be decreasing values in (0, 1]")

    base = dc.strip_lod(obj.name)
    result = LodResult(base=base)
    if obj.name != dc.lod_name(base, 0) and obj.name != base:
        result.failed = f"{obj.name}: only _LOD0 (or unsuffixed) meshes can seed LODs"
        return result

    depsgraph = bpy.context.evaluated_depsgraph_get()
    result.tris[0] = dd_bpy.triangle_count(obj, depsgraph)
    levels = dc.planned_lod_levels(result.tris[0], ratios)
    if not levels:
        result.skipped = f"{base}: {result.tris[0]} triangles is already minimal (< {dc.LOD_MIN_SOURCE_TRIS})"
        return result
    if obj.name == base:
        obj.name = dc.lod_name(base, 0)
    if not keep_existing:
        for stale in range(levels[-1] + 1, dc.MAX_LOD + 1):
            _remove_object(dc.lod_name(base, stale))
    for lod in levels:
        name = dc.lod_name(base, lod)
        if bpy.data.objects.get(name) is not None:
            if keep_existing:
                result.tris[lod] = dd_bpy.triangle_count(bpy.data.objects[name], depsgraph)
                continue
            _remove_object(name)

        lod_obj = obj.copy()
        lod_obj.data = obj.data.copy()
        lod_obj.name = name
        lod_obj.animation_data_clear()
        for col in obj.users_collection:
            col.objects.link(lod_obj)

        mod = lod_obj.modifiers.new(name="DD_Decimate", type="DECIMATE")
        mod.decimate_type = "COLLAPSE"
        mod.ratio = float(ratios[lod])
        mod.use_collapse_triangulate = False
        mod.use_symmetry = False

        bpy.context.view_layer.update()
        depsgraph = bpy.context.evaluated_depsgraph_get()
        evaluated = lod_obj.evaluated_get(depsgraph)
        applied = bpy.data.meshes.new_from_object(evaluated, preserve_all_data_layers=True, depsgraph=depsgraph)
        applied.name = name
        old = lod_obj.data
        lod_obj.modifiers.clear()
        lod_obj.data = applied
        if old.users == 0:
            bpy.data.meshes.remove(old)
        # Material slots linked to the object (not the mesh) survive the data swap; re-assert mesh materials.
        for i, slot in enumerate(obj.material_slots):
            if i < len(lod_obj.material_slots) and slot.link == "OBJECT":
                lod_obj.material_slots[i].material = slot.material

        bpy.context.view_layer.update()
        result.tris[lod] = dd_bpy.triangle_count(lod_obj)
        result.created.append(name)
    return result


def _targets(scene: "bpy.types.Scene", names: Optional[Sequence[str]], collection: Optional[str]) -> List["bpy.types.Object"]:
    if names:
        missing = [n for n in names if bpy.data.objects.get(n) is None]
        if missing:
            raise KeyError("objects not found: " + ", ".join(missing))
        return [bpy.data.objects[n] for n in names]
    out = []
    for obj in dd_bpy.objects_in_scope(scene, collection):
        if obj.type != "MESH" or not obj.name.startswith("ST_"):
            continue
        try:
            asset = dc.parse_asset_name(obj.name)
        except dc.NameError_:
            continue
        # Crowd meshes are driven by vertex animation textures (each LOD would need its own bake);
        # backdrop cards are single quads.
        if dd_bpy.layer_of(obj) in (dc.LAYER_CROWD, dc.LAYER_BACKDROP) or is_animated_mesh(obj):
            continue
        if asset.lod in (None, 0):
            out.append(obj)
    return out


def is_animated_mesh(obj: "bpy.types.Object") -> bool:
    """Shape keys or a VAT_ID UV map: decimated LODs would lose the animation / vertex ids."""
    return bool(obj.data.shape_keys) or obj.data.uv_layers.get("VAT_ID") is not None


def main(argv: Sequence[str]) -> int:
    p = argparse.ArgumentParser(prog="dd_lod", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--objects", nargs="*", help="mesh objects to process (default: every ST_ prop at LOD0)")
    p.add_argument("--collection", help="limit the default selection to this collection")
    p.add_argument("--ratios", nargs="*", type=float, default=list(dc.PROP_LOD_RATIOS))
    p.add_argument("--keep-existing", action="store_true", help="do not regenerate LODs that already exist")
    p.add_argument("--save", action="store_true", help="save the .blend after generating")
    args = p.parse_args(list(argv))

    scene = bpy.context.scene
    try:
        targets = _targets(scene, args.objects, args.collection)
    except KeyError as e:
        print(f"dd_lod: {e}")
        return 1
    failures = 0
    for obj in targets:
        if obj.type == "MESH" and is_animated_mesh(obj):
            print(f"WARN   {obj.name}: shape keys / VAT ids are not carried into decimated LODs; bake a VAT per LOD")
        r = generate_lods(obj, args.ratios, keep_existing=args.keep_existing)
        if r.failed:
            print("ERROR  " + r.failed)
            failures += 1
            continue
        if r.skipped:
            print("SKIP   " + r.skipped)
            continue
        chain = ", ".join(f"LOD{k}={v:,}" for k, v in sorted(r.tris.items()))
        print(f"{r.base}: {chain}")
        for w in dc.check_lod_chain(r.base, r.tris):
            print("WARN   " + w)
    if args.save and bpy.data.filepath:
        bpy.ops.wm.save_mainfile()
    print(f"dd_lod: {len(targets) - failures} object(s) processed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(dd_bpy.args_from_sys()))
