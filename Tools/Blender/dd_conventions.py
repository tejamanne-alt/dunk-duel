"""Dunk Duel content-pipeline conventions: the single source of truth for every Blender tool.

Pure Python (no ``bpy`` import) so the rules can be unit-tested anywhere and imported by every
script in this folder. The rules come from the design doc's content pipeline:

* meters, applied transforms (rotation 0, scale 1);
* names ``CH_<Fighter>_<Part>`` and ``ST_<Court>_<Prop>``, LOD suffixes ``_LOD0`` .. ``_LOD2``;
* texture sets BaseColor / Normal / ORM (+ optional Emission), 2K maximum;
* 40-60k triangles per fighter at LOD0;
* frame-locked animation sampled at the sim's 60 Hz.

Coordinate frames (see README.md):

* Sim / Unity gameplay space: X toward the hoop, Y up, Z depth away from the gameplay camera.
* Blender court space: X toward the hoop, Z up, Y depth away from the camera, so Blender's front view
  (numpad 1) *is* the gameplay camera. Courts export with ``axis_forward='Z', axis_up='Y'`` which maps
  Blender ``(x, y, z)`` to Unity ``(x, z, y)``.
* Characters face Blender ``-Y`` and export with the standard ``axis_forward='-Z', axis_up='Y'`` so
  they face Unity ``+Z``; gameplay code turns them to face along X.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Dict, Iterable, List, Mapping, Optional, Sequence, Tuple

# ---------------------------------------------------------------------------------------------------
# Units and transforms
# ---------------------------------------------------------------------------------------------------

#: Blender scene unit scale (1.0 == meters). The sim works in meters.
UNIT_SCALE_LENGTH: float = 1.0
UNIT_SYSTEM: str = "METRIC"
#: Tolerance for "applied" rotation (radians) and scale (unitless).
TRANSFORM_EPSILON: float = 1e-4
#: The sim ticks at 60 Hz; frame-locked animation must be authored and exported at the same rate.
ANIMATION_FPS: int = 60

# ---------------------------------------------------------------------------------------------------
# Roster and courts
# ---------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class FighterSpec:
    """A playable fighter: sim id, the CamelCase token used in asset names, and height in meters."""

    id: str
    token: str
    display_name: str
    height_m: float


@dataclass(frozen=True)
class CourtSpec:
    """One of the nine courts: sim id, CamelCase token for asset names, tier."""

    id: str
    token: str
    display_name: str
    tier: str  # "street", "school" or "pro"


FIGHTERS: Tuple[FighterSpec, ...] = (
    FighterSpec("kofi", "Kofi", 'Kofi "Liftoff" Mensah', 1.96),
    FighterSpec("meera", "Meera", 'Meera "Monsoon" Rao', 1.76),
    FighterSpec("juno", "Juno", 'Juno "Glitch" Park', 1.68),
    FighterSpec("matas", "Matas", 'Matas "Landslide" Vilkas', 2.16),
)

COURTS: Tuple[CourtSpec, ...] = (
    CourtSpec("monsoon_bazaar", "MonsoonBazaar", "Monsoon Bazaar", "street"),
    CourtSpec("the_tracks", "TheTracks", "The Tracks", "street"),
    CourtSpec("concrete_coast", "ConcreteCoast", "Concrete Coast", "street"),
    CourtSpec("rivalry_night", "RivalryNight", "Rivalry Night", "school"),
    CourtSpec("lights_out", "LightsOut", "Lights Out", "school"),
    CourtSpec("campus_quad", "CampusQuad", "Campus Quad", "school"),
    CourtSpec("old_barn", "OldBarn", "The Old Barn", "pro"),
    CourtSpec("glass_court", "GlassCourt", "Glass Court", "pro"),
    CourtSpec("the_crown", "TheCrown", "The Crown", "pro"),
)

#: Stage assets reused by every court (crowd meshes, shared props) use this court token.
SHARED_COURT_TOKEN: str = "Shared"

FIGHTER_BY_TOKEN: Dict[str, FighterSpec] = {f.token: f for f in FIGHTERS}
FIGHTER_BY_ID: Dict[str, FighterSpec] = {f.id: f for f in FIGHTERS}
COURT_BY_TOKEN: Dict[str, CourtSpec] = {c.token: c for c in COURTS}
COURT_BY_ID: Dict[str, CourtSpec] = {c.id: c for c in COURTS}

# ---------------------------------------------------------------------------------------------------
# Court geometry (sim coordinates, meters). Mirrors Assets/DunkDuel/Data/Resources/DunkDuelData/tuning.json.
# ---------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class CourtGeometry:
    rim_x: float = 0.0
    rim_y: float = 3.05
    rim_radius: float = 0.23
    rim_tube_radius: float = 0.01
    backboard_face_x: float = 0.38
    backboard_width: float = 1.80
    backboard_bottom: float = 2.90
    backboard_top: float = 3.95
    backboard_thickness: float = 0.03
    baseline_x: float = 1.6
    arc_x: float = -6.75
    check_x: float = -7.6
    free_throw_x: float = -4.2
    left_bound_x: float = -10.5
    depth_m: float = 8.0  # court extent along Blender Y / Unity Z
    line_width: float = 0.05


COURT_GEOMETRY = CourtGeometry()

# ---------------------------------------------------------------------------------------------------
# Naming
# ---------------------------------------------------------------------------------------------------

LOD_SUFFIXES: Tuple[str, ...] = ("_LOD0", "_LOD1", "_LOD2")
MAX_LOD: int = len(LOD_SUFFIXES) - 1

_PART = r"[A-Z][A-Za-z0-9]*(?:_[A-Z][A-Za-z0-9]*)*"
_NAME_RE = re.compile(r"^(?P<family>CH|ST)_(?P<owner>[A-Z][A-Za-z0-9]*)_(?P<part>" + _PART + r")$")
_LOD_RE = re.compile(r"^(?P<base>.+)_LOD(?P<lod>[0-9]+)$")
_BLENDER_DUP_RE = re.compile(r"\.\d{3}$")


@dataclass(frozen=True)
class AssetName:
    """A parsed ``CH_<Fighter>_<Part>[_LODn]`` or ``ST_<Court>_<Prop>[_LODn]`` name."""

    raw: str
    family: str          # "CH" (character) or "ST" (stage)
    owner: str           # fighter or court token
    part: str            # part / prop name, may contain underscores ("Hoop_Rim")
    lod: Optional[int]   # None when the name has no LOD suffix

    @property
    def base(self) -> str:
        """Name without the LOD suffix: the export group key."""
        return f"{self.family}_{self.owner}_{self.part}"

    @property
    def is_character(self) -> bool:
        return self.family == "CH"

    @property
    def is_stage(self) -> bool:
        return self.family == "ST"


class NameError_(ValueError):
    """Raised by :func:`parse_asset_name` with a human-readable reason."""


def parse_asset_name(name: str) -> AssetName:
    """Parse an asset name, raising :class:`NameError_` with the reason if it breaks the convention."""
    if _BLENDER_DUP_RE.search(name):
        raise NameError_(f"'{name}': Blender duplicate suffix (.001) - rename the object")
    lod: Optional[int] = None
    core = name
    m = _LOD_RE.match(name)
    if m:
        lod = int(m.group("lod"))
        core = m.group("base")
        if lod > MAX_LOD:
            raise NameError_(f"'{name}': LOD{lod} is beyond _LOD{MAX_LOD}")
    elif re.search(r"_lod\d+$", name, re.IGNORECASE):
        raise NameError_(f"'{name}': LOD suffix must be upper case, e.g. _LOD1")
    nm = _NAME_RE.match(core)
    if not nm:
        raise NameError_(f"'{name}': expected CH_<Fighter>_<Part> or ST_<Court>_<Prop> (CamelCase tokens)")
    family, owner, part = nm.group("family"), nm.group("owner"), nm.group("part")
    if family == "CH" and owner not in FIGHTER_BY_TOKEN:
        known = ", ".join(sorted(FIGHTER_BY_TOKEN))
        raise NameError_(f"'{name}': unknown fighter '{owner}' (known: {known})")
    if family == "ST" and owner not in COURT_BY_TOKEN and owner != SHARED_COURT_TOKEN:
        known = ", ".join(sorted(COURT_BY_TOKEN) + [SHARED_COURT_TOKEN])
        raise NameError_(f"'{name}': unknown court '{owner}' (known: {known})")
    return AssetName(raw=name, family=family, owner=owner, part=part, lod=lod)


def is_valid_asset_name(name: str) -> bool:
    try:
        parse_asset_name(name)
        return True
    except NameError_:
        return False


def lod_name(base: str, lod: int) -> str:
    """``lod_name("ST_TheTracks_Bench", 1) == "ST_TheTracks_Bench_LOD1"``."""
    if not 0 <= lod <= MAX_LOD:
        raise ValueError(f"lod must be 0..{MAX_LOD}")
    return base + LOD_SUFFIXES[lod]


def strip_lod(name: str) -> str:
    m = _LOD_RE.match(name)
    return m.group("base") if m else name


def fighter_token(fighter_id: str) -> str:
    return FIGHTER_BY_ID[fighter_id].token


def court_token(court_id: str) -> str:
    return COURT_BY_ID[court_id].token


def rig_name(fighter_id: str) -> str:
    """Armature object name for a fighter: ``CH_<Fighter>_Rig``."""
    return f"CH_{fighter_token(fighter_id)}_Rig"


# ---------------------------------------------------------------------------------------------------
# Court depth layers (Blender collections inside a court file)
# ---------------------------------------------------------------------------------------------------

LAYER_PLAY = "PlayPlane"
LAYER_MID = "MidGround"
LAYER_BACKDROP = "Backdrop"
LAYER_CROWD = "Crowd"
COURT_LAYERS: Tuple[str, ...] = (LAYER_PLAY, LAYER_MID, LAYER_BACKDROP, LAYER_CROWD)

# ---------------------------------------------------------------------------------------------------
# Triangle budgets
# ---------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class Budget:
    """Triangle budget. ``max_tris`` is a hard limit (error); ``min_tris`` is a quality target (warning)."""

    max_tris: int
    min_tris: int = 0


#: Whole fighter (all CH_<Fighter>_* meshes at one LOD), from the design doc for LOD0.
FIGHTER_BUDGETS: Mapping[int, Budget] = {
    0: Budget(max_tris=60_000, min_tris=40_000),
    1: Budget(max_tris=30_000),
    2: Budget(max_tris=12_000),
}

#: Per court depth layer (sum of meshes in the layer collection at LOD0).
COURT_LAYER_BUDGETS: Mapping[str, Budget] = {
    LAYER_PLAY: Budget(max_tris=120_000),     # hoop, floor, fence, hero props: full detail
    LAYER_MID: Budget(max_tris=40_000),       # low-poly baked mid-ground
    LAYER_BACKDROP: Budget(max_tris=2_000),   # painted cards
    LAYER_CROWD: Budget(max_tris=30_000),     # instanced crowd source meshes + rigged hero spectators
}
#: Whole court at LOD0 across all layers.
COURT_TOTAL_BUDGET = Budget(max_tris=180_000)

#: Single stage prop (per mesh group at LOD0), by layer.
PROP_BUDGETS: Mapping[str, Budget] = {
    LAYER_PLAY: Budget(max_tris=20_000),
    LAYER_MID: Budget(max_tris=6_000),
    LAYER_BACKDROP: Budget(max_tris=200),
    LAYER_CROWD: Budget(max_tris=10_000),
}
#: One GPU-instanced crowd spectator mesh (VAT-driven); hundreds are drawn.
CROWD_INSTANCE_BUDGET = Budget(max_tris=1_500)

#: Automatic LOD ratios (Decimate collapse) for stage props; index = LOD level.
PROP_LOD_RATIOS: Tuple[float, ...] = (1.0, 0.5, 0.25)
#: Props under this many triangles at LOD0 are already minimal: no automatic LODs.
LOD_MIN_SOURCE_TRIS: int = 64
#: Automatic LOD generation stops before a level would drop under this many triangles.
LOD_MIN_TRIS: int = 24


def planned_lod_levels(lod0_tris: int, ratios: Sequence[float] = PROP_LOD_RATIOS) -> List[int]:
    """LOD levels (>= 1) worth generating for a prop with ``lod0_tris`` triangles."""
    if lod0_tris < LOD_MIN_SOURCE_TRIS:
        return []
    levels: List[int] = []
    for lod in range(1, len(ratios)):
        if lod0_tris * ratios[lod] < LOD_MIN_TRIS:
            break
        levels.append(lod)
    return levels
#: Each LOD should be at most this fraction of the previous level (warning otherwise).
LOD_STEP_MAX_FRACTION: float = 0.75


@dataclass
class BudgetResult:
    key: str
    tris: int
    budget: Budget
    errors: List[str] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)


def check_budget(key: str, tris: int, budget: Budget, *, enforce_min: bool = True) -> BudgetResult:
    """Compare a triangle count against a budget."""
    r = BudgetResult(key=key, tris=tris, budget=budget)
    if tris > budget.max_tris:
        r.errors.append(f"{key}: {tris:,} triangles exceeds budget {budget.max_tris:,}")
    if enforce_min and budget.min_tris and tris < budget.min_tris:
        r.warnings.append(f"{key}: {tris:,} triangles is under the {budget.min_tris:,} target")
    return r


def check_lod_chain(base: str, tris_by_lod: Mapping[int, int]) -> List[str]:
    """Warnings for gaps in a LOD chain or LODs that do not shrink enough."""
    warnings: List[str] = []
    levels = sorted(tris_by_lod)
    if levels and levels[0] != 0:
        warnings.append(f"{base}: LOD chain starts at LOD{levels[0]}, expected _LOD0")
    for a, b in zip(levels, levels[1:]):
        if b != a + 1:
            warnings.append(f"{base}: LOD chain skips from LOD{a} to LOD{b}")
        if tris_by_lod[a] > 0 and tris_by_lod[b] > tris_by_lod[a] * LOD_STEP_MAX_FRACTION:
            warnings.append(
                f"{base}: LOD{b} has {tris_by_lod[b]:,} triangles, more than "
                f"{int(LOD_STEP_MAX_FRACTION * 100)}% of LOD{a} ({tris_by_lod[a]:,})"
            )
    return warnings


# ---------------------------------------------------------------------------------------------------
# Textures
# ---------------------------------------------------------------------------------------------------

TEXTURE_SLOTS_REQUIRED: Tuple[str, ...] = ("BaseColor", "Normal", "ORM")
TEXTURE_SLOTS_OPTIONAL: Tuple[str, ...] = ("Emission",)
TEXTURE_SLOTS: Tuple[str, ...] = TEXTURE_SLOTS_REQUIRED + TEXTURE_SLOTS_OPTIONAL
TEXTURE_MAX_SIZE: int = 2048
TEXTURE_EXTENSIONS: Tuple[str, ...] = (".png", ".tga", ".exr", ".tif", ".tiff")
#: Color spaces expected per slot (Blender image colorspace names).
TEXTURE_COLORSPACE: Mapping[str, str] = {
    "BaseColor": "sRGB",
    "Emission": "sRGB",
    "Normal": "Non-Color",
    "ORM": "Non-Color",
}
#: Materials ending with this suffix are untextured graybox stand-ins (texture checks relaxed).
GRAYBOX_MATERIAL_SUFFIX: str = "_Graybox"

_TEX_RE = re.compile(r"^(?P<set>.+)_(?P<slot>BaseColor|Normal|ORM|Emission)$")


def parse_texture_name(stem: str) -> Optional[Tuple[str, str]]:
    """``"CH_Kofi_Skin_ORM" -> ("CH_Kofi_Skin", "ORM")``; ``None`` if the stem has no slot suffix."""
    m = _TEX_RE.match(stem)
    if not m:
        return None
    return m.group("set"), m.group("slot")


def is_power_of_two(n: int) -> bool:
    return n > 0 and (n & (n - 1)) == 0


def check_texture_size(name: str, width: int, height: int) -> List[str]:
    """Errors for a texture over the 2K limit or with non power-of-two sides (mipmaps, ASTC)."""
    errors: List[str] = []
    if width > TEXTURE_MAX_SIZE or height > TEXTURE_MAX_SIZE:
        errors.append(f"{name}: {width}x{height} exceeds {TEXTURE_MAX_SIZE}x{TEXTURE_MAX_SIZE}")
    if not (is_power_of_two(width) and is_power_of_two(height)):
        errors.append(f"{name}: {width}x{height} is not a power of two")
    return errors


def check_texture_set(material: str, slots_found: Iterable[str]) -> Tuple[List[str], List[str]]:
    """(errors, warnings) for one material's texture set."""
    found = set(slots_found)
    errors: List[str] = []
    warnings: List[str] = []
    if material.endswith(GRAYBOX_MATERIAL_SUFFIX):
        if found:
            warnings.append(f"{material}: graybox material has textures ({', '.join(sorted(found))})")
        return errors, warnings
    missing = [s for s in TEXTURE_SLOTS_REQUIRED if s not in found]
    if missing and not found:
        warnings.append(f"{material}: no textures (name it *{GRAYBOX_MATERIAL_SUFFIX} if it is a graybox stand-in)")
    elif missing:
        errors.append(f"{material}: texture set missing {', '.join(missing)}")
    unknown = sorted(found - set(TEXTURE_SLOTS))
    if unknown:
        errors.append(f"{material}: unknown texture slots {', '.join(unknown)}")
    return errors, warnings


