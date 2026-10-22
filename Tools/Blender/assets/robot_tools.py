"""
SimuLean kit: end-of-arm tools for the IndustrialRobot (swappable: RobotArmRig.tool).

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/robot_tools.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs (a kit: one FBX per tool, one shared texture set / material RobotTools.mat)
    Assets/SimuLean.Unity/Art/Models/RobotTools/Gripper.fbx         parallel two-finger gripper (boxes)
    Assets/SimuLean.Unity/Art/Models/RobotTools/VacuumGripper.fbx   vacuum frame with 8 cups (panels, boards)
    Assets/SimuLean.Unity/Art/Models/RobotTools/WeldingTorch.fbx    MIG/MAG welding torch (straight neck)
    Assets/SimuLean.Unity/Art/Models/RobotTools/RobotTools_*.png
    Tools/Blender/build/RobotTools.blend

Frame (kit rules, simulean_modular): authored +X = Unity +X, authored +Y = Unity +Z, authored +Z = Unity +Y.
A tool's root is its MOUNT face, centred on the robot flange: the tool extends along Unity +Z (the
flange axis) -- it is parented to the robot's ToolSocket with an identity local pose.  Generic design,
library palette (aluminium, black, a yellow accent), no third-party marks.

Nodes (Unity names, positions in the tool's frame, metres):
    Gripper                 mount face
    |- Finger_L / Finger_R  the jaws: localPosition.x = +-0.30 fully open .. +-0.035 closed (move along X);
    |                       the jaws hang 0.25 m below the palm, rubber pads on their inner faces
    '- TCP                  the palm face (0, 0, 0.18): a gripped box's top rests here
    VacuumGripper           mount face
    '- TCP                  the cup lips (0, 0, 0.315): a held panel's top rests here
                            (frame 1.40 x 0.80 m, cups at x = +-0.2 / +-0.6, y = +-0.35)
    WeldingTorch            mount face (collision sensor, straight torch body, gas nozzle, contact tip)
    '- TCP                  the wire tip (0, 0, 0.41), 15 mm wire stick-out: on the flange axis, so the
                            IK puts the arc point exactly on the target
RobotArmRig reads the tool length from TCP; RobotArmView opens / closes Finger_L / _R to the item's width.
"""

import os
import sys
import argparse
import time
from math import pi, cos, sin

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import bpy                                  # noqa: E402
import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
import simulean_modular as KM               # noqa: E402
from simulean_kit import Part, finish       # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'RobotTools')
BUILD_DIR = os.path.join(KIT, 'build')
PREFIX = 'RobotTools'

# gripper
OPEN_X = 0.30                    # jaw position fully open (half the opening)
PALM_Y = 0.18                    # palm face (TCP) along the tool axis
JAW_LEN = 0.25
# vacuum frame
CUP_X, CUP_Z, LIP_Y = (-0.6, -0.2, 0.2, 0.6), (-0.35, 0.35), 0.315

MATS = {}


def build_materials():
    MATS.update({
        'Alu': M.dark_steel('Alu', color=(0.55, 0.56, 0.58), rough=0.32, metal=0.9),
        'Dark': M.dark_steel('Dark'),
        'Black': M.plastic('Black', color=(0.02, 0.02, 0.022), rough=0.45),
        'Accent': M.paint('Accent', (0.80, 0.42, 0.015), rough=0.35, wear=0.4),
        'Pad': M.rubber('Pad', color=(0.03, 0.03, 0.032), rough=0.8),
        'Cup': M.rubber('Cup', color=(0.05, 0.05, 0.055), rough=0.6),
        'Hose': M.rubber('Hose', color=(0.02, 0.06, 0.12), rough=0.5),
        'Bolt': M.dark_steel('Bolt', color=(0.12, 0.12, 0.125)),
        'Copper': M.dark_steel('Copper', color=(0.62, 0.34, 0.18), rough=0.3, metal=0.95),
        'Wire': M.dark_steel('Wire', color=(0.55, 0.45, 0.30), rough=0.25, metal=0.95),
    })


def mk(name, fn, bevel=0.003, seg=1):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, seg, 40, 40)
    return ob


def adapter(p):
    """ISO-style adapter plate on the mount face (tool axis = +Y), with a bolt circle."""
    p.lathe([(0.0, 0.074), (0.024, 0.074)], 32, 'Dark', axis='Y')
    for k in range(6):
        a = 2 * pi * k / 6 + pi / 6
        x, z = 0.058 * cos(a), 0.058 * sin(a)
        p.cylinder((x, 0.0, z), (x, -0.004, z), 0.006, 'Bolt', n=8)


