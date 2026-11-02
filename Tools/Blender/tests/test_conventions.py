"""Unit tests for dd_conventions (pure Python, no Blender needed).

Run: python3 -m unittest discover -s Tools/Blender/tests -p "test_*.py"
"""

from __future__ import annotations

import os
import sys
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

import dd_conventions as dc  # noqa: E402


class NamingTests(unittest.TestCase):
    def test_character_names(self) -> None:
        a = dc.parse_asset_name("CH_Kofi_Head_LOD0")
        self.assertEqual((a.family, a.owner, a.part, a.lod, a.base), ("CH", "Kofi", "Head", 0, "CH_Kofi_Head"))
        self.assertTrue(a.is_character)
        self.assertIsNone(dc.parse_asset_name("CH_Matas_Rig").lod)

    def test_stage_names_with_multi_part_props(self) -> None:
        a = dc.parse_asset_name("ST_TheTracks_Hoop_Rim_LOD2")
        self.assertEqual((a.owner, a.part, a.lod, a.base), ("TheTracks", "Hoop_Rim", 2, "ST_TheTracks_Hoop_Rim"))
        self.assertEqual(dc.parse_asset_name("ST_Shared_CrowdA").owner, "Shared")

    def test_rejects_bad_names(self) -> None:
        bad = [
            "Cube",                     # no family
            "CH_kofi_Head",             # lower-case token
            "CH_Steve_Head",            # unknown fighter
            "ST_Moon_Floor",            # unknown court
            "CH_Kofi_Head_LOD3",        # beyond LOD2
            "CH_Kofi_Head_lod1",        # lower-case suffix
            "ST_TheTracks_Bench.001",   # Blender duplicate suffix
            "CH_Kofi_head",             # part must be CamelCase
            "CH_Kofi_",                 # empty part
        ]
        for name in bad:
            with self.subTest(name=name):
                self.assertFalse(dc.is_valid_asset_name(name))
                with self.assertRaises(dc.NameError_):
                    dc.parse_asset_name(name)

    def test_every_roster_and_court_token_parses(self) -> None:
        for f in dc.FIGHTERS:
            self.assertTrue(dc.is_valid_asset_name(f"CH_{f.token}_Body_LOD0"))
            self.assertEqual(dc.rig_name(f.id), f"CH_{f.token}_Rig")
        for c in dc.COURTS:
            self.assertTrue(dc.is_valid_asset_name(f"ST_{c.token}_Floor"))
        self.assertEqual(len(dc.COURTS), 9)
        self.assertEqual({c.tier for c in dc.COURTS}, {"street", "school", "pro"})

    def test_lod_helpers(self) -> None:
        self.assertEqual(dc.lod_name("ST_TheTracks_Bench", 1), "ST_TheTracks_Bench_LOD1")
        self.assertEqual(dc.strip_lod("ST_TheTracks_Bench_LOD1"), "ST_TheTracks_Bench")
        self.assertEqual(dc.strip_lod("ST_TheTracks_Bench"), "ST_TheTracks_Bench")
        with self.assertRaises(ValueError):
            dc.lod_name("X", 3)

    def test_heights_match_roster(self) -> None:
        self.assertEqual({f.id: f.height_m for f in dc.FIGHTERS}, {"kofi": 1.96, "meera": 1.76, "juno": 1.68, "matas": 2.16})


class BudgetTests(unittest.TestCase):
    def test_fighter_lod0_window(self) -> None:
        b = dc.FIGHTER_BUDGETS[0]
        self.assertFalse(dc.check_budget("k", 50_000, b).errors)
        self.assertFalse(dc.check_budget("k", 50_000, b).warnings)
        self.assertTrue(dc.check_budget("k", 61_000, b).errors)
        self.assertTrue(dc.check_budget("k", 20_000, b).warnings)
        self.assertFalse(dc.check_budget("k", 20_000, b, enforce_min=False).warnings)

    def test_lod_chain(self) -> None:
        self.assertEqual(dc.check_lod_chain("p", {0: 1000, 1: 500, 2: 250}), [])
        self.assertTrue(dc.check_lod_chain("p", {0: 1000, 1: 900}))           # not shrinking enough
        self.assertTrue(dc.check_lod_chain("p", {0: 1000, 2: 200}))           # gap
        self.assertTrue(dc.check_lod_chain("p", {1: 500}))                    # no LOD0

    def test_planned_lod_levels(self) -> None:
        self.assertEqual(dc.planned_lod_levels(512), [1, 2])
        self.assertEqual(dc.planned_lod_levels(80), [1])            # 80 * 0.25 = 20 < 24: stop after LOD1
        self.assertEqual(dc.planned_lod_levels(12), [])             # a box is already minimal
        self.assertEqual(dc.planned_lod_levels(dc.LOD_MIN_SOURCE_TRIS - 1), [])

    def test_lod_ratios_decrease(self) -> None:
        r = dc.PROP_LOD_RATIOS
        self.assertEqual(r[0], 1.0)
        self.assertTrue(all(b < a for a, b in zip(r, r[1:])))
        self.assertEqual(len(r), dc.MAX_LOD + 1)