# ---------------------------------------------------------------------------------------------------
# Skeleton
# ---------------------------------------------------------------------------------------------------

#: Root bone carrying root motion (baked from Blender into fixed-point curves at import).
ROOT_BONE = "Root"

#: Required fighter bones, named after Unity's HumanBodyBones so the importer maps them 1:1.
REQUIRED_BONES: Tuple[str, ...] = (
    ROOT_BONE,
    "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head",
    "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
    "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
    "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes",
    "RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes",
)

#: Optional bones (fingers, face, ball sockets for dribbling and frame-locked props).
OPTIONAL_BONES: Tuple[str, ...] = tuple(
    [f"{side}{finger}{seg}" for side in ("Left", "Right")
     for finger in ("Thumb", "Index", "Middle", "Ring", "Little")
     for seg in ("Proximal", "Intermediate", "Distal")]
    + ["Jaw", "LeftEye", "RightEye", "BallSocketLeft", "BallSocketRight"]
)

#: Canonical bone -> Unity HumanBodyBones enum name (None: not a humanoid bone, kept as a generic transform).
UNITY_HUMAN_BONE: Mapping[str, Optional[str]] = {
    **{b: b for b in REQUIRED_BONES if b != ROOT_BONE},
    ROOT_BONE: None,
    **{b: b for b in OPTIONAL_BONES if not b.startswith("BallSocket")},
    "BallSocketLeft": None,
    "BallSocketRight": None,
}

