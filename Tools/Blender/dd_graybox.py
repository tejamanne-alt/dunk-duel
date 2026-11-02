"""Generate graybox mannequins and a graybox half court at the sim's dimensions, then save a .blend.

Usage (headless, no input file needed)::

    blender -b -P Tools/Blender/dd_graybox.py -- --out art/graybox_the_tracks.blend [--court-id the_tracks]
        [--fighters kofi meera juno matas] [--no-court] [--no-fighters] [--lineup] [--render preview.png]

Fighters: a primitive humanoid per fighter at the exact roster height (top of head = height), with hands,
shoulders and calves pushed +15% per the art direction, a ``CH_<Fighter>_Rig`` armature with every required
bone (rigid skinning), and an untextured ``*_Graybox`` material in the fighter's identity hue. Mannequins
face -Y (Blender front) and stand at the origin unless ``--lineup`` spreads them along X for previews.

Court: X toward the hoop, Z up, Y depth away from the gameplay camera (Blender front view == game camera):
rim center (0, 3.05) radius 0.23, backboard face x = +0.38 (1.8 m wide), baseline +1.6, arc -6.75 (a line
across the court depth), free-throw -4.2, check spot -7.6, left bound -10.5, 8 m deep. Objects sit in the
three depth layers (``ST_<Court>_PlayPlane``, ``_MidGround``, ``_Backdrop``) plus ``_Crowd`` with an animated
``ST_Shared_CrowdA`` spectator (shape-key cheer) for VAT baking. Helpers (camera, light) start with "_".
"""

from __future__ import annotations

import argparse
import math
import os
import sys
from typing import Dict, List, Optional, Sequence, Tuple

import bpy  # type: ignore[import-not-found]  # must come first: the bpy module adds bmesh/mathutils to sys.path

import bmesh  # type: ignore[import-not-found]  # noqa: E402
from mathutils import Matrix, Vector  # type: ignore[import-not-found]  # noqa: E402

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import dd_bpy  # noqa: E402
import dd_conventions as dc  # noqa: E402

Vec3 = Tuple[float, float, float]

#: Art direction: hands, shoulders and calves pushed 10-20%.
EXAGGERATION = 1.15

#: Identity hues (linear RGB) used for the graybox materials; stage palettes avoid these.
FIGHTER_COLORS: Dict[str, Tuple[float, float, float]] = {
    "kofi": (0.92, 0.45, 0.04),   # saffron
    "meera": (0.10, 0.12, 0.52),  # indigo
    "juno": (0.80, 0.03, 0.35),   # magenta
    "matas": (0.03, 0.40, 0.18),  # emerald
}

#: Floor color per court tier (neutral so fighters pop).
TIER_FLOOR: Dict[str, Tuple[float, float, float]] = {
    "street": (0.32, 0.33, 0.35),
    "school": (0.55, 0.38, 0.20),
    "pro": (0.20, 0.17, 0.15),
}


# ---------------------------------------------------------------------------------------------------
# Mesh building
# ---------------------------------------------------------------------------------------------------


