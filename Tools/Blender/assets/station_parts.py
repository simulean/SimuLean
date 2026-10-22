"""
Shared parts of the SimuLean station models (paint booth, inspection station, palletizer, ...).

Stack light ("beacon"): a three-segment signal tower -- green (working), amber (waiting / blocked), red
(stopped / failed).  Each lamp is its OWN node (Lamp_Green / Lamp_Amber / Lamp_Red under Beacon) so a view
(MachineStateView) can light one by the element's state with a MaterialPropertyBlock on that renderer; the
lamps are baked with their emission on, so a scene without the view shows them lit.

Materials needed in the caller's MATS: 'BeaconBody' (dark plastic), 'LensG', 'LensA', 'LensR' (simulean_materials
.lens), 'BeaconPost' (any metal / paint) -- beacon_materials() returns them.

Also the build / bake / rig / export pipeline the station scripts share (station_main): every station is a set of
objects, each named after its node, LODs by a plan, nodes as bare Transforms (<node>_LOD0/1/2 under each).
"""

import os
import sys
import time
import argparse

import bpy
import simulean_kit as K
from simulean_kit import Part, finish

KIT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
BUILD_DIR = os.path.join(KIT, 'build')

SP_H = 0.07                      # one segment (size 1)
SP_R = 0.035


def beacon_materials(M):
    """The materials the stack light uses (to merge into the caller's MATS)."""
    return {
        'BeaconBody': M.plastic('BeaconBody', color=(0.02, 0.02, 0.022), rough=0.4),
        'BeaconPost': M.dark_steel('BeaconPost'),
        'LensG': M.lens('LensG', (0.10, 0.85, 0.30)),
        'LensA': M.lens('LensA', (1.00, 0.55, 0.02)),
        'LensR': M.lens('LensR', (1.00, 0.04, 0.02)),
    }


def build_beacon(mats, base, post=0.25, prefix='BC', size=1.0):
    """Stack light standing on `base` (x, y, z) with a `post`-long mast; `size` scales the lamp head (1 = 70 mm
    diameter segments, 1.4 for a booth-sized machine).
    -> (body object, {'Lamp_Green': obj, 'Lamp_Amber': obj, 'Lamp_Red': obj}, pivot of Beacon (base))."""
    x, y, z = base
    LAMP_R, LAMP_H = SP_R * size, SP_H * size

    def body(p):
        p.cylinder((x, y, z), (x, y, z + post), 0.012, 'BeaconPost', n=10)
        p.lathe([(z + post, 0.8 * LAMP_R), (z + post + 0.03, LAMP_R)], 16, 'BeaconBody', center=(x, y, 0), axis='Z')
        top = z + post + 0.03 + 3 * LAMP_H
        p.lathe([(top, LAMP_R), (top + 0.02, LAMP_R), (top + 0.03, 0.6 * LAMP_R), (top + 0.032, 0.0)], 16, 'BeaconBody',
                center=(x, y, 0), axis='Z', cap0=False)
        p.cylinder((x, y, z), (x, y, z + 0.006), 0.04, 'BeaconBody', n=12)
    b = Part(f'{prefix}_Body')
    body(b)
    bo = b.build(mats)
    finish(bo, 0.0, 1, 40, 40)
    lamps = {}
    z0 = z + post + 0.03
    for i, (node, lens) in enumerate((('Lamp_Green', 'LensG'), ('Lamp_Amber', 'LensA'), ('Lamp_Red', 'LensR'))):
        q = Part(f'{prefix}_{node}')
        za = z0 + i * LAMP_H
        q.lathe([(za + 0.004, LAMP_R - 0.002), (za + LAMP_H - 0.004, LAMP_R - 0.002)], 16, lens, center=(x, y, 0),
                axis='Z', cap0=False, cap1=False)
        q.lathe([(za, LAMP_R), (za + 0.004, LAMP_R), (za + 0.004, LAMP_R - 0.002)], 16, 'BeaconBody',
                center=(x, y, 0), axis='Z', cap0=False, cap1=False)
        o = q.build(mats)
        finish(o, 0.0, 1, 40, 40)
        lamps[node] = o
    return bo, lamps, (x, y, z)


def build_status_panel(mats, centre, prefix='SL', r=0.022, gap=0.065):
    """Wall / fascia status indicator: a dark plate with three round lamps in a row (green, amber, red, from -X
    to +X) facing -Y, centred at `centre` (x, y, z) with its back face on y.  Same nodes as the stack light
    (Beacon > Lamp_*), so the same state view drives it; for places where a stack light looks wrong
    (hospital desks, consultation boxes).  -> (plate object, {lamp node: obj}, Beacon pivot)."""
    x, y, z = centre
    w, h, t = 2 * gap + 2 * r + 0.04, 2 * r + 0.04, 0.012
    b = Part(f'{prefix}_Body')
    b.box_minmax((x - w / 2, y - t, z - h / 2), (x + w / 2, y, z + h / 2), 'BeaconBody')
    bo = b.build(mats)
    finish(bo, 0.002, 1, 40, 40)
    lamps = {}
    for i, (node, lens) in enumerate((('Lamp_Green', 'LensG'), ('Lamp_Amber', 'LensA'), ('Lamp_Red', 'LensR'))):
        q = Part(f'{prefix}_{node}')
        cx = x + (i - 1) * gap
        q.cylinder((cx, y - t, z), (cx, y - t - 0.006, z), r, lens, n=16)
        o = q.build(mats)
        finish(o, 0.0, 1, 40, 40)
        lamps[node] = o
    return bo, lamps, (x, y, z)


