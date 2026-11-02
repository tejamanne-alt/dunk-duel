"""Validate, then batch-export a Blender file to FBX for the Unity project.

Usage (headless)::

    blender -b court.blend -P Tools/Blender/dd_export.py -- --project /path/to/dunk-duel
        [--collection ST_TheTracks] [--only CH_Kofi ST_TheTracks_Rim] [--graybox] [--force] [--dry-run]
        [--report out/export_report.json]

Export groups:

* one FBX per fighter (``CH_<Fighter>.fbx``): rig + every ``CH_<Fighter>_*`` mesh and LOD, all actions as takes,
  sampled every frame at 60 fps with no curve simplification (frame-locked animation);
  written to ``Assets/DunkDuel/Art/Fighters/<id>/``;
* one FBX per stage prop group (``ST_<Court>_<Prop>.fbx`` with its ``_LOD0``-``_LOD2`` meshes, which Unity turns
  into a LODGroup), written to ``Assets/DunkDuel/Art/Courts/<id>/`` (or ``Courts/shared``) plus one
  ``ST_<Court>_layout.json`` per court listing each prop's layer and Unity-space position;
* textures used by exported materials are copied to ``<dest>/Textures/``.

Axis conventions (see dd_conventions): stage assets export with forward Z / up Y and the space transform baked,
mapping Blender (x, y, z) to Unity (x, z, y) with identity rotations; characters export with forward -Z / up Y and
rely on Unity's "Bake Axis Conversion" import option. Validation errors abort the export unless --force.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
from dataclasses import asdict, dataclass, field
from typing import Dict, List, Optional, Sequence

import bpy  # type: ignore[import-not-found]

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import dd_bpy  # noqa: E402
import dd_conventions as dc  # noqa: E402
import dd_validate  # noqa: E402


@dataclass
class ExportGroup:
    key: str
    kind: str              # "character" or "stage"
    owner: str             # fighter or court token
    dest_dir: str          # relative to the Unity project
    objects: List[str] = field(default_factory=list)
    layer: Optional[str] = None
    tris_by_lod: Dict[str, int] = field(default_factory=dict)
    fbx: Optional[str] = None
    textures: List[str] = field(default_factory=list)
    position_unity: Optional[List[float]] = None


def plan(scene: "bpy.types.Scene", collection: Optional[str] = None, only: Optional[Sequence[str]] = None) -> List[ExportGroup]:
    """Group exportable objects into FBX files."""
    groups: Dict[str, ExportGroup] = {}
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for obj in dd_bpy.objects_in_scope(scene, collection):
        try:
            asset = dc.parse_asset_name(obj.name)
        except dc.NameError_:
            continue
        if asset.is_character:
            key = f"CH_{asset.owner}"
            kind = "character"
        else:
            key = asset.base
            kind = "stage"
        if only and key not in only and asset.base not in only:
            continue
        g = groups.get(key)
        if g is None:
            g = ExportGroup(key=key, kind=kind, owner=asset.owner, dest_dir=dc.unity_dir_for(asset))
            groups[key] = g
        g.objects.append(obj.name)
        if obj.type == "MESH":
            lod = asset.lod if asset.lod is not None else 0
            g.tris_by_lod[f"LOD{lod}"] = g.tris_by_lod.get(f"LOD{lod}", 0) + dd_bpy.triangle_count(obj, depsgraph)
            if kind == "stage" and (asset.lod in (None, 0)):
                g.layer = dd_bpy.layer_of(obj) or dc.LAYER_PLAY
                x, y, z = obj.matrix_world.translation
                g.position_unity = [round(v, 5) for v in dc.blender_to_unity_stage(x, y, z)]
    # Character groups also take the children of their rig (accessories without a CH_ prefix are an error upstream).
    return sorted(groups.values(), key=lambda g: g.key)


def _copy_textures(objects: Sequence["bpy.types.Object"], tex_dir: str) -> List[str]:
    written: List[str] = []
    for mat in dd_bpy.materials_of(objects):
        for node in dd_bpy.image_nodes(mat):
            img = node.image
            stem = dd_bpy.image_stem(img)
            src = bpy.path.abspath(img.filepath) if img.filepath else ""
            ext = os.path.splitext(src)[1].lower() if src else ".png"
            if ext not in dc.TEXTURE_EXTENSIONS:
                ext = ".png"
            dest = os.path.join(tex_dir, stem + ext)
            os.makedirs(tex_dir, exist_ok=True)
            if src and os.path.isfile(src):
                if os.path.abspath(src) != os.path.abspath(dest):
                    shutil.copy2(src, dest)
            elif img.packed_file is not None:
                with open(dest, "wb") as fh:
                    fh.write(img.packed_file.data)
            else:
                # Generated or in-memory image: save a PNG copy without touching the original datablock path.
                copy = img.copy()
                copy.filepath_raw = dest
                copy.file_format = "PNG"
                copy.save()
                bpy.data.images.remove(copy)
            written.append(dest)
    return written


def _settings(kind: str) -> Dict[str, object]:
    base = dc.FBX_CHARACTER if kind == "character" else dc.FBX_STAGE
    return {k: (set(v) if isinstance(v, (set, frozenset)) else v) for k, v in base.items()}


def export(project: str, *, scene: Optional["bpy.types.Scene"] = None, collection: Optional[str] = None,
           only: Optional[Sequence[str]] = None, graybox: bool = False, validate_first: bool = True,
           force: bool = False, dry_run: bool = False, report_path: Optional[str] = None) -> int:
    scene = scene or bpy.context.scene
    result: Dict[str, object] = {"source": bpy.data.filepath or "<unsaved>", "blender": bpy.app.version_string, "project": project}

    if validate_first:
        rep = dd_validate.validate(scene, collection=collection, graybox=graybox)
        result["validation"] = {"ok": rep.ok, "errors": rep.errors, "warnings": rep.warnings}
        if not rep.ok and not force:
            for e in rep.errors:
                print("ERROR  " + e)
            print(f"dd_export: aborted, {len(rep.errors)} validation error(s) (use --force to export anyway)")
            _write_report(report_path, result)
            return 1

    groups = plan(scene, collection, only)
    if not groups:
        print("dd_export: nothing to export")
    for g in groups:
        dest_dir = os.path.join(project, g.dest_dir)
        fbx_path = os.path.join(dest_dir, f"{g.key}.fbx")
        g.fbx = os.path.relpath(fbx_path, project)
        if dry_run:
            print(f"[dry-run] {g.kind:9s} {g.key} -> {g.fbx} ({len(g.objects)} objects, {g.tris_by_lod})")
            continue
        objs = [bpy.data.objects[n] for n in g.objects]
        if g.kind == "character":
            # Include everything parented under the rig so accessories travel with the fighter.
            for o in list(objs):
                if o.type == "ARMATURE":
                    objs.extend(c for c in o.children_recursive if c not in objs and not dd_bpy.is_helper(c.name))
        os.makedirs(dest_dir, exist_ok=True)
        dd_bpy.select_only(objs)
        bpy.ops.export_scene.fbx(filepath=fbx_path, **_settings(g.kind))
        g.textures = [os.path.relpath(p, project) for p in _copy_textures(objs, os.path.join(dest_dir, "Textures"))]
        print(f"exported {g.kind:9s} {g.key} -> {g.fbx}")

    # Court layout manifests (layers drive quality tiers; positions are informational for tooling).
    layouts: Dict[str, Dict[str, object]] = {}
    for g in groups:
        if g.kind != "stage" or g.owner == dc.SHARED_COURT_TOKEN:
            continue
        court = dc.COURT_BY_TOKEN[g.owner]
        lay = layouts.setdefault(g.owner, {"court": court.id, "token": court.token, "props": []})
        lay["props"].append({"name": g.key, "fbx": g.fbx, "layer": g.layer, "positionUnity": g.position_unity, "tris": g.tris_by_lod})
    if not dry_run:
        for token, lay in layouts.items():
            path = os.path.join(project, dc.unity_court_dir(lay["court"]), f"ST_{token}_layout.json")
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "w", encoding="utf-8") as fh:
                json.dump(lay, fh, indent=2, sort_keys=True)
            print(f"wrote layout {os.path.relpath(path, project)}")

    result["groups"] = [asdict(g) for g in groups]
    _write_report(report_path, result)
    return 0


def _write_report(path: Optional[str], data: Dict[str, object]) -> None:
    if not path:
        return
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(data, fh, indent=2, sort_keys=True, default=str)


def _default_project() -> str:
    # Tools/Blender/ -> repository root (the Unity project root).
    return os.path.abspath(os.path.join(_HERE, "..", ".."))


def main(argv: Sequence[str]) -> int:
    p = argparse.ArgumentParser(prog="dd_export", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--project", default=_default_project(), help="Unity project root (default: this repository)")
    p.add_argument("--collection", help="export only this collection")
    p.add_argument("--only", nargs="*", help="export only these group keys (CH_Kofi, ST_TheTracks_Rim, ...)")
    p.add_argument("--graybox", action="store_true", help="graybox file: skip the fighter triangle target")
    p.add_argument("--force", action="store_true", help="export even if validation fails")
    p.add_argument("--no-validate", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--report", help="write a JSON export report here")
    args = p.parse_args(list(argv))
    return export(args.project, collection=args.collection, only=args.only, graybox=args.graybox,
                  validate_first=not args.no_validate, force=args.force, dry_run=args.dry_run, report_path=args.report)


if __name__ == "__main__":
    sys.exit(main(dd_bpy.args_from_sys()))