class MeshBuilder:
    """Accumulates primitives in one bmesh and remembers which vertices belong to which bone."""

    def __init__(self) -> None:
        self.bm = bmesh.new()
        self.bm.loops.layers.uv.new("UVMap")
        self.groups: Dict[str, List["bmesh.types.BMVert"]] = {}

    def _tag(self, verts: Sequence["bmesh.types.BMVert"], bone: Optional[str]) -> List["bmesh.types.BMVert"]:
        verts = list(verts)
        if bone:
            self.groups.setdefault(bone, []).extend(verts)
        return verts

    def box(self, center: Vec3, size: Vec3, bone: Optional[str] = None) -> List["bmesh.types.BMVert"]:
        m = Matrix.Translation(center) @ Matrix.Diagonal((size[0], size[1], size[2], 1.0))
        res = bmesh.ops.create_cube(self.bm, size=1.0, matrix=m, calc_uvs=True)
        return self._tag(res["verts"], bone)

    def cone(self, a: Vec3, b: Vec3, radius_a: float, radius_b: float, segments: int = 12, caps: bool = True,
             bone: Optional[str] = None) -> List["bmesh.types.BMVert"]:
        va, vb = Vector(a), Vector(b)
        axis = vb - va
        length = axis.length
        rot = Vector((0.0, 0.0, 1.0)).rotation_difference(axis.normalized()).to_matrix().to_4x4()
        m = Matrix.Translation((va + vb) * 0.5) @ rot
        res = bmesh.ops.create_cone(self.bm, cap_ends=caps, cap_tris=False, segments=segments, radius1=radius_a,
                                    radius2=radius_b, depth=length, matrix=m, calc_uvs=True)
        return self._tag(res["verts"], bone)

    def sphere(self, center: Vec3, radii: Vec3, segments: int = 16, rings: int = 8, bone: Optional[str] = None) -> List["bmesh.types.BMVert"]:
        m = Matrix.Translation(center) @ Matrix.Diagonal((radii[0], radii[1], radii[2], 1.0))
        res = bmesh.ops.create_uvsphere(self.bm, u_segments=segments, v_segments=rings, radius=1.0, matrix=m, calc_uvs=True)
        return self._tag(res["verts"], bone)

    def capsule(self, a: Vec3, b: Vec3, radius: float, radius_b: Optional[float] = None, bone: Optional[str] = None) -> None:
        rb = radius if radius_b is None else radius_b
        self.cone(a, b, radius, rb, caps=False, bone=bone)
        self.sphere(a, (radius, radius, radius), 12, 6, bone=bone)
        self.sphere(b, (rb, rb, rb), 12, 6, bone=bone)

    def torus(self, center: Vec3, major: float, minor: float, seg_major: int = 32, seg_minor: int = 8,
              bone: Optional[str] = None) -> List["bmesh.types.BMVert"]:
        """Torus lying in the XY plane (a rim seen from the side is a thin horizontal ring)."""
        cx, cy, cz = center
        rings: List[List["bmesh.types.BMVert"]] = []
        for i in range(seg_major):
            u = 2.0 * math.pi * i / seg_major
            ring = []
            for j in range(seg_minor):
                v = 2.0 * math.pi * j / seg_minor
                r = major + minor * math.cos(v)
                ring.append(self.bm.verts.new((cx + r * math.cos(u), cy + r * math.sin(u), cz + minor * math.sin(v))))
            rings.append(ring)
        uv = self.bm.loops.layers.uv.active
        for i in range(seg_major):
            for j in range(seg_minor):
                quad = (rings[i][j], rings[(i + 1) % seg_major][j], rings[(i + 1) % seg_major][(j + 1) % seg_minor], rings[i][(j + 1) % seg_minor])
                face = self.bm.faces.new(quad)
                coords = ((i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1))
                for loop, (a, c) in zip(face.loops, coords):
                    loop[uv].uv = (a / seg_major, c / seg_minor)
        return self._tag([v for ring in rings for v in ring], bone)

    def vertical_card(self, x0: float, x1: float, y: float, z0: float, z1: float, bone: Optional[str] = None) -> List["bmesh.types.BMVert"]:
        """A quad in the XZ plane at depth y, facing -Y (toward the gameplay camera)."""
        vs = [self.bm.verts.new(p) for p in ((x0, y, z0), (x1, y, z0), (x1, y, z1), (x0, y, z1))]
        face = self.bm.faces.new(vs)
        uv = self.bm.loops.layers.uv.active
        for loop, c in zip(face.loops, ((0, 0), (1, 0), (1, 1), (0, 1))):
            loop[uv].uv = c
        face.normal_update()
        if face.normal.y > 0:
            face.normal_flip()
        return self._tag(vs, bone)

    def to_object(self, name: str, collection: "bpy.types.Collection", material: Optional["bpy.types.Material"]) -> "bpy.types.Object":
        bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces[:])
        self.bm.verts.index_update()
        mesh = bpy.data.meshes.new(name)
        self.bm.to_mesh(mesh)
        index_groups = {bone: sorted({v.index for v in verts}) for bone, verts in self.groups.items()}
        self.bm.free()
        obj = bpy.data.objects.new(name, mesh)
        collection.objects.link(obj)
        if material is not None:
            mesh.materials.append(material)
        for bone, idx in index_groups.items():
            vg = obj.vertex_groups.new(name=bone)
            vg.add(idx, 1.0, "REPLACE")
        return obj


