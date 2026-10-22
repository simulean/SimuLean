"""
Combined preview: the IndustrialBuilding kit with the OverheadCrane kit running
on its column brackets.  Uses the rigged kits saved by the two build scripts
(build/IndustrialBuilding.blend, build/OverheadCrane.blend) with their baked
runtime materials, so what it renders is what Unity imports.

Run (after both kit builds):
    blender -b --factory-startup -P Tools/Blender/assets/workshop_preview.py -- [--quick]

Outputs Tools/Blender/previews/workshop_*.png
"""

import os
import re
import sys
import argparse
from math import pi

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import bpy                                   # noqa: E402
from mathutils import Matrix                 # noqa: E402
import simulean_kit as K                     # noqa: E402
import simulean_modular as KM                # noqa: E402
import industrial_building as B              # noqa: E402
import overhead_crane as C                   # noqa: E402

BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')
LOD_RE = re.compile(r'_LOD(\d+)(\.\d+)?$')


def load_kit(blend, names):
    """Append a saved kit and rebuild lightweight Pieces (meshes per node and
    LOD level, in the exported frame) that KM.place / KM.strip accept."""
    with bpy.data.libraries.load(blend, link=False) as (src, dst):
        dst.objects = list(src.objects)
    coll = K.collection(os.path.splitext(os.path.basename(blend))[0])
    loaded = [o for o in dst.objects if o is not None]
    for o in loaded:
        coll.objects.link(o)
    bpy.context.view_layer.update()
    pieces, keep = {}, set()
    for name in names:
        root = next(o for o in loaded if o.parent is None and re.sub(r'\.\d+$', '', o.name) == name
                    and o.type == 'EMPTY')
        pc = KM.Piece(name, 'fixed', [])
        pc.meshes = {}
        for o in [root] + list(root.children_recursive):
            keep.add(o)
            m = LOD_RE.search(o.name)
            if o.type != 'MESH' or not m:
                continue
            node = None if o.parent is root else re.sub(r'\.\d+$', '', o.parent.name)
            pc.meshes.setdefault(node, []).append((int(m.group(1)), o))
        pc.meshes = {k: [o for _, o in sorted(v, key=lambda t: t[0])] for k, v in pc.meshes.items()}
        pc.levels = max(len(v) for v in pc.meshes.values())
        pc.root = root
        pieces[name] = pc
    for o in loaded:                         # old previews, props, cameras, lights
        if o not in keep:
            bpy.data.objects.remove(o, do_unlink=True)
    for o in keep:
        o.hide_render = True
    return pieces


BUILDING = ['Wall_Panel', 'Wall_Panel_Dark', 'Wall_Window', 'Wall_Plinth', 'Wall_Top', 'Wall_Corner',
            'Corner_Plinth', 'Corner_Top', 'Door_Sectional', 'Door_Personnel', 'Sign_SimuLean',
            'Column_Base', 'Column_Shaft', 'Column_Top', 'Column_Bracket', 'Column_Guard',
            'Beam_Segment', 'Beam_End']
CRANE = ['Runway_Segment', 'Runway_EndStop', 'Girder_Segment', 'Girder_Logo', 'Girder_End',
         'EndTruck', 'Trolley']

# crane on the building brackets: runways along the long (x) walls
SPAN = B.BLD_D - 2 * (B.COL_OFF + B.BRACKET_E)
RUNWAY = B.BLD_W - 2 * B.COL_OFF + 0.30


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--quick', action='store_true')
    args = ap.parse_args(argv)

    scene = K.reset_scene()
    K.setup_cycles_gpu(scene)
    bld = load_kit(os.path.join(BUILD_DIR, 'IndustrialBuilding.blend'), BUILDING)
    crn = load_kit(os.path.join(BUILD_DIR, 'OverheadCrane.blend'), CRANE)

    KM.studio(scene, k=6.0, floor=160)
    coll = K.collection('Workshop')
    B.building(coll, bld)
    cc = K.collection('WorkshopCrane')
    C.assemble(cc, crn, S=SPAN, RL=RUNWAY, H=B.RAIL, yb=3.0, xt=-2.5, drop=3.4)
    turn = Matrix.Rotation(pi / 2, 4, 'Z')   # crane authored runway axis (y) -> building x
    for o in cc.objects:
        o.matrix_world = turn @ o.matrix_world
    KM.scale_figure(coll, (-2.0, 1.5, 0), 2.6)
    KM.scale_figure(coll, (-7.2, -10.4, 0), 0.3)

    spp = 32 if args.quick else 192
    res = (800, 500) if args.quick else (1600, 1000)
    shots = {
        'aerial': ((26.0, -30.0, 24.0), (0.0, 0.0, 2.5), 30),
        'interior': ((9.5, 6.8, 1.7), (-4.0, -2.0, 5.6), 18),
        'runway': ((-6.6, -5.6, 4.6), (-4.0, -8.2, 5.9), 28),
        'exterior': ((16.0, -27.0, 2.0), (-2.0, -5.0, 4.0), 28),
    }
    for name, c in shots.items():
        KM.render(scene, KM.camera(scene, f'Cam_{name}', *c), os.path.join(PREVIEW_DIR, f'workshop_{name}.png'),
                  res, spp)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, 'Workshop.blend'))


if __name__ == '__main__':
    main()
