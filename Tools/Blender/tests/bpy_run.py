"""Emulate ``blender -b <file.blend> -P <script.py> -- <args>`` with the pip ``bpy`` module.

Usage::

    python3.11 Tools/Blender/tests/bpy_run.py <file.blend|-> <script.py> -- <script args>

``-`` starts from an empty factory file. Exits with the script's ``main()`` return code.
"""

from __future__ import annotations

import importlib.util
import os
import sys

import bpy  # type: ignore[import-not-found]


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    blend, script = sys.argv[1], sys.argv[2]
    rest = sys.argv[3:]
    args = rest[rest.index("--") + 1:] if "--" in rest else rest
    if blend == "-":
        bpy.ops.wm.read_factory_settings(use_empty=True)
    else:
        bpy.ops.wm.open_mainfile(filepath=os.path.abspath(blend))
    script_dir = os.path.dirname(os.path.abspath(script))
    if script_dir not in sys.path:
        sys.path.insert(0, script_dir)
    spec = importlib.util.spec_from_file_location(os.path.splitext(os.path.basename(script))[0], script)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    sys.modules[spec.name] = module  # dataclasses resolve annotations through sys.modules
    spec.loader.exec_module(module)
    return int(module.main(args))


if __name__ == "__main__":
    sys.exit(main())