def graybox_material(name: str, color: Tuple[float, float, float], roughness: float = 0.7, metallic: float = 0.0,
                     alpha: float = 1.0) -> "bpy.types.Material":
    if not name.endswith(dc.GRAYBOX_MATERIAL_SUFFIX):
        name += dc.GRAYBOX_MATERIAL_SUFFIX
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = (*color, 1.0)
        bsdf.inputs["Roughness"].default_value = roughness
        bsdf.inputs["Metallic"].default_value = metallic
        if alpha < 1.0:
            bsdf.inputs["Alpha"].default_value = alpha
    mat.diffuse_color = (*color, alpha)
    return mat


def ensure_collection(name: str, parent: "bpy.types.Collection") -> "bpy.types.Collection":
    col = bpy.data.collections.get(name)
    if col is None:
        col = bpy.data.collections.new(name)
    if col.name not in parent.children:
        parent.children.link(col)
    return col


# ---------------------------------------------------------------------------------------------------
# Mannequins
# ---------------------------------------------------------------------------------------------------


def _skeleton(h: float) -> Dict[str, Tuple[Vec3, Vec3, Optional[str], bool]]:
    """bone -> (head, tail, parent, deform). Character faces -Y; its left side is +X."""
    bones: Dict[str, Tuple[Vec3, Vec3, Optional[str], bool]] = {
        dc.ROOT_BONE: ((0, 0, 0), (0, 0, 0.10 * h), None, False),
        "Hips": ((0, 0, 0.475 * h), (0, 0, 0.54 * h), dc.ROOT_BONE, True),
        "Spine": ((0, 0, 0.54 * h), (0, 0, 0.62 * h), "Hips", True),
        "Chest": ((0, 0, 0.62 * h), (0, 0, 0.70 * h), "Spine", True),
        "UpperChest": ((0, 0, 0.70 * h), (0, 0, 0.835 * h), "Chest", True),
        "Neck": ((0, 0, 0.835 * h), (0, 0, 0.885 * h), "UpperChest", True),
        "Head": ((0, 0, 0.885 * h), (0, 0, 1.0 * h), "Neck", True),
    }
    for side, sx in (("Left", 1.0), ("Right", -1.0)):
        shoulder = (sx * 0.13 * h, 0.0, 0.795 * h)
        elbow = (sx * 0.20 * h, 0.005 * h, 0.625 * h)
        wrist = (sx * 0.245 * h, 0.0, 0.475 * h)
        hand_tip = (sx * 0.255 * h, 0.0, 0.385 * h)
        hip = (sx * 0.055 * h, 0.0, 0.475 * h)
        knee = (sx * 0.06 * h, -0.005 * h, 0.265 * h)
        ankle = (sx * 0.062 * h, 0.01 * h, 0.045 * h)
        ball = (sx * 0.062 * h, -0.07 * h, 0.02 * h)
        toe = (sx * 0.062 * h, -0.12 * h, 0.02 * h)
        bones[f"{side}Shoulder"] = ((sx * 0.02 * h, 0.0, 0.80 * h), shoulder, "UpperChest", True)
        bones[f"{side}UpperArm"] = (shoulder, elbow, f"{side}Shoulder", True)
        bones[f"{side}LowerArm"] = (elbow, wrist, f"{side}UpperArm", True)
        bones[f"{side}Hand"] = (wrist, hand_tip, f"{side}LowerArm", True)
        bones[f"BallSocket{side}"] = (hand_tip, (hand_tip[0], hand_tip[1] - 0.05 * h, hand_tip[2]), f"{side}Hand", False)
        bones[f"{side}UpperLeg"] = (hip, knee, "Hips", True)
        bones[f"{side}LowerLeg"] = (knee, ankle, f"{side}UpperLeg", True)
        bones[f"{side}Foot"] = (ankle, ball, f"{side}LowerLeg", True)
        bones[f"{side}Toes"] = (ball, toe, f"{side}Foot", True)
    return bones


