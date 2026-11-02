"""End-to-end pipeline tests with the ``bpy`` module (skipped when bpy is not importable).

Run with a Python that has bpy (Blender 4.5 LTS wheels need Python 3.11)::

    python3.11 -m unittest discover -s Tools/Blender/tests -p "test_*.py" -v
"""

from __future__ import annotations

import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

try:
    import bpy  # type: ignore[import-not-found]
    HAVE_BPY = True
except ImportError:  # pragma: no cover - environment dependent
    HAVE_BPY = False

if HAVE_BPY:
    import dd_conventions as dc
    import dd_export
    import dd_graybox
    import dd_lod
    import dd_validate
    import dd_vat_bake


@unittest.skipUnless(HAVE_BPY, "bpy module not available")
class PipelineTests(unittest.TestCase):
    tmp: str
    blend: str

    @classmethod
    def setUpClass(cls) -> None:
        cls.tmp = tempfile.mkdtemp(prefix="dd_blender_")
        cls.blend = os.path.join(cls.tmp, "graybox.blend")
        dd_graybox.reset_file()
        dd_graybox.generate(court_id="the_tracks")
        bpy.ops.wm.save_as_mainfile(filepath=cls.blend)

    @classmethod
    def tearDownClass(cls) -> None:
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def setUp(self) -> None:
        bpy.ops.wm.open_mainfile(filepath=self.blend)

    # ------------------------------------------------------------------ graybox

    def test_mannequins_have_roster_heights(self) -> None:
        bpy.context.view_layer.update()
        for f in dc.FIGHTERS:
            mesh = bpy.data.objects[dc.lod_name(f"CH_{f.token}_Mannequin", 0)]
            top = max((mesh.matrix_world @ v.co).z for v in mesh.data.vertices)
            bottom = min((mesh.matrix_world @ v.co).z for v in mesh.data.vertices)
            self.assertAlmostEqual(top, f.height_m, places=4, msg=f.id)
            self.assertAlmostEqual(bottom, 0.0, places=4, msg=f.id)
            rig = bpy.data.objects[dc.rig_name(f.id)]
            errors, _ = dc.check_bones([b.name for b in rig.data.bones], {b.name: b.parent.name if b.parent else None for b in rig.data.bones})
            self.assertEqual(errors, [], f.id)

    def test_court_dimensions(self) -> None:
        g = dc.COURT_GEOMETRY
        bpy.context.view_layer.update()

        def bounds(name: str):
            o = bpy.data.objects[name]
            pts = [o.matrix_world @ v.co for v in o.data.vertices]
            return [min(p[i] for p in pts) for i in range(3)], [max(p[i] for p in pts) for i in range(3)]

        lo, hi = bounds("ST_TheTracks_Rim")
        self.assertAlmostEqual((lo[0] + hi[0]) / 2, g.rim_x, places=4)
        self.assertAlmostEqual((lo[2] + hi[2]) / 2, g.rim_y, places=4)
        self.assertAlmostEqual((hi[0] - lo[0]) / 2, g.rim_radius + g.rim_tube_radius, places=4)
        lo, hi = bounds("ST_TheTracks_Backboard")
        self.assertAlmostEqual(lo[0], g.backboard_face_x, places=4)
        self.assertAlmostEqual(hi[1] - lo[1], g.backboard_width, places=4)
        self.assertAlmostEqual(lo[2], g.backboard_bottom, places=4)
        self.assertAlmostEqual(hi[2], g.backboard_top, places=4)
        for name, x in (("ST_TheTracks_LineArc", g.arc_x), ("ST_TheTracks_LineBaseline", g.baseline_x),
                        ("ST_TheTracks_LineLeftBound", g.left_bound_x), ("ST_TheTracks_CheckSpot", g.check_x)):
            lo, hi = bounds(name)
            self.assertAlmostEqual((lo[0] + hi[0]) / 2, x, places=4, msg=name)
        lo, hi = bounds("ST_TheTracks_LineArc")
        self.assertAlmostEqual(hi[1] - lo[1], g.depth_m, places=4)
        for layer in dc.COURT_LAYERS:
            self.assertIn(f"ST_TheTracks_{layer}", bpy.data.collections)

    # ------------------------------------------------------------------ validation

    def test_validate_clean_graybox(self) -> None:
        rep = dd_validate.validate(graybox=True)
        self.assertEqual(rep.errors, [])
        self.assertEqual(rep.warnings, [])
        strict = dd_validate.validate(graybox=False)
        self.assertEqual(strict.errors, [])
        self.assertEqual(sum("under the 40,000 target" in w for w in strict.warnings), 4)
        self.assertEqual(set(rep.fighters), {"Kofi", "Meera", "Juno", "Matas"})

    def test_validate_catches_problems(self) -> None:
        objs = bpy.data.objects
        objs["ST_TheTracks_MidBlock01"].name = "Cube"
        objs["ST_TheTracks_Floor"].scale = (2.0, 1.0, 1.0)
        objs["ST_TheTracks_HoopPole"].rotation_euler = (0.0, 0.0, 0.5)
        # Blow the mid-ground prop budget.
        heavy = objs["ST_TheTracks_MidBlock02"]
        sub = heavy.modifiers.new("Sub", "SUBSURF")
        sub.levels = 6
        # Drop a required bone.
        rig = objs[dc.rig_name("kofi")]
        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.mode_set(mode="EDIT")
        rig.data.edit_bones.remove(rig.data.edit_bones["LeftHand"])
        bpy.ops.object.mode_set(mode="OBJECT")
        # Wrongly named, oversized texture on a real (non-graybox) material.
        mat = bpy.data.materials.new("ST_TheTracks_Bench")
        mat.use_nodes = True
        tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
        tex.image = bpy.data.images.new("bench_diffuse", 4096, 4096)
        objs["ST_TheTracks_MidBlock03"].data.materials.clear()
        objs["ST_TheTracks_MidBlock03"].data.materials.append(mat)

        rep = dd_validate.validate(graybox=True)
        joined = "\n".join(rep.errors)
        for needle in ("'Cube'", "ST_TheTracks_Floor: unapplied scale", "ST_TheTracks_HoopPole: unapplied rotation",
                       "prop ST_TheTracks_MidBlock02", "missing required bone 'LeftHand'",
                       "texture 'bench_diffuse' must be named"):
            self.assertIn(needle, joined)
        report_path = os.path.join(self.tmp, "bad.json")
        self.assertEqual(dd_validate.main(["--graybox", "--quiet", "--report", report_path]), 1)
        with open(report_path, encoding="utf-8") as fh:
            data = json.load(fh)
        self.assertFalse(data["ok"])
        self.assertGreaterEqual(len(data["errors"]), 6)

    def test_texture_set_rules(self) -> None:
        img_dir = os.path.join(self.tmp, "tex")
        os.makedirs(img_dir, exist_ok=True)
        mat = bpy.data.materials.new("ST_TheTracks_Bench")
        mat.use_nodes = True
        for slot, cs in (("BaseColor", "sRGB"), ("Normal", "Non-Color"), ("ORM", "Non-Color")):
            img = bpy.data.images.new(f"ST_TheTracks_Bench_{slot}", 512, 512)
            path = os.path.join(img_dir, f"ST_TheTracks_Bench_{slot}.png")
            img.filepath_raw = path
            img.file_format = "PNG"
            img.save()
            img.colorspace_settings.name = cs
            node = mat.node_tree.nodes.new("ShaderNodeTexImage")
            node.image = img
        block = bpy.data.objects["ST_TheTracks_MidBlock03"]
        block.data.materials.clear()
        block.data.materials.append(mat)
        rep = dd_validate.validate(graybox=True)
        self.assertEqual(rep.errors, [])
        project = os.path.join(self.tmp, "tex_project")
        self.assertEqual(dd_export.export(project, only=["ST_TheTracks_MidBlock03"], graybox=True), 0)
        for slot in ("BaseColor", "Normal", "ORM"):
            self.assertTrue(os.path.isfile(os.path.join(project, "Assets/DunkDuel/Art/Courts/the_tracks/Textures", f"ST_TheTracks_Bench_{slot}.png")))

    # ------------------------------------------------------------------ LOD

    def test_lod_generation(self) -> None:
        rim = bpy.data.objects["ST_TheTracks_Rim"]
        uv_layers = len(rim.data.uv_layers)
        material = rim.data.materials[0].name
        result = dd_lod.generate_lods(rim)
        self.assertIsNone(result.skipped)
        self.assertEqual(result.created, ["ST_TheTracks_Rim_LOD1", "ST_TheTracks_Rim_LOD2"])
        self.assertIn("ST_TheTracks_Rim_LOD0", bpy.data.objects)
        tris = result.tris
        self.assertGreater(tris[0], tris[1])
        self.assertGreater(tris[1], tris[2])
        self.assertLessEqual(tris[1], tris[0] * 0.6)
        for name in result.created:
            obj = bpy.data.objects[name]
            self.assertEqual(len(obj.data.uv_layers), uv_layers, name)
            self.assertEqual(obj.data.materials[0].name, material, name)
            self.assertEqual(len(obj.modifiers), 0, name)
            self.assertEqual(obj.users_collection[0].name, "ST_TheTracks_PlayPlane")
        self.assertEqual(dc.check_lod_chain("rim", tris), [])
        # Re-running replaces instead of duplicating.
        again = dd_lod.generate_lods(bpy.data.objects["ST_TheTracks_Rim_LOD0"])
        self.assertEqual(again.created, result.created)
        self.assertNotIn("ST_TheTracks_Rim_LOD1.001", bpy.data.objects)
        rep = dd_validate.validate(graybox=True)
        self.assertEqual(rep.errors, [])

    def test_lod_default_selection_skips_minimal_cards_and_crowd(self) -> None:
        self.assertEqual(dd_lod.main([]), 0)
        names = set(bpy.data.objects.keys())
        self.assertIn("ST_TheTracks_Rim_LOD2", names)                 # 512 tris: full chain
        self.assertIn("ST_TheTracks_CheckSpot_LOD1", names)           # 92 tris: LOD1 only
        self.assertNotIn("ST_TheTracks_CheckSpot_LOD2", names)
        self.assertIn("ST_TheTracks_Floor", names)                    # 12-tri box: untouched
        self.assertNotIn("ST_TheTracks_Floor_LOD1", names)
        self.assertNotIn("ST_TheTracks_CardFar_LOD1", names)          # backdrop card
        self.assertNotIn("ST_Shared_CrowdA_LOD1", names)              # VAT crowd mesh
        rep = dd_validate.validate(graybox=True)
        self.assertEqual(rep.errors, [])
        self.assertEqual([w for w in rep.warnings if "LOD" in w], [])

    # ------------------------------------------------------------------ VAT

    def test_vat_bake(self) -> None:
        crowd = bpy.data.objects["ST_Shared_CrowdA"]
        out = os.path.join(self.tmp, "vat")
        info = dd_vat_bake.bake(crowd, 1, 30, out, "crowd_cheer", normals=True)
        self.assertEqual(info.frames, 30)
        self.assertEqual(info.vertexCount, len(crowd.data.vertices))
        w, h, rows = dc.vat_layout(info.vertexCount, 30)
        self.assertEqual((info.textureWidth, info.textureHeight, info.rowsPerFrame), (w, h, rows))
        with open(os.path.join(out, "crowd_cheer.vat.json"), encoding="utf-8") as fh:
            sidecar = json.load(fh)
        self.assertEqual(sidecar["vertexCount"], info.vertexCount)
        self.assertEqual(sidecar["fps"], 60.0)
        self.assertEqual(sidecar["space"], "stage")

        # The EXR decodes back to the evaluated mesh (Unity axes) within half-float precision.
        img = bpy.data.images.load(os.path.join(out, "crowd_cheer_pos.exr"))
        self.assertEqual((img.size[0], img.size[1]), (w, h))
        px = list(img.pixels)
        lo, hi = sidecar["boundsMin"], sidecar["boundsMax"]
        bpy.context.scene.frame_set(15)
        dg = bpy.context.evaluated_depsgraph_get()
        mesh = crowd.evaluated_get(dg).to_mesh()
        worst = 0.0
        moved = 0
        for v in range(0, info.vertexCount, 7):
            x, y = dc.vat_texel(v, 15 - 1, w, rows)
            i = (y * w + x) * 4
            decoded = [lo[a] + px[i + a] * (hi[a] - lo[a]) for a in range(3)]
            expect = dc.blender_to_unity_stage(*mesh.vertices[v].co)
            worst = max(worst, max(abs(decoded[a] - expect[a]) for a in range(3)))
            x0, y0 = dc.vat_texel(v, 0, w, rows)
            j = (y0 * w + x0) * 4
            if any(abs(px[i + a] - px[j + a]) > 1e-3 for a in range(3)):
                moved += 1
        crowd.evaluated_get(dg).to_mesh_clear()
        self.assertLess(worst, 2e-3)
        self.assertGreater(moved, 0, "cheer animation should move some vertices between frame 1 and 15")
        self.assertTrue(os.path.isfile(os.path.join(out, "crowd_cheer_nrm.exr")))

        uv2 = crowd.data.uv_layers.get(dd_vat_bake.UV_LAYER_NAME)
        self.assertIsNotNone(uv2)
        self.assertEqual(list(crowd.data.uv_layers).index(uv2), 1, "VAT_ID must be the second UV set (Unity uv2)")
        loop = crowd.data.loops[5]
        u, v = uv2.data[5].uv
        eu, ev = dc.vat_uv2(loop.vertex_index, w, h)
        self.assertAlmostEqual(u, eu, places=5)
        self.assertAlmostEqual(v, ev, places=5)

    # ------------------------------------------------------------------ export

    def test_export_and_reimport(self) -> None:
        project = os.path.join(self.tmp, "project")
        report = os.path.join(self.tmp, "export.json")
        self.assertEqual(dd_export.export(project, graybox=True, report_path=report), 0)
        art = os.path.join(project, "Assets", "DunkDuel", "Art")
        for f in dc.FIGHTERS:
            path = os.path.join(art, "Fighters", f.id, f"CH_{f.token}.fbx")
            self.assertTrue(os.path.isfile(path), path)
            with open(path, "rb") as fh:
                self.assertEqual(fh.read(18), b"Kaydara FBX Binary")
        court_dir = os.path.join(art, "Courts", "the_tracks")
        for prop in ("Rim", "Backboard", "Floor", "LineArc", "MidBlock01", "CardFar"):
            self.assertTrue(os.path.isfile(os.path.join(court_dir, f"ST_TheTracks_{prop}.fbx")), prop)
        self.assertTrue(os.path.isfile(os.path.join(art, "Courts", "shared", "ST_Shared_CrowdA.fbx")))
        with open(os.path.join(court_dir, "ST_TheTracks_layout.json"), encoding="utf-8") as fh:
            layout = json.load(fh)
        layers = {p["name"]: p["layer"] for p in layout["props"]}
        self.assertEqual(layers["ST_TheTracks_Rim"], dc.LAYER_PLAY)
        self.assertEqual(layers["ST_TheTracks_MidBlock01"], dc.LAYER_MID)
        self.assertEqual(layers["ST_TheTracks_CardFar"], dc.LAYER_BACKDROP)
        rim = next(p for p in layout["props"] if p["name"] == "ST_TheTracks_Rim")
        self.assertEqual(rim["positionUnity"], [0.0, 0.0, 0.0])  # placement lives in the mesh (rotation/scale applied)
        with open(report, encoding="utf-8") as fh:
            self.assertTrue(json.load(fh)["validation"]["ok"])

        # Character round trip: rig and height survive.
        dd_graybox.reset_file()
        bpy.ops.import_scene.fbx(filepath=os.path.join(art, "Fighters", "kofi", "CH_Kofi.fbx"), axis_forward="-Z", axis_up="Y")
        arm = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
        names = {b.name for b in arm.data.bones}
        self.assertTrue(set(dc.REQUIRED_BONES) <= names, set(dc.REQUIRED_BONES) - names)
        mesh = next(o for o in bpy.context.scene.objects if o.type == "MESH")
        bpy.context.view_layer.update()
        top = max((mesh.matrix_world @ v.co).z for v in mesh.data.vertices)
        self.assertAlmostEqual(top, 1.96, places=3)

        # Stage round trip: importing with the stage axes restores the court-space placement.
        dd_graybox.reset_file()
        bpy.ops.import_scene.fbx(filepath=os.path.join(court_dir, "ST_TheTracks_Backboard.fbx"), axis_forward="Z", axis_up="Y")
        board = next(o for o in bpy.context.scene.objects if o.type == "MESH")
        bpy.context.view_layer.update()
        pts = [board.matrix_world @ v.co for v in board.data.vertices]
        g = dc.COURT_GEOMETRY
        self.assertAlmostEqual(min(p.x for p in pts), g.backboard_face_x, places=3)
        self.assertAlmostEqual(min(p.z for p in pts), g.backboard_bottom, places=3)
        self.assertAlmostEqual(max(p.z for p in pts), g.backboard_top, places=3)
        self.assertAlmostEqual(max(p.y for p in pts) - min(p.y for p in pts), g.backboard_width, places=3)

    def test_export_refuses_invalid_and_dry_run(self) -> None:
        bpy.data.objects["ST_TheTracks_Floor"].scale = (3.0, 3.0, 3.0)
        project = os.path.join(self.tmp, "refuse")
        self.assertEqual(dd_export.export(project, graybox=True), 1)
        self.assertFalse(os.path.exists(os.path.join(project, "Assets")))
        self.assertEqual(dd_export.export(project, graybox=True, dry_run=True, validate_first=False), 0)
        self.assertFalse(os.path.exists(os.path.join(project, "Assets")))


if __name__ == "__main__":
    unittest.main()