class TextureTests(unittest.TestCase):
    def test_parse(self) -> None:
        self.assertEqual(dc.parse_texture_name("CH_Kofi_Skin_ORM"), ("CH_Kofi_Skin", "ORM"))
        self.assertEqual(dc.parse_texture_name("ST_TheTracks_Floor_BaseColor"), ("ST_TheTracks_Floor", "BaseColor"))
        self.assertIsNone(dc.parse_texture_name("kofi_diffuse"))

    def test_size(self) -> None:
        self.assertEqual(dc.check_texture_size("t", 2048, 2048), [])
        self.assertEqual(dc.check_texture_size("t", 1024, 512), [])
        self.assertTrue(dc.check_texture_size("t", 4096, 4096))
        self.assertTrue(dc.check_texture_size("t", 1000, 1000))

    def test_sets(self) -> None:
        self.assertEqual(dc.check_texture_set("CH_Kofi_Skin", ["BaseColor", "Normal", "ORM"]), ([], []))
        self.assertEqual(dc.check_texture_set("CH_Kofi_Skin", ["BaseColor", "Normal", "ORM", "Emission"]), ([], []))
        errors, _ = dc.check_texture_set("CH_Kofi_Skin", ["BaseColor", "Normal"])
        self.assertTrue(any("ORM" in e for e in errors))
        errors, warnings = dc.check_texture_set("CH_Kofi_Skin", [])
        self.assertFalse(errors)
        self.assertTrue(warnings)
        self.assertEqual(dc.check_texture_set("CH_Kofi_Mannequin_Graybox", []), ([], []))


class BoneTests(unittest.TestCase):
    def _parents(self) -> dict:
        return dict(dc.BONE_PARENTS)

    def test_complete_rig_passes(self) -> None:
        errors, warnings = dc.check_bones(list(dc.REQUIRED_BONES), self._parents())
        self.assertEqual(errors, [])
        self.assertEqual(warnings, [])

    def test_missing_bone_suggests_alias(self) -> None:
        names = [b for b in dc.REQUIRED_BONES if b != "LeftUpperArm"] + ["mixamorig:LeftArm"]
        errors, warnings = dc.check_bones(names)
        self.assertTrue(any("LeftUpperArm" in e and "mixamorig:LeftArm" in e for e in errors))
        self.assertTrue(warnings)  # the alias is a non-canonical extra

    def test_bad_hierarchy(self) -> None:
        parents = self._parents()
        parents["LeftHand"] = "Hips"
        errors, _ = dc.check_bones(list(dc.REQUIRED_BONES), parents)
        self.assertTrue(any("LeftHand" in e for e in errors))
        parents = self._parents()
        parents[dc.ROOT_BONE] = "Hips"
        errors, _ = dc.check_bones(list(dc.REQUIRED_BONES), parents)
        self.assertTrue(any(dc.ROOT_BONE in e for e in errors))

    def test_duplicates(self) -> None:
        errors, _ = dc.check_bones(list(dc.REQUIRED_BONES) + ["Head"])
        self.assertTrue(any("duplicate" in e for e in errors))

    def test_unity_mapping_covers_required(self) -> None:
        for b in dc.REQUIRED_BONES:
            self.assertIn(b, dc.UNITY_HUMAN_BONE)
        self.assertIsNone(dc.UNITY_HUMAN_BONE[dc.ROOT_BONE])
        self.assertEqual(dc.UNITY_HUMAN_BONE["LeftUpperArm"], "LeftUpperArm")
        for alias, canonical in dc.BONE_ALIASES.items():
            self.assertIn(canonical, dc.REQUIRED_BONES, alias)


class AxisAndVatTests(unittest.TestCase):
    def test_stage_axis_mapping(self) -> None:
        # Blender court: X toward hoop, Y depth, Z up  ->  Unity: X toward hoop, Y up, Z depth.
        self.assertEqual(dc.blender_to_unity_stage(1.0, 2.0, 3.0), (1.0, 3.0, 2.0))
        self.assertEqual(dc.FBX_STAGE["axis_forward"], "Z")
        self.assertEqual(dc.FBX_STAGE["axis_up"], "Y")
        self.assertEqual(dc.FBX_CHARACTER["axis_forward"], "-Z")
        self.assertEqual(dc.FBX_CHARACTER["bake_anim_simplify_factor"], 0.0)

    def test_vat_layout(self) -> None:
        self.assertEqual(dc.vat_layout(148, 30), (256, 30, 1))
        self.assertEqual(dc.vat_layout(1, 1), (1, 1, 1))
        w, h, rows = dc.vat_layout(5000, 30, 2048)
        self.assertEqual((w, rows, h), (2048, 3, 90))
        self.assertEqual(dc.vat_texel(4100, 2, w, rows), (4100 % 2048, 2 * 3 + 2))
        u, v = dc.vat_uv2(4100, w, h)
        self.assertAlmostEqual(u, (4100 % 2048 + 0.5) / 2048)
        self.assertAlmostEqual(v, (2 + 0.5) / 90)
        with self.assertRaises(ValueError):
            dc.vat_layout(10, 10, 1000)

    def test_unity_dirs(self) -> None:
        self.assertEqual(dc.unity_dir_for(dc.parse_asset_name("CH_Juno_Rig")), "Assets/DunkDuel/Art/Fighters/juno")
        self.assertEqual(dc.unity_dir_for(dc.parse_asset_name("ST_OldBarn_Organ")), "Assets/DunkDuel/Art/Courts/old_barn")
        self.assertEqual(dc.unity_dir_for(dc.parse_asset_name("ST_Shared_CrowdA")), "Assets/DunkDuel/Art/Courts/shared")

    def test_script_args(self) -> None:
        self.assertEqual(dc.script_args(["blender", "-b", "f.blend", "-P", "x.py", "--", "--out", "o"]), ["--out", "o"])
        self.assertEqual(dc.script_args(["python"]), [])


if __name__ == "__main__":
    unittest.main()
