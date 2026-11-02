"""Validate a Blender scene against the Dunk Duel content conventions and write a JSON report.

Usage (headless)::

    blender -b fighter.blend -P Tools/Blender/dd_validate.py -- --report out/validation.json [--collection CH_Kofi]
                                                                [--graybox] [--strict]

Checks: metric units at scale 1.0, 60 fps for animated files, CH_/ST_ naming and LOD suffixes, applied
rotation/scale, triangle budgets (per fighter per LOD, per court layer, per prop), LOD chains, fighter
skeletons (required bones, hierarchy, 4 influences per vertex), texture sets (BaseColor/Normal/ORM
[+Emission], 2K max, power of two, color spaces) and UVs. Exit code 1 if any error (or warning with --strict).
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
from collections import defaultdict
from dataclasses import asdict, dataclass, field
from typing import Dict, List, Optional, Sequence

import bpy  # type: ignore[import-not-found]

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import dd_bpy  # noqa: E402
import dd_conventions as dc  # noqa: E402


@dataclass
class ObjectStats:
    name: str
    type: str
    tris: int = 0
    lod: Optional[int] = None
    layer: Optional[str] = None
    materials: List[str] = field(default_factory=list)


@dataclass
class Report:
    source: str
    blender: str
    errors: List[str] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)
    objects: List[ObjectStats] = field(default_factory=list)
    fighters: Dict[str, Dict[str, int]] = field(default_factory=dict)
    courts: Dict[str, Dict[str, int]] = field(default_factory=dict)
    rules: Dict[str, object] = field(default_factory=dc.report_skeleton)

    @property
    def ok(self) -> bool:
        return not self.errors

    def to_json(self) -> str:
        data = asdict(self)
        data["ok"] = self.ok
        return json.dumps(data, indent=2, sort_keys=True)


def _near(a: float, b: float) -> bool:
    return abs(a - b) <= dc.TRANSFORM_EPSILON


def validate(scene: Optional["bpy.types.Scene"] = None, *, collection: Optional[str] = None, graybox: bool = False) -> Report:
    """Run every check on ``scene`` (default: the active scene)."""
    scene = scene or bpy.context.scene
    report = Report(source=bpy.data.filepath or "<unsaved>", blender=bpy.app.version_string)
    err, warn = report.errors.append, report.warnings.append

    # ---- scene settings
    us = scene.unit_settings
    if us.system != dc.UNIT_SYSTEM:
        err(f"scene units are {us.system}, expected {dc.UNIT_SYSTEM}")
    if not _near(us.scale_length, dc.UNIT_SCALE_LENGTH):
        err(f"scene unit scale is {us.scale_length}, expected {dc.UNIT_SCALE_LENGTH} (meters)")
    if dd_bpy.scene_has_animation(scene):
        fps = scene.render.fps / scene.render.fps_base
        if not _near(fps, dc.ANIMATION_FPS):
            err(f"animated scene runs at {fps:g} fps, frame-locked animation needs {dc.ANIMATION_FPS}")

    try:
        objects = dd_bpy.objects_in_scope(scene, collection)
    except KeyError as e:
        err(str(e))
        return report
    if not objects:
        warn("nothing to validate (no mesh/armature/empty objects in scope)")

    depsgraph = bpy.context.evaluated_depsgraph_get()
    fighter_tris: Dict[str, Dict[int, int]] = defaultdict(lambda: defaultdict(int))
    court_layer_tris: Dict[str, Dict[str, int]] = defaultdict(lambda: defaultdict(int))
    group_lods: Dict[str, Dict[int, int]] = defaultdict(dict)
    group_layer: Dict[str, Optional[str]] = {}
    exported_meshes: List[bpy.types.Object] = []

    for obj in objects:
        stats = ObjectStats(name=obj.name, type=obj.type)
        report.objects.append(stats)

        # ---- naming
        try:
            asset = dc.parse_asset_name(obj.name)
        except dc.NameError_ as e:
            if obj.type == "EMPTY" and not obj.children:
                warn(f"{e} (empty without children is ignored)")
            else:
                err(str(e))
            continue
        stats.lod = asset.lod

        # ---- transforms (location may be non-zero for placed props)
        rot = obj.rotation_quaternion if obj.rotation_mode == "QUATERNION" else obj.rotation_euler
        if obj.rotation_mode == "QUATERNION":
            rot_ok = _near(rot[0], 1.0) and all(_near(c, 0.0) for c in rot[1:])
        else:
            rot_ok = all(_near(c, 0.0) for c in rot)
        if not rot_ok:
            err(f"{obj.name}: unapplied rotation {tuple(round(math.degrees(c), 2) for c in obj.rotation_euler)} deg (Ctrl+A)")
        if any(s < 0 for s in obj.scale):
            err(f"{obj.name}: negative scale {tuple(obj.scale)}")
        elif not all(_near(s, 1.0) for s in obj.scale):
            err(f"{obj.name}: unapplied scale {tuple(round(s, 4) for s in obj.scale)} (Ctrl+A)")
        if asset.is_character and obj.parent is None and any(abs(c) > 1e-3 for c in obj.location):
            warn(f"{obj.name}: character root is not at the origin {tuple(round(c, 3) for c in obj.location)}")

        if obj.type == "ARMATURE":
            if asset.is_character:
                _check_armature(obj, report)
            continue
        if obj.type != "MESH":
            continue

        # ---- mesh
        exported_meshes.append(obj)
        tris = dd_bpy.triangle_count(obj, depsgraph)
        stats.tris = tris
        stats.materials = [s.material.name for s in obj.material_slots if s.material]
        lod = asset.lod if asset.lod is not None else 0
        group_lods[asset.base][lod] = tris
        if asset.is_character:
            fighter_tris[asset.owner][lod] += tris
            _check_skinning(obj, report)
        else:
            layer = dd_bpy.layer_of(obj)
            stats.layer = layer
            group_layer[asset.base] = layer
            if layer is None:
                warn(f"{obj.name}: not inside a court layer collection ({', '.join(dc.COURT_LAYERS)}); budgeted as {dc.LAYER_PLAY}")
                layer = dc.LAYER_PLAY
            if lod == 0:
                court_layer_tris[asset.owner][layer] += tris
        mesh = obj.data
        if len(mesh.uv_layers) == 0:
            textured = any(not m.name.endswith(dc.GRAYBOX_MATERIAL_SUFFIX) for m in (s.material for s in obj.material_slots) if m)
            (err if textured else warn)(f"{obj.name}: mesh has no UV map")
        if not obj.material_slots or all(s.material is None for s in obj.material_slots):
            warn(f"{obj.name}: no material assigned")
        loose = _loose_vertex_count(mesh)
        if loose:
            warn(f"{obj.name}: {loose} loose vertices")

    # ---- budgets
    for token, by_lod in sorted(fighter_tris.items()):
        report.fighters[token] = {f"LOD{k}": v for k, v in sorted(by_lod.items())}
        for lod, tris in sorted(by_lod.items()):
            budget = dc.FIGHTER_BUDGETS.get(lod)
            if budget is None:
                continue
            r = dc.check_budget(f"fighter {token} LOD{lod}", tris, budget, enforce_min=not graybox)
            report.errors.extend(r.errors)
            report.warnings.extend(r.warnings)
    for token, by_layer in sorted(court_layer_tris.items()):
        total = sum(by_layer.values())
        report.courts[token] = dict(sorted(by_layer.items()), total=total)
        for layer, tris in sorted(by_layer.items()):
            r = dc.check_budget(f"court {token} {layer}", tris, dc.COURT_LAYER_BUDGETS[layer])
            report.errors.extend(r.errors)
        if token != dc.SHARED_COURT_TOKEN:
            r = dc.check_budget(f"court {token} total", total, dc.COURT_TOTAL_BUDGET)
            report.errors.extend(r.errors)
    for base, lods in sorted(group_lods.items()):
        report.warnings.extend(dc.check_lod_chain(base, lods))
        if base.startswith("ST_") and 0 in lods:
            layer = group_layer.get(base) or dc.LAYER_PLAY
            budget = dc.CROWD_INSTANCE_BUDGET if layer == dc.LAYER_CROWD and base.startswith("ST_Shared_Crowd") else dc.PROP_BUDGETS[layer]
            r = dc.check_budget(f"prop {base}", lods[0], budget)
            report.errors.extend(r.errors)

    # ---- textures
    _check_textures(dd_bpy.materials_of(exported_meshes), report)
    return report


def _loose_vertex_count(mesh: "bpy.types.Mesh") -> int:
    used = [False] * len(mesh.vertices)
    for e in mesh.edges:
        used[e.vertices[0]] = True
        used[e.vertices[1]] = True
    return sum(1 for u in used if not u)


def _check_armature(arm: "bpy.types.Object", report: Report) -> None:
    bones = arm.data.bones
    names = [b.name for b in bones]
    parents = {b.name: (b.parent.name if b.parent else None) for b in bones}
    errors, warnings = dc.check_bones(names, parents)
    report.errors.extend(f"{arm.name}: {e}" for e in errors)
    report.warnings.extend(f"{arm.name}: {w}" for w in warnings)
    deform = sum(1 for b in bones if b.use_deform)
    if deform > dc.MAX_DEFORM_BONES:
        report.warnings.append(f"{arm.name}: {deform} deform bones (> {dc.MAX_DEFORM_BONES}) is heavy for mid-tier phones")


def _check_skinning(obj: "bpy.types.Object", report: Report) -> None:
    arm = dd_bpy.armature_of(obj)
    if arm is None:
        report.warnings.append(f"{obj.name}: character mesh is not skinned to an armature")
        return
    bone_names = {b.name for b in arm.data.bones}
    group_names = {g.index: g.name for g in obj.vertex_groups}
    over = 0
    unweighted = 0
    for v in obj.data.vertices:
        influences = [g for g in v.groups if g.weight > 1e-4 and group_names.get(g.group) in bone_names]
        if len(influences) > dc.MAX_BONE_INFLUENCES:
            over += 1
        if not influences:
            unweighted += 1
    if over:
        report.warnings.append(f"{obj.name}: {over} vertices have more than {dc.MAX_BONE_INFLUENCES} bone influences (Unity keeps 4)")
    if unweighted:
        report.errors.append(f"{obj.name}: {unweighted} vertices have no bone weights")


def _check_textures(materials: Sequence["bpy.types.Material"], report: Report) -> None:
    for mat in materials:
        slots: List[str] = []
        for node in dd_bpy.image_nodes(mat):
            img = node.image
            stem = dd_bpy.image_stem(img)
            parsed = dc.parse_texture_name(stem)
            if parsed is None:
                report.errors.append(f"{mat.name}: texture '{stem}' must be named <Material>_<BaseColor|Normal|ORM|Emission>")
                continue
            set_name, slot = parsed
            if set_name != mat.name:
                report.errors.append(f"{mat.name}: texture '{stem}' belongs to set '{set_name}', not to this material")
            slots.append(slot)
            width, height = img.size[0], img.size[1]
            if width == 0 or height == 0:
                report.errors.append(f"{mat.name}: texture '{stem}' has no pixels (missing file?)")
            else:
                report.errors.extend(dc.check_texture_size(stem, width, height))
            expected = dc.TEXTURE_COLORSPACE.get(slot)
            actual = img.colorspace_settings.name
            if expected and actual != expected:
                report.warnings.append(f"{stem}: color space '{actual}', expected '{expected}'")
        errors, warnings = dc.check_texture_set(mat.name, slots)
        report.errors.extend(errors)
        report.warnings.extend(warnings)


def _parse(argv: Sequence[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(prog="dd_validate", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--report", help="write the JSON report here")
    p.add_argument("--collection", help="validate only this collection (recursively)")
    p.add_argument("--graybox", action="store_true", help="graybox file: skip the 40k fighter triangle target")
    p.add_argument("--strict", action="store_true", help="treat warnings as errors")
    p.add_argument("--quiet", action="store_true")
    return p.parse_args(list(argv))


def main(argv: Sequence[str]) -> int:
    args = _parse(argv)
    report = validate(collection=args.collection, graybox=args.graybox)
    if args.report:
        os.makedirs(os.path.dirname(os.path.abspath(args.report)), exist_ok=True)
        with open(args.report, "w", encoding="utf-8") as fh:
            fh.write(report.to_json())
    if not args.quiet:
        for e in report.errors:
            print("ERROR  " + e)
        for w in report.warnings:
            print("WARN   " + w)
        print(f"dd_validate: {len(report.errors)} error(s), {len(report.warnings)} warning(s), {len(report.objects)} object(s)")
    failed = bool(report.errors) or (args.strict and bool(report.warnings))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(dd_bpy.args_from_sys()))