def control_cabinet(p, x0, x1, y0, y1, top, panel='Panel', frame='Frame', accent='Orange'):
    """Free-standing control cabinet facing -Y (HMI, e-stop, handle) on a plinth."""
    p.box_minmax((x0, y0, 0.0), (x1, y1, 0.10), frame)                         # plinth
    p.box_minmax((x0, y0, 0.10), (x1, y1, top), panel)
    p.box_minmax((x0 + 0.02, y0 - 0.005, 0.14), (x1 - 0.02, y0, top - 0.04), panel)   # door
    hz = min(1.42, top - 0.20)
    p.box_minmax((x0 + 0.12, y0 - 0.012, hz - 0.32), (x1 - 0.12, y0 - 0.004, hz), 'Black')   # HMI
    p.box_minmax((x0 + 0.14, y0 - 0.014, hz - 0.29), (x1 - 0.14, y0 - 0.010, hz - 0.02), 'Screen')
    p.cylinder((x1 - 0.12, y0 - 0.004, hz - 0.47), (x1 - 0.12, y0 - 0.03, hz - 0.47), 0.025, accent, n=14)
    p.box_minmax((x1 - 0.06, y0 - 0.02, hz - 0.72), (x1 - 0.04, y0, hz - 0.52), 'Black')     # handle


def name_beacon(body, lamps):
    """Name the beacon objects after their nodes -> [Beacon, Lamp_Green, Lamp_Amber, Lamp_Red]."""
    body.name = body.data.name = 'Beacon'
    for node, o in lamps.items():
        o.name = o.data.name = node
    return [body] + list(lamps.values())


def beacon_nodes(base, parent):
    """The NODES entries of a beacon standing at `base` under `parent`."""
    return {'Beacon': (base, parent), 'Lamp_Green': (base, 'Beacon'), 'Lamp_Amber': (base, 'Beacon'),
            'Lamp_Red': (base, 'Beacon')}


BEACON_LODS = {'Beacon': (0.5, 0.25), 'Lamp_Green': (0.6, 0.3), 'Lamp_Amber': (0.6, 0.3),
               'Lamp_Red': (0.6, 0.3)}


def build_lods(objs, plan):
    lods = {}
    for ob in objs:
        n = ob.name
        r1, r2 = plan[n]
        l1, l2 = K.make_lod(ob, r1, n + '_LOD1'), K.make_lod(ob, r2, n + '_LOD2')
        ob.name = ob.data.name = n + '_LOD0'
        lods[n] = [ob, l1, l2]
    return lods


def rig(lods, root_name, nodes_plan):
    """nodes_plan: {node: (pivot (authored), parent)}; parents listed before their children."""
    nodes = {root_name: K.empty(root_name, (0, 0, 0), size=0.4, display='ARROWS')}
    for name, (pivot, _) in nodes_plan.items():
        nodes[name] = K.empty(name, pivot, size=0.12 if 'Socket' not in name else 0.2,
                              display='CUBE' if 'Socket' in name else 'PLAIN_AXES')
    bpy.context.view_layer.update()
    for name, meshes in lods.items():
        for mesh in meshes:
            K.bake_into(mesh, nodes[name])
    bpy.context.view_layer.update()
    for name, (_, parent) in nodes_plan.items():
        K.parent_keep_local(nodes[name], nodes[parent])
        bpy.context.view_layer.update()
    return nodes[root_name]


def station_main(name, build, lod_plan, nodes_plan, weights=None):
    """The command line of a station script: build() -> objects named after their nodes; bake one atlas,
    LODs, rig, export Assets/SimuLean.Unity/Art/Models/<name>/<name>.fbx and Tools/Blender/build/<name>.blend."""
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=2048)
    ap.add_argument('--save', type=int, default=1024)
    ap.add_argument('--quick', action='store_true')
    ap.add_argument('--cpu', action='store_true')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 1024
    t0 = time.time()
    out_dir = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', name)
    scene = K.reset_scene()
    K.setup_cycles_gpu(scene)
    if args.cpu:
        from rail_shuttle import use_cpu
        use_cpu(scene)
    os.makedirs(BUILD_DIR, exist_ok=True)
    everything = build()
    K.atlas_unwrap(everything, margin=0.003, weights=weights or {})
    scene.world = bpy.data.worlds.new('BakeWorld')
    try:
        scene.world.light_settings.distance = 0.3
    except AttributeError:
        pass
    paths = K.bake_texture_set(everything, out_dir, name, args.bake, args.save)
    K.assign_single_material(everything, K.make_runtime_material(name, paths))
    lods = build_lods(everything, lod_plan)
    print(f'[{name}] triangles per LOD (renderers):')
    for lvl in range(3):
        obs = [ls[lvl] for ls in lods.values() if len(ls) > lvl]
        print(f'    LOD{lvl}  {sum(K.tri_count(o) for o in obs):>7}  ({len(obs)})')
    root = rig(lods, name, nodes_plan)
    K.export_fbx(root, os.path.join(out_dir, name + '.fbx'))
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, name + '.blend'))
    print(f'[{name}] done in {time.time() - t0:.1f}s')