#: Expected parent of each required bone (Root is the armature's top bone).
BONE_PARENTS: Mapping[str, Optional[str]] = {
    ROOT_BONE: None,
    "Hips": ROOT_BONE,
    "Spine": "Hips", "Chest": "Spine", "UpperChest": "Chest", "Neck": "UpperChest", "Head": "Neck",
    "LeftShoulder": "UpperChest", "LeftUpperArm": "LeftShoulder", "LeftLowerArm": "LeftUpperArm", "LeftHand": "LeftLowerArm",
    "RightShoulder": "UpperChest", "RightUpperArm": "RightShoulder", "RightLowerArm": "RightUpperArm", "RightHand": "RightLowerArm",
    "LeftUpperLeg": "Hips", "LeftLowerLeg": "LeftUpperLeg", "LeftFoot": "LeftLowerLeg", "LeftToes": "LeftFoot",
    "RightUpperLeg": "Hips", "RightLowerLeg": "RightUpperLeg", "RightFoot": "RightLowerLeg", "RightToes": "RightFoot",
}

#: Common names from other rigs -> canonical names, used to suggest renames.
BONE_ALIASES: Mapping[str, str] = {
    # Mixamo
    "mixamorig:Hips": "Hips", "mixamorig:Spine": "Spine", "mixamorig:Spine1": "Chest", "mixamorig:Spine2": "UpperChest",
    "mixamorig:Neck": "Neck", "mixamorig:Head": "Head",
    "mixamorig:LeftShoulder": "LeftShoulder", "mixamorig:LeftArm": "LeftUpperArm", "mixamorig:LeftForeArm": "LeftLowerArm",
    "mixamorig:LeftHand": "LeftHand", "mixamorig:RightShoulder": "RightShoulder", "mixamorig:RightArm": "RightUpperArm",
    "mixamorig:RightForeArm": "RightLowerArm", "mixamorig:RightHand": "RightHand",
    "mixamorig:LeftUpLeg": "LeftUpperLeg", "mixamorig:LeftLeg": "LeftLowerLeg", "mixamorig:LeftFoot": "LeftFoot",
    "mixamorig:LeftToeBase": "LeftToes", "mixamorig:RightUpLeg": "RightUpperLeg", "mixamorig:RightLeg": "RightLowerLeg",
    "mixamorig:RightFoot": "RightFoot", "mixamorig:RightToeBase": "RightToes",
    # Rigify deform bones
    "DEF-spine": "Hips", "DEF-spine.001": "Spine", "DEF-spine.002": "Chest", "DEF-spine.003": "UpperChest",
    "DEF-spine.004": "Neck", "DEF-spine.006": "Head",
    "DEF-shoulder.L": "LeftShoulder", "DEF-upper_arm.L": "LeftUpperArm", "DEF-forearm.L": "LeftLowerArm", "DEF-hand.L": "LeftHand",
    "DEF-shoulder.R": "RightShoulder", "DEF-upper_arm.R": "RightUpperArm", "DEF-forearm.R": "RightLowerArm", "DEF-hand.R": "RightHand",
    "DEF-thigh.L": "LeftUpperLeg", "DEF-shin.L": "LeftLowerLeg", "DEF-foot.L": "LeftFoot", "DEF-toe.L": "LeftToes",
    "DEF-thigh.R": "RightUpperLeg", "DEF-shin.R": "RightLowerLeg", "DEF-foot.R": "RightFoot", "DEF-toe.R": "RightToes",
    # Blender metarig / generic
    "hips": "Hips", "spine": "Spine", "chest": "Chest", "neck": "Neck", "head": "Head",
    "shoulder.L": "LeftShoulder", "upper_arm.L": "LeftUpperArm", "forearm.L": "LeftLowerArm", "hand.L": "LeftHand",
    "shoulder.R": "RightShoulder", "upper_arm.R": "RightUpperArm", "forearm.R": "RightLowerArm", "hand.R": "RightHand",
    "thigh.L": "LeftUpperLeg", "shin.L": "LeftLowerLeg", "foot.L": "LeftFoot", "toe.L": "LeftToes",
    "thigh.R": "RightUpperLeg", "shin.R": "RightLowerLeg", "foot.R": "RightFoot", "toe.R": "RightToes",
}