def build_rig(fighter: dc.FighterSpec, collection: "bpy.types.Collection", offset: Vec3) -> "bpy.types.Object":
    arm_data = bpy.data.armatures.new(dc.rig_name(fighter.id))
    arm = bpy.data.objects.new(dc.rig_name(fighter.id), arm_data)
    collection.objects.link(arm)
    arm.location = offset
    view_layer = bpy.context.view_layer
    for o in view_layer.objects:
        o.select_set(False)
    arm.select_set(True)
    view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode="EDIT")
    edit = arm_data.edit_bones
    spec = _skeleton(fighter.height_m)
    for name, (head, tail, _parent, deform) in spec.items():
        eb = edit.new(name)
        eb.head = head
        eb.tail = tail
        eb.use_deform = deform
    for name, (_head, _tail, parent, _deform) in spec.items():
        if parent:
            edit[name].parent = edit[parent]
            edit[name].use_connect = False
    bpy.ops.object.mode_set(mode="OBJECT")
    return arm


def build_mannequin(fighter: dc.FighterSpec, parent: "bpy.types.Collection", offset: Vec3 = (0.0, 0.0, 0.0)) -> Tuple["bpy.types.Object", "bpy.types.Object"]:
    """Mannequin mesh + rig for one fighter inside collection ``CH_<Fighter>``."""
    h = fighter.height_m
    col = ensure_collection(f"CH_{fighter.token}", parent)
    arm = build_rig(fighter, col, offset)
    mb = MeshBuilder()
    e = EXAGGERATION
    # Head, neck, torso.
    mb.sphere((0, 0, h - 0.065 * h), (0.058 * h, 0.066 * h, 0.065 * h), 20, 10, bone="Head")
    mb.cone((0, 0, 0.835 * h), (0, 0, 0.885 * h), 0.028 * h, 0.026 * h, bone="Neck")
    mb.box((0, 0, 0.735 * h), (0.21 * h, 0.12 * h, 0.19 * h), bone="Chest")
    mb.box((0, 0, 0.58 * h), (0.17 * h, 0.11 * h, 0.12 * h), bone="Spine")
    mb.box((0, 0, 0.50 * h), (0.18 * h, 0.115 * h, 0.08 * h), bone="Hips")
    for side, sx in (("Left", 1.0), ("Right", -1.0)):
        shoulder = (sx * 0.13 * h, 0.0, 0.795 * h)
        elbow = (sx * 0.20 * h, 0.005 * h, 0.625 * h)
        wrist = (sx * 0.245 * h, 0.0, 0.475 * h)
        hip = (sx * 0.055 * h, 0.0, 0.475 * h)
        knee = (sx * 0.06 * h, -0.005 * h, 0.265 * h)
        ankle = (sx * 0.062 * h, 0.01 * h, 0.045 * h)
        mb.sphere((sx * 0.115 * h, 0.0, 0.80 * h), (0.045 * h * e,) * 3, 14, 7, bone=f"{side}Shoulder")
        mb.capsule(shoulder, elbow, 0.032 * h, bone=f"{side}UpperArm")
        mb.capsule(elbow, wrist, 0.026 * h, 0.022 * h, bone=f"{side}LowerArm")
        mb.box((sx * 0.25 * h, 0.0, 0.475 * h - 0.05 * h), (0.03 * h * e, 0.05 * h * e, 0.09 * h * e), bone=f"{side}Hand")
        mb.capsule(hip, knee, 0.05 * h, 0.042 * h, bone=f"{side}UpperLeg")
        mb.cone(ankle, knee, 0.028 * h, 0.042 * h * e, bone=f"{side}LowerLeg")
        mb.sphere((sx * 0.061 * h, 0.012 * h, 0.20 * h), (0.036 * h * e, 0.04 * h * e, 0.06 * h * e), 12, 6, bone=f"{side}LowerLeg")
        mb.box((sx * 0.062 * h, -0.045 * h, 0.0225 * h), (0.055 * h, 0.15 * h, 0.045 * h), bone=f"{side}Foot")
    mat = graybox_material(f"CH_{fighter.token}_Mannequin", FIGHTER_COLORS[fighter.id], roughness=0.55)
    mesh_obj = mb.to_object(dc.lod_name(f"CH_{fighter.token}_Mannequin", 0), col, mat)
    mesh_obj.parent = arm
    mod = mesh_obj.modifiers.new("Armature", "ARMATURE")
    mod.object = arm
    return arm, mesh_obj


# ---------------------------------------------------------------------------------------------------
# Court
# ---------------------------------------------------------------------------------------------------