# =============================================================================
# Gripper
# =============================================================================

def build_gripper():
    def body(p):
        adapter(p)
        p.box_minmax((-0.10, 0.024, -0.07), (0.10, 0.11, 0.07), 'Alu')                     # housing
        p.box_minmax((-0.101, 0.05, -0.071), (0.101, 0.07, 0.071), 'Accent')               # accent band
        p.box_minmax((-0.37, 0.11, -0.05), (0.37, 0.155, 0.05), 'Alu')                     # jaw rail beam
        for z in (-0.03, 0.03):                                                               # guide rails
            p.box_minmax((-0.36, 0.155, z - 0.008), (0.36, 0.165, z + 0.008), 'Dark')
        p.cylinder((-0.33, 0.085, -0.085), (0.33, 0.085, -0.085), 0.026, 'Dark', n=20)      # air cylinder
        for x in (-0.33, 0.33):
            p.cylinder((x, 0.085, -0.085), (x + (0.02 if x > 0 else -0.02), 0.085, -0.085), 0.03, 'Black', n=20)
        p.box_minmax((-0.27, PALM_Y - 0.012, -0.075), (0.27, PALM_Y, 0.075), 'Pad')        # palm (box top rests here)
        p.box_minmax((-0.27, 0.165, -0.07), (0.27, PALM_Y - 0.012, 0.07), 'Alu')          # palm plate
        p.cylinder((0.10, 0.06, 0.07), (0.10, 0.06, 0.11), 0.012, 'Black', n=12)           # valve / cable
        p.sweep([(0.10, 0.06, 0.11), (0.10, 0.03, 0.13), (0.06, 0.0, 0.14)], K.circle(0.008, 8), 'Hose')
    def jaw(side):
        def f(p):
            x = side * OPEN_X
            p.box_minmax((x - 0.035, 0.155, -0.065), (x + 0.035, 0.20, 0.065), 'Dark')     # carriage
            xo, xi = x + side * 0.008, x - side * 0.008
            p.box_minmax((min(xo, xi), 0.17, -0.08), (max(xo, xi), PALM_Y + JAW_LEN, 0.08), 'Alu')
            xp = x - side * 0.008
            p.box_minmax((min(xp, xp - side * 0.010), PALM_Y + 0.03, -0.07),
                         (max(xp, xp - side * 0.010), PALM_Y + JAW_LEN - 0.01, 0.07), 'Pad')
            p.box_minmax((min(xo, xo + side * 0.004), 0.20, -0.05),
                         (max(xo, xo + side * 0.004), 0.26, 0.05), 'Accent')                # jaw tag
        return f
    parts = [mk('GR_Body', body)]
    fl, fr = mk('GR_Finger_L', jaw(1)), mk('GR_Finger_R', jaw(-1))
    return KM.Piece('Gripper', 'fixed', {None: parts, 'Finger_L': [fl], 'Finger_R': [fr]}, levels=2,
                    lod={'GR_Body': (0.45,), 'GR_Finger_L': (0.5,), 'GR_Finger_R': (0.5,)},
                    pivots={'Finger_L': (OPEN_X, 0.17, 0.0), 'Finger_R': (-OPEN_X, 0.17, 0.0)},
                    sockets={'TCP': (0.0, PALM_Y, 0.0)})


# =============================================================================
# Vacuum frame
# =============================================================================

def build_vacuum():
    def frame(p):
        adapter(p)
        p.box_minmax((-0.08, 0.024, -0.08), (0.08, 0.125, 0.08), 'Alu')                    # hub
        p.box_minmax((-0.081, 0.06, -0.081), (0.081, 0.08, 0.081), 'Accent')
        p.box_minmax((0.08, 0.04, -0.04), (0.16, 0.11, 0.04), 'Black')                     # ejector
        p.box_minmax((-0.72, 0.125, -0.035), (0.72, 0.185, 0.035), 'Alu')                  # main beam
        for x in CUP_X:                                                                       # cross beams
            p.box_minmax((x - 0.025, 0.185, -0.42), (x + 0.025, 0.235, 0.42), 'Alu')
            for z in CUP_Z:
                p.cylinder((x, 0.235, z), (x, 0.27, z), 0.012, 'Dark', n=12)                 # cup stem
                p.lathe([(0.27, 0.016), (0.282, 0.034), (0.292, 0.026), (0.302, 0.038), (LIP_Y - 0.004, 0.044),
                         (LIP_Y, 0.046)], 20, 'Cup', center=(x, 0, z), axis='Y', cap1=False)
        for x in (-0.6, 0.6):                                                                 # vacuum hoses
            p.sweep([(0.12, 0.11, 0.0), (x * 0.5, 0.16, 0.06), (x, 0.205, 0.06)], K.circle(0.009, 8), 'Hose')
        for x in (-0.72, 0.72):
            p.box_minmax((x - 0.004 if x > 0 else x, 0.125, -0.036), (x if x > 0 else x + 0.004, 0.185, 0.036), 'Black')
    parts = [mk('VG_Frame', frame)]
    return KM.Piece('VacuumGripper', 'fixed', {None: parts}, levels=2, lod={'VG_Frame': (0.4,)},
                    sockets={'TCP': (0.0, LIP_Y, 0.0)})