#: Mobile skinning: Unity's default quality setting keeps 4 influences per vertex.
MAX_BONE_INFLUENCES: int = 4
#: Warn above this many deform bones per fighter (GPU skinning cost on mid-tier phones).
MAX_DEFORM_BONES: int = 90


def check_bones(bone_names: Sequence[str], parents: Optional[Mapping[str, Optional[str]]] = None) -> Tuple[List[str], List[str]]:
    """(errors, warnings) for a fighter armature's bone list (and optional child -> parent map)."""
    errors: List[str] = []
    warnings: List[str] = []
    names = list(bone_names)
    present = set(names)
    dupes = sorted({n for n in names if names.count(n) > 1})
    if dupes:
        errors.append("duplicate bone names: " + ", ".join(dupes))
    for bone in REQUIRED_BONES:
        if bone in present:
            continue
        aliases = sorted(a for a, c in BONE_ALIASES.items() if c == bone and a in present)
        hint = f" (rename '{aliases[0]}' to '{bone}')" if aliases else ""
        errors.append(f"missing required bone '{bone}'{hint}")
    known = set(REQUIRED_BONES) | set(OPTIONAL_BONES)
    extras = sorted(n for n in present if n not in known)
    if extras:
        warnings.append(f"{len(extras)} non-canonical bones kept as generic transforms: " + ", ".join(extras[:12])
                        + (" ..." if len(extras) > 12 else ""))
    if parents is not None:
        for bone, expected in BONE_PARENTS.items():
            if bone in present and expected is not None and expected in present:
                actual = parents.get(bone)
                if actual != expected:
                    errors.append(f"bone '{bone}' should be parented to '{expected}', not '{actual}'")
        if ROOT_BONE in present and parents.get(ROOT_BONE) is not None:
            errors.append(f"'{ROOT_BONE}' must be the top bone (parent is '{parents.get(ROOT_BONE)}')")
    return errors, warnings