def build_court(court: dc.CourtSpec, parent: "bpy.types.Collection") -> Dict[str, "bpy.types.Collection"]:
    g = dc.COURT_GEOMETRY
    t = court.token
    root = ensure_collection(f"ST_{t}", parent)
    layers = {layer: ensure_collection(f"ST_{t}_{layer}", root) for layer in dc.COURT_LAYERS}
    play, mid, back, crowd = (layers[dc.LAYER_PLAY], layers[dc.LAYER_MID], layers[dc.LAYER_BACKDROP], layers[dc.LAYER_CROWD])

    floor_mat = graybox_material(f"ST_{t}_Floor", TIER_FLOOR[court.tier], roughness=0.85)
    line_mat = graybox_material(f"ST_{t}_Lines", (0.85, 0.85, 0.82), roughness=0.6)
    hoop_mat = graybox_material(f"ST_{t}_Hoop", (0.80, 0.25, 0.05), roughness=0.4, metallic=0.8)
    glass_mat = graybox_material(f"ST_{t}_Glass", (0.75, 0.85, 0.90), roughness=0.05, alpha=0.35)
    steel_mat = graybox_material(f"ST_{t}_Steel", (0.25, 0.26, 0.28), roughness=0.5, metallic=0.9)
    net_mat = graybox_material(f"ST_{t}_Net", (0.95, 0.95, 0.95), roughness=0.9)
    mid_mat = graybox_material(f"ST_{t}_Mid", (0.18, 0.20, 0.24), roughness=0.9)
    card_mat = graybox_material(f"ST_{t}_Card", (0.10, 0.12, 0.18), roughness=1.0)

    half = g.depth_m / 2.0
    line_h = 0.004

    def single(name: str, col: "bpy.types.Collection", mat: "bpy.types.Material", build) -> "bpy.types.Object":  # type: ignore[no-untyped-def]
        mb = MeshBuilder()
        build(mb)
        return mb.to_object(name, col, mat)

    # Play plane: floor, lines, hoop.
    single(f"ST_{t}_Floor", play, floor_mat,
           lambda mb: mb.box(((g.left_bound_x - 1.0 + g.baseline_x + 1.5) / 2.0, 0.5, -0.05),
                             ((g.baseline_x + 1.5) - (g.left_bound_x - 1.0), g.depth_m + 3.0, 0.1)))
    lines = {
        "LineBaseline": (g.baseline_x, g.depth_m),
        "LineArc": (g.arc_x, g.depth_m),
        "LineFreeThrow": (g.free_throw_x, 4.9),
        "LineLeftBound": (g.left_bound_x, g.depth_m),
    }
    for nm, (x, length) in lines.items():
        single(f"ST_{t}_{nm}", play, line_mat, lambda mb, x=x, length=length: mb.box((x, 0.0, line_h / 2.0), (g.line_width, length, line_h)))
    for nm, y in (("LineKeyNear", -2.45), ("LineKeyFar", 2.45)):
        span = g.baseline_x - g.free_throw_x
        single(f"ST_{t}_{nm}", play, line_mat,
               lambda mb, y=y, span=span: mb.box(((g.baseline_x + g.free_throw_x) / 2.0, y, line_h / 2.0), (span, g.line_width, line_h)))
    single(f"ST_{t}_CheckSpot", play, line_mat,
           lambda mb: mb.cone((g.check_x, 0.0, 0.0), (g.check_x, 0.0, line_h), 0.15, 0.15, segments=24))
    single(f"ST_{t}_Rim", play, hoop_mat, lambda mb: mb.torus((g.rim_x, 0.0, g.rim_y), g.rim_radius, g.rim_tube_radius))
    single(f"ST_{t}_Net", play, net_mat,
           lambda mb: mb.cone((g.rim_x, 0.0, g.rim_y - 0.40), (g.rim_x, 0.0, g.rim_y), 0.15, g.rim_radius, segments=16, caps=False))
    single(f"ST_{t}_Backboard", play, glass_mat,
           lambda mb: mb.box((g.backboard_face_x + g.backboard_thickness / 2.0, 0.0, (g.backboard_bottom + g.backboard_top) / 2.0),
                             (g.backboard_thickness, g.backboard_width, g.backboard_top - g.backboard_bottom)))
    pole_x = g.baseline_x + 0.4
    single(f"ST_{t}_HoopArm", play, steel_mat,
           lambda mb: mb.box(((g.backboard_face_x + g.backboard_thickness + pole_x) / 2.0, 0.0, 3.27),
                             (pole_x - g.backboard_face_x - g.backboard_thickness, 0.16, 0.14)))
    single(f"ST_{t}_HoopPole", play, steel_mat, lambda mb: mb.box((pole_x + 0.1, 0.0, 1.7), (0.2, 0.2, 3.4)))

    # Mid-ground: low-poly blocks (bleachers, stalls, buildings) behind the court.
    blocks = [(-11.0, 7.0, 3.0, 2.5, 3.5), (-6.5, 8.0, 4.0, 3.0, 5.0), (-2.0, 7.5, 3.5, 2.0, 2.8), (2.5, 9.0, 5.0, 3.0, 6.5), (-15.0, 9.5, 5.0, 3.0, 7.5)]
    for i, (x, y, w, d, hgt) in enumerate(blocks, start=1):
        single(f"ST_{t}_MidBlock{i:02d}", mid, mid_mat, lambda mb, x=x, y=y, w=w, d=d, hgt=hgt: mb.box((x, y, hgt / 2.0), (w, d, hgt)))

    # Backdrop: painted cards with parallax.
    single(f"ST_{t}_CardNear", back, card_mat, lambda mb: mb.vertical_card(-26.0, 18.0, 18.0, -1.0, 15.0))
    single(f"ST_{t}_CardFar", back, card_mat, lambda mb: mb.vertical_card(-40.0, 30.0, 28.0, -1.0, 25.0))

    # Crowd: one animated spectator for VAT baking (instanced by the hundreds in Unity).
    build_spectator(crowd, (-4.0, half + 1.5, 0.0))
    return layers