# =============================================================================
# Welding torch
# =============================================================================

TIP_Y = 0.41                     # wire tip (TCP) along the tool axis


def build_torch():
    def f(p):
        adapter(p)
        p.lathe([(0.024, 0.050), (0.075, 0.050), (0.082, 0.040)], 24, 'Black', axis='Y', cap0=False)  # collision sensor
        p.box_minmax((-0.03, 0.03, 0.05), (0.03, 0.07, 0.085), 'Accent')                  # sensor housing
        p.lathe([(0.082, 0.030), (0.10, 0.026), (0.30, 0.022)], 20, 'Dark', axis='Y', cap0=False)       # torch body
        for y in (0.13, 0.17, 0.21):
            p.lathe([(y, 0.0235), (y + 0.012, 0.0235)], 20, 'Black', axis='Y', cap0=False, cap1=False)  # grip rings
        p.lathe([(0.30, 0.022), (0.31, 0.019), (0.38, 0.012), (0.385, 0.0095)], 20, 'Copper', axis='Y',
                cap0=False)                                                                    # gas nozzle
        p.lathe([(0.385, 0.0045), (0.396, 0.0035)], 12, 'Copper', axis='Y', cap0=False)       # contact tip
        p.cylinder((0, 0.396, 0), (0, TIP_Y, 0), 0.0008, 'Wire', n=6)                        # wire stick-out
        # cable package: from the sensor back along the arm
        p.sweep([(0.0, 0.06, 0.055), (0.0, 0.03, 0.12), (0.0, -0.06, 0.16), (0.0, -0.20, 0.17)],
                K.circle(0.016, 10), 'Black', side=(1, 0, 0))
    body = mk('WT_Body', f, bevel=0.002)
    return KM.Piece('WeldingTorch', 'fixed', {None: [body]}, levels=2, lod={'WT_Body': (0.4,)},
                    sockets={'TCP': (0.0, TIP_Y, 0.0)})


def build_kit():
    build_materials()
    return [build_gripper(), build_vacuum(), build_torch()]


def build_preview():
    """Live preview: the tools as built (no bake), side by side."""
    pieces = build_kit()
    for i, pc in enumerate(pieces):
        for o in pc.parts:
            o.location.x += i * 1.8
    return [o for pc in pieces for o in pc.parts]


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=2048)
    ap.add_argument('--save', type=int, default=1024)
    ap.add_argument('--quick', action='store_true')
    ap.add_argument('--cpu', action='store_true')
    ap.add_argument('--no-render', action='store_true', help='(accepted for symmetry: no previews)')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 1024
    t0 = time.time()
    scene = K.reset_scene()
    K.setup_cycles_gpu(scene)
    if args.cpu:
        from rail_shuttle import use_cpu
        use_cpu(scene)
    os.makedirs(BUILD_DIR, exist_ok=True)
    pieces = build_kit()
    KM.bake_kit(pieces, OUT_DIR, PREFIX, args.bake, args.save, ao_distance=0.15)
    for pc in pieces:
        KM.build_lods(pc)
    KM.report(pieces)
    KM.rotate_to_export(pieces)
    for pc in pieces:                         # one at a time: every tool has a node named TCP, and Blender
        root = KM.rig(pc)                     # names are unique, so each piece's TCP is renamed once exported
        K.export_fbx(root, os.path.join(OUT_DIR, f'{pc.name}.fbx'))
        for o in root.children_recursive:
            if o.name == 'TCP':
                o.name = f'TCP_{pc.name}'
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, f'{PREFIX}.blend'))
    print(f'[tools] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