# ---------------------------------------------------------------------------------------------------
# Export settings and Unity folders
# ---------------------------------------------------------------------------------------------------

UNITY_ART_ROOT = "Assets/DunkDuel/Art"


def unity_fighter_dir(fighter_id: str) -> str:
    return f"{UNITY_ART_ROOT}/Fighters/{fighter_id}"


def unity_court_dir(court_id: str) -> str:
    return f"{UNITY_ART_ROOT}/Courts/{court_id}"


UNITY_SHARED_STAGE_DIR = f"{UNITY_ART_ROOT}/Courts/shared"
UNITY_CROWD_DIR = f"{UNITY_ART_ROOT}/Crowd"


def unity_dir_for(asset: AssetName) -> str:
    """Destination folder (relative to the Unity project) the AssetPostprocessor keys its settings on."""
    if asset.is_character:
        return unity_fighter_dir(FIGHTER_BY_TOKEN[asset.owner].id)
    if asset.owner == SHARED_COURT_TOKEN:
        return UNITY_SHARED_STAGE_DIR
    return unity_court_dir(COURT_BY_TOKEN[asset.owner].id)


#: FBX exporter settings shared by every asset (bpy.ops.export_scene.fbx keyword arguments).
FBX_COMMON: Mapping[str, object] = {
    "use_selection": True,
    "apply_unit_scale": True,
    "apply_scale_options": "FBX_SCALE_ALL",
    "global_scale": 1.0,
    "axis_up": "Y",
    "use_mesh_modifiers": True,
    "mesh_smooth_type": "FACE",
    "use_tspace": False,
    "use_triangles": False,
    "add_leaf_bones": False,
    "primary_bone_axis": "Y",
    "secondary_bone_axis": "X",
    "use_armature_deform_only": True,
    "armature_nodetype": "NULL",
    "path_mode": "STRIP",
    "embed_textures": False,
    "object_types": {"MESH", "ARMATURE", "EMPTY"},
}