def build_spectator(collection: "bpy.types.Collection", location: Vec3) -> "bpy.types.Object":
    """Low-poly spectator with a 'Cheer' shape key keyed 0 -> 1 -> 0 over frames 1-30."""
    mb = MeshBuilder()
    mb.box((0.0, 0.0, 0.55), (0.42, 0.26, 0.6), bone="Body")
    mb.box((0.0, 0.0, 0.18), (0.36, 0.24, 0.36), bone="Body")
    mb.sphere((0.0, 0.0, 1.0), (0.12, 0.12, 0.13), 10, 6, bone="Body")
    mb.box((0.27, 0.0, 0.60), (0.1, 0.1, 0.45), bone="ArmL")
    mb.box((-0.27, 0.0, 0.60), (0.1, 0.1, 0.45), bone="ArmR")
    mat = graybox_material("ST_Shared_Crowd", (0.45, 0.45, 0.50), roughness=0.9)
    obj = mb.to_object("ST_Shared_CrowdA", collection, None)
    obj.data.materials.append(mat)
    obj.location = location
    # Shape keys: arms swing up over the shoulder pivot.
    obj.shape_key_add(name="Basis", from_mix=False)
    cheer = obj.shape_key_add(name="Cheer", from_mix=False)
    for group in ("ArmL", "ArmR"):
        vg = obj.vertex_groups[group]
        sign = 1.0 if group == "ArmL" else -1.0
        pivot = Vector((sign * 0.27, 0.0, 0.82))
        rot = Matrix.Rotation(math.radians(150.0) * sign, 4, "Y")
        for v in obj.data.vertices:
            if any(g.group == vg.index for g in v.groups):
                cheer.data[v.index].co = pivot + (rot @ (v.co - pivot))
    for group in ("Body", "ArmL", "ArmR"):
        obj.vertex_groups.remove(obj.vertex_groups[group])
    for frame, value in ((1, 0.0), (15, 1.0), (30, 0.0)):
        cheer.value = value
        cheer.keyframe_insert("value", frame=frame)
    return obj


# ---------------------------------------------------------------------------------------------------
# Scene
# ---------------------------------------------------------------------------------------------------


def configure_scene(scene: "bpy.types.Scene") -> None:
    scene.unit_settings.system = dc.UNIT_SYSTEM
    scene.unit_settings.scale_length = dc.UNIT_SCALE_LENGTH
    scene.render.fps = dc.ANIMATION_FPS
    scene.render.fps_base = 1.0
    scene.frame_start = 1
    scene.frame_end = 120


