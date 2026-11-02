"""Shared ``bpy`` helpers for the Dunk Duel Blender tools (requires Blender 4.x / the bpy module)."""

from __future__ import annotations

import os
import sys
from typing import Iterable, Iterator, List, Optional, Sequence, Set

import bpy  # type: ignore[import-not-found]

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import dd_conventions as dc  # noqa: E402  (path set up above)

#: Objects and collections whose names start with this prefix are helpers (cameras, lights, widgets)
#: and are never validated or exported.
HELPER_PREFIX = "_"


def is_helper(name: str) -> bool:
    return name.startswith(HELPER_PREFIX) or name.startswith("WGT-")


def iter_collections_recursive(root: "bpy.types.Collection") -> Iterator["bpy.types.Collection"]:
    yield root
    for child in root.children:
        yield from iter_collections_recursive(child)


def objects_in_scope(scene: "bpy.types.Scene", collection_name: Optional[str] = None) -> List["bpy.types.Object"]:
    """Exportable objects (mesh, armature, empty), excluding helpers and helper collections."""
    if collection_name:
        root = bpy.data.collections.get(collection_name)
        if root is None:
            raise KeyError(f"collection '{collection_name}' not found")
    else:
        root = scene.collection
    seen: Set[str] = set()
    result: List[bpy.types.Object] = []
    for col in iter_collections_recursive(root):
        if col is not scene.collection and is_helper(col.name):
            continue
        for obj in col.objects:
            if obj.name in seen or is_helper(obj.name):
                continue
            if obj.type not in {"MESH", "ARMATURE", "EMPTY"}:
                continue
            seen.add(obj.name)
            result.append(obj)
    return result


def layer_of(obj: "bpy.types.Object") -> Optional[str]:
    """Court depth layer from the collection hierarchy (a collection named <Layer> or *_<Layer>)."""
    for col in obj.users_collection:
        layer = _layer_of_collection(col)
        if layer:
            return layer
    return None


def _layer_of_collection(col: "bpy.types.Collection") -> Optional[str]:
    for layer in dc.COURT_LAYERS:
        if col.name == layer or col.name.endswith("_" + layer):
            return layer
    # Walk up: a collection can sit inside a layer collection.
    for parent in bpy.data.collections:
        if col.name in parent.children:
            found = _layer_of_collection(parent)
            if found:
                return found
    return None


def triangle_count(obj: "bpy.types.Object", depsgraph: Optional["bpy.types.Depsgraph"] = None) -> int:
    """Triangles of the evaluated mesh (modifiers applied), 0 for non-meshes."""
    if obj.type != "MESH":
        return 0
    depsgraph = depsgraph or bpy.context.evaluated_depsgraph_get()
    eval_obj = obj.evaluated_get(depsgraph)
    mesh = eval_obj.to_mesh()
    try:
        mesh.calc_loop_triangles()
        return len(mesh.loop_triangles)
    finally:
        eval_obj.to_mesh_clear()


def select_only(objects: Sequence["bpy.types.Object"]) -> None:
    """Make exactly ``objects`` selected (and the first one active), unhiding them if needed."""
    view_layer = bpy.context.view_layer
    for o in view_layer.objects:
        o.select_set(False)
    for o in objects:
        o.hide_set(False)
        o.hide_viewport = False
        o.select_set(True)
    if objects:
        view_layer.objects.active = objects[0]


def image_nodes(material: "bpy.types.Material") -> Iterator["bpy.types.ShaderNodeTexImage"]:
    """Every image texture node in a material, including inside node groups."""
    if not material or not material.use_nodes or not material.node_tree:
        return
    stack = [material.node_tree]
    visited: Set[str] = set()
    while stack:
        tree = stack.pop()
        if tree.name in visited:
            continue
        visited.add(tree.name)
        for node in tree.nodes:
            if node.type == "TEX_IMAGE" and node.image is not None:
                yield node
            elif node.type == "GROUP" and node.node_tree is not None:
                stack.append(node.node_tree)


def image_stem(image: "bpy.types.Image") -> str:
    """File stem of an image (``CH_Kofi_Skin_BaseColor``), falling back to the datablock name."""
    path = image.filepath_raw or image.filepath
    if path:
        return os.path.splitext(os.path.basename(bpy.path.abspath(path)))[0]
    return os.path.splitext(image.name)[0]


def materials_of(objects: Iterable["bpy.types.Object"]) -> List["bpy.types.Material"]:
    seen: Set[str] = set()
    out: List[bpy.types.Material] = []
    for obj in objects:
        if obj.type != "MESH":
            continue
        for slot in obj.material_slots:
            mat = slot.material
            if mat is not None and mat.name not in seen:
                seen.add(mat.name)
                out.append(mat)
    return out


def armature_of(obj: "bpy.types.Object") -> Optional["bpy.types.Object"]:
    """The armature deforming a mesh (modifier first, then parent)."""
    for mod in obj.modifiers:
        if mod.type == "ARMATURE" and mod.object is not None:
            return mod.object
    if obj.parent is not None and obj.parent.type == "ARMATURE":
        return obj.parent
    return None


def scene_has_animation(scene: "bpy.types.Scene") -> bool:
    for obj in scene.objects:
        ad = obj.animation_data
        if ad and (ad.action or len(ad.nla_tracks)):
            return True
        if obj.type == "MESH" and obj.data.shape_keys and obj.data.shape_keys.animation_data:
            return True
    return False


def args_from_sys() -> List[str]:
    return dc.script_args(sys.argv)