#: Characters face Blender -Y; standard Unity mapping, axis conversion baked by Unity's importer
#: (ModelImporter.bakeAxisConversion) because baking it in Blender is unreliable for armatures.
FBX_CHARACTER: Mapping[str, object] = {
    **FBX_COMMON,
    "axis_forward": "-Z",
    "bake_space_transform": False,
    "bake_anim": True,
    "bake_anim_use_all_bones": True,
    "bake_anim_use_nla_strips": False,
    "bake_anim_use_all_actions": True,
    "bake_anim_force_startend_keying": True,
    "bake_anim_step": 1.0,
    "bake_anim_simplify_factor": 0.0,  # frame-locked sampling: never simplify curves
}

#: Courts and stage props: Blender front view == gameplay camera; maps Blender (x, y, z) -> Unity (x, z, y)
#: and bakes the conversion into vertices so Unity objects get identity rotations.
FBX_STAGE: Mapping[str, object] = {
    **FBX_COMMON,
    "axis_forward": "Z",
    "bake_space_transform": True,
    "bake_anim": False,
}


def blender_to_unity_stage(x: float, y: float, z: float) -> Tuple[float, float, float]:
    """Position mapping used for stage exports (and stage VAT data): Blender (x, y, z) -> Unity (x, z, y)."""
    return (x, z, y)