def add_helpers(scene: "bpy.types.Scene") -> None:
    g = dc.COURT_GEOMETRY
    cam_data = bpy.data.cameras.new("_GameplayCamera")
    cam_data.lens = 30.0
    cam = bpy.data.objects.new("_GameplayCamera", cam_data)
    scene.collection.objects.link(cam)
    cam.location = ((g.left_bound_x + g.baseline_x) / 2.0, -13.0, 2.4)
    cam.rotation_euler = (math.radians(88.0), 0.0, 0.0)  # looks along +Y: Blender front view == gameplay camera
    scene.camera = cam
    sun_data = bpy.data.lights.new("_KeyLight", type="SUN")
    sun_data.energy = 3.0
    sun = bpy.data.objects.new("_KeyLight", sun_data)
    scene.collection.objects.link(sun)
    sun.rotation_euler = (math.radians(50.0), math.radians(10.0), math.radians(-30.0))


def reset_file() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)


def generate(*, court_id: Optional[str] = "the_tracks", fighters: Sequence[str] = tuple(f.id for f in dc.FIGHTERS),
             lineup: bool = False) -> Dict[str, object]:
    """Build the graybox into the current (empty) file. Returns a summary."""
    scene = bpy.context.scene
    configure_scene(scene)
    summary: Dict[str, object] = {"fighters": {}, "court": None}
    g = dc.COURT_GEOMETRY
    center_x = (g.left_bound_x + g.baseline_x) / 2.0 if court_id else 0.0
    for i, fid in enumerate(fighters):
        spec = dc.FIGHTER_BY_ID[fid]
        offset = (center_x + (i - (len(fighters) - 1) / 2.0) * 1.2, -2.0, 0.0) if lineup else (0.0, 0.0, 0.0)
        arm, mesh = build_mannequin(spec, scene.collection, offset)
        bpy.context.view_layer.update()
        top = max((arm.matrix_world @ (mesh.matrix_local @ v.co)).z for v in mesh.data.vertices)
        summary["fighters"][fid] = {"height": spec.height_m, "measuredTop": round(top, 4), "tris": dd_bpy.triangle_count(mesh)}
    if court_id:
        build_court(dc.COURT_BY_ID[court_id], scene.collection)
        summary["court"] = court_id
    add_helpers(scene)
    return summary


def render_preview(path: str, width: int = 960, height: int = 540, samples: int = 8) -> None:
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = samples
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    world = bpy.data.worlds.new("_World")
    world.color = (0.05, 0.05, 0.07)
    scene.world = world
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def main(argv: Sequence[str]) -> int:
    p = argparse.ArgumentParser(prog="dd_graybox", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--out", help="save the generated .blend here")
    p.add_argument("--court-id", default="the_tracks", choices=[c.id for c in dc.COURTS])
    p.add_argument("--fighters", nargs="*", default=[f.id for f in dc.FIGHTERS], choices=[f.id for f in dc.FIGHTERS])
    p.add_argument("--no-court", action="store_true")
    p.add_argument("--no-fighters", action="store_true")
    p.add_argument("--lineup", action="store_true", help="spread fighters along X for previews (validator warns)")
    p.add_argument("--render", help="render a Cycles preview PNG from the gameplay camera")
    p.add_argument("--keep-file", action="store_true", help="build into the open file instead of a fresh one")
    args = p.parse_args(list(argv))

    if not args.keep_file:
        reset_file()
    summary = generate(court_id=None if args.no_court else args.court_id,
                       fighters=[] if args.no_fighters else args.fighters, lineup=args.lineup)
    for fid, info in summary["fighters"].items():  # type: ignore[union-attr]
        print(f"{fid}: height {info['height']} m, top of head {info['measuredTop']} m, {info['tris']:,} tris")
    if summary["court"]:
        print(f"court: {summary['court']}")
    if args.out:
        os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
        bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(args.out))
        print(f"saved {args.out}")
    if args.render:
        render_preview(os.path.abspath(args.render))
        print(f"rendered {args.render}")
    return 0


if __name__ == "__main__":
    sys.exit(main(dd_bpy.args_from_sys()))