def blender_to_unity_character(x: float, y: float, z: float) -> Tuple[float, float, float]:
    """Position mapping for character exports with axis_forward='-Z': Blender (x, y, z) -> Unity (-x, z, -y)."""
    return (-x, z, -y)


def report_skeleton() -> Dict[str, object]:
    """Machine-readable summary of the rules, embedded in validation reports."""
    return {
        "unitScale": UNIT_SCALE_LENGTH,
        "fps": ANIMATION_FPS,
        "fighters": {f.id: {"token": f.token, "heightM": f.height_m} for f in FIGHTERS},
        "courts": {c.id: {"token": c.token, "tier": c.tier} for c in COURTS},
        "fighterBudgets": {f"LOD{k}": {"max": v.max_tris, "min": v.min_tris} for k, v in FIGHTER_BUDGETS.items()},
        "courtLayerBudgets": {k: v.max_tris for k, v in COURT_LAYER_BUDGETS.items()},
        "courtTotal": COURT_TOTAL_BUDGET.max_tris,
        "propLodRatios": list(PROP_LOD_RATIOS),
        "textureMax": TEXTURE_MAX_SIZE,
        "textureSlots": {"required": list(TEXTURE_SLOTS_REQUIRED), "optional": list(TEXTURE_SLOTS_OPTIONAL)},
    }


def vat_layout(vertex_count: int, frames: int, max_width: int = TEXTURE_MAX_SIZE) -> Tuple[int, int, int]:
    """Vertex-animation-texture layout: ``(width, height, rows_per_frame)``.

    One column per vertex, one row per frame. Width is the vertex count rounded up to a power of two and capped
    at ``max_width``; when a frame needs more than ``max_width`` columns it wraps over ``rows_per_frame`` rows.
    Vertex ``v`` at frame ``f`` lives at texel ``(v % width, f * rows_per_frame + v // width)``.
    """
    if vertex_count <= 0 or frames <= 0:
        raise ValueError("vertex_count and frames must be positive")
    if not is_power_of_two(max_width):
        raise ValueError("max_width must be a power of two")
    width = 1
    while width < vertex_count and width < max_width:
        width <<= 1
    rows = -(-vertex_count // width)  # ceil
    return width, rows * frames, rows


def vat_texel(vertex: int, frame: int, width: int, rows_per_frame: int) -> Tuple[int, int]:
    """Texel ``(x, y)`` of a vertex at a frame (y counts from the bottom row, like UV v)."""
    return vertex % width, frame * rows_per_frame + vertex // width


def vat_uv2(vertex: int, width: int, height: int) -> Tuple[float, float]:
    """UV2 written into the mesh: the vertex's texel center at frame 0."""
    return (vertex % width + 0.5) / width, (vertex // width + 0.5) / height


def script_args(argv: Sequence[str]) -> List[str]:
    """Arguments after ``--`` (Blender passes its own flags before it)."""
    argv = list(argv)
    return argv[argv.index("--") + 1:] if "--" in argv else []
