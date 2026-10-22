"""
SimuLean asset: consultation / triage box (emergency-department bay) -- the visual of a service station where a
doctor or nurse sees one patient at a time: an open-front bay closed by a curtain, an examination couch, a desk
with a workstation, a sink.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/consultation_box.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/ConsultationBox/ConsultationBox.fbx   (+ 5 PNG maps)
    Tools/Blender/build/ConsultationBox.blend

Real-world reference: emergency-department cubicles / small consultation rooms, 3.0 x 2.6 m, 2.1 m partitions,
a ceiling curtain track across the open side.  Hospital palette (hospital_parts), no marks; the status
indicator on the front fascia shows free / busy / closed (station_parts.build_status_panel).

Frame: authored front = -Y (Unity +Z: the corridor side, the curtain), +Z up (Unity +Y), origin on the floor at
the centre of the bay.

Animation hierarchy (Unity names; every name is a bare Transform with <name>_LOD0/1/2 meshes under it):
    ConsultationBox         root, on the floor at the bay centre
    |- Body                 partitions, fascia, track, couch, desk, chairs, sink, wall equipment (static)
    |- Curtain              pleated curtain, pivot at its right end (1.44, 0, 1.20): scale local X 1 (closed)
    |                       .. 0.15 (drawn to the right)
    |- Beacon / Lamp_*      status indicator on the fascia
    |- PatientSocket        on the couch, hips (-0.95, 0.72, -0.10): a lying patient (head towards -Z)
    |- StaffSocket          the doctor's chair (0.75, 0, -0.30)
    |- VisitorSocket        the patient / companion chair (0.10, 0, -0.75)
    '- EntrySocket          in front of the bay, on the corridor (0, 0, 1.60)
"""

import os
import sys
from math import pi
from mathutils import Euler

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
import station_parts as SP                  # noqa: E402
import hospital_parts as HP                 # noqa: E402

HX, HY = 1.50, 1.30              # half width / half depth
WALL_H, WALL_T = 2.10, 0.06
FASCIA_Z = (2.10, 2.35)
TRACK_Z = 2.08
COUCH_X, COUCH_Z = -0.95, 0.70
STATUS = (-1.00, -HY - 0.02, 2.225)

MATS = {}


def build_materials():
    MATS.update(HP.materials(M))
    MATS.update(SP.beacon_materials(M))
    MATS['Curtain'] = M.plastic('Curtain', color=(0.30, 0.52, 0.50), rough=0.95, grain=0.3)
    MATS['Paper'] = M.plastic('Paper', color=(0.85, 0.86, 0.84), rough=0.95, grain=0.1)


def mk(name, fn, bevel=0.003):
    return HP.mk(MATS, name, fn, bevel)


def partitions(p):
    # back wall and the two side walls (white panels, teal band, grey skirting)
    p.box_minmax((-HX, HY - WALL_T, 0.0), (HX, HY, WALL_H), 'White')
    for sx in (1, -1):
        xa, xb = sorted((sx * HX, sx * (HX - WALL_T)))
        p.box_minmax((xa, -HY, 0.0), (xb, HY, WALL_H), 'White')
        p.box_minmax((xa - 0.003, -HY, 0.0), (xb + 0.003, HY, 0.10), 'Grey')                  # skirting
        p.box_minmax((xa - 0.003, -HY, 1.00), (xb + 0.003, HY, 1.06), 'Teal')                 # band
    p.box_minmax((-HX, HY - WALL_T - 0.003, 0.0), (HX, HY, 0.10), 'Grey')
    p.box_minmax((-HX, HY - WALL_T - 0.003, 1.00), (HX, HY, 1.06), 'Teal')
    # front fascia over the opening and the curtain track
    p.box_minmax((-HX, -HY - 0.02, FASCIA_Z[0]), (HX, -HY + 0.04, FASCIA_Z[1]), 'White')
    p.box_minmax((-HX, -HY - 0.025, FASCIA_Z[0]), (HX, -HY - 0.02, FASCIA_Z[0] + 0.03), 'Teal')
    p.box_minmax((-HX + WALL_T, -HY + 0.08, TRACK_Z), (HX - WALL_T, -HY + 0.12, TRACK_Z + 0.02), 'Chrome')
    p.box_minmax((0.20, -HY - 0.024, 2.17), (0.70, -HY - 0.02, 2.30), 'Grey')                # (blank) room sign


def couch(p):
    """Height-adjustable examination couch along the left wall, head end at the back (+Y)."""
    x, z = COUCH_X, COUCH_Z
    y0, y1 = -0.70, 1.10
    p.box_minmax((x - 0.25, y0 + 0.30, 0.0), (x + 0.25, y1 - 0.30, 0.08), 'Grey')            # base
    p.box_minmax((x - 0.10, 0.05, 0.08), (x + 0.10, 0.35, z - 0.12), 'Shroud')               # column
    p.box_minmax((x - 0.30, y0, z - 0.12), (x + 0.30, y1, z - 0.06), 'White')                # frame
    p.extrude(K.rrect(0.62, 0.07, 0.03, 3, x, z - 0.025), 'Y', y0, 0.55, 'Vinyl')             # pad
    # raised head section (30 deg)
    import math
    a = math.radians(30)
    L = y1 - 0.55
    p.box((x, 0.55 + L / 2 * math.cos(a), z + 0.01 + L / 2 * math.sin(a)), (0.62, L, 0.07), 'Vinyl',
          rot=Euler((a, 0, 0)))
    # paper sheet roll at the head end, the paper on the pad
    p.cylinder((x - 0.28, y1 + 0.04, z + 0.30), (x + 0.28, y1 + 0.04, z + 0.30), 0.05, 'Paper', n=14)
    p.box_minmax((x - 0.27, y0 + 0.05, z + 0.011), (x + 0.27, 0.55, z + 0.013), 'Paper')
    p.box_minmax((x - 0.30, y0 + 0.30, 0.08), (x + 0.30, y0 + 0.34, 0.10), 'Teal')            # foot pedal bar


def desk(p):
    x0, x1, y0, y1, z = 0.15, HX - WALL_T, 0.55, HY - WALL_T, 0.74
    p.box_minmax((x0, y0, z - 0.03), (x1, y1, z), 'Birch')
    p.box_minmax((x0, y0 + 0.02, 0.0), (x0 + 0.03, y1, z - 0.03), 'White')                   # end panel
    p.box_minmax((x1 - 0.42, y0 + 0.02, 0.02), (x1, y1, z - 0.05), 'White')                  # pedestal
    for zz in (0.22, 0.46):
        p.box_minmax((x1 - 0.32, y0 + 0.015, zz), (x1 - 0.10, y0 + 0.02, zz + 0.02), 'Chrome')
    HP.workstation(p, 0.75, 0.95, z, face=-1)
    HP.office_chair(p, 0.75, 0.30, face=1)
    # patient chair (4 legs, arms) beside the desk, facing the doctor
    cx, cy, sz = 0.10, 0.75, 0.46
    for dx in (-0.22, 0.22):
        for dy in (-0.22, 0.22):
            p.cylinder((cx + dx, cy + dy, 0.0), (cx + dx, cy + dy, sz - 0.04), 0.012, 'Chrome', n=8)
    p.box_minmax((cx - 0.25, cy - 0.25, sz - 0.04), (cx + 0.25, cy + 0.25, sz + 0.03), 'Vinyl')
    p.box_minmax((cx - 0.27, cy - 0.25, sz + 0.03), (cx - 0.23, cy + 0.25, sz + 0.45), 'Vinyl')   # back (-X side)
    for dy in (-0.25, 0.23):
        p.box_minmax((cx - 0.25, cy + dy, sz + 0.18), (cx + 0.20, cy + dy + 0.02, sz + 0.21), 'Chrome')


def sink_and_wall(p):
    # sink unit against the right wall, front part
    x1 = HX - WALL_T
    p.box_minmax((x1 - 0.55, -0.95, 0.0), (x1, -0.15, 0.86), 'White')
    p.box_minmax((x1 - 0.57, -0.97, 0.86), (x1, -0.13, 0.89), 'Grey')
    p.box_minmax((x1 - 0.48, -0.75, 0.80), (x1 - 0.08, -0.35, 0.886), 'Chrome')               # basin
    p.cylinder((x1 - 0.04, -0.55, 0.89), (x1 - 0.04, -0.55, 1.12), 0.012, 'Chrome', n=10)
    p.cylinder((x1 - 0.04, -0.55, 1.12), (x1 - 0.20, -0.55, 1.12), 0.010, 'Chrome', n=10)        # tap
    for y in (-0.80, -0.30):
        p.box_minmax((x1 - 0.10, y - 0.06, 1.20), (x1, y + 0.06, 1.42), 'White')               # dispensers
    # wall equipment over the couch head: vital-signs monitor, oxygen / suction panel
    p.box_minmax((COUCH_X - 0.20, HY - WALL_T - 0.10, 1.45), (COUCH_X + 0.20, HY - WALL_T, 1.75), 'Grey')
    p.box_minmax((COUCH_X - 0.18, HY - WALL_T - 0.102, 1.47), (COUCH_X + 0.18, HY - WALL_T - 0.10, 1.73),
                 'Screen')
    p.box_minmax((COUCH_X - 0.45, HY - WALL_T - 0.05, 1.20), (COUCH_X + 0.45, HY - WALL_T, 1.38), 'White')
    for k, mat in enumerate(('Teal', 'Grey', 'Black')):
        p.cylinder((COUCH_X - 0.30 + k * 0.30, HY - WALL_T - 0.05, 1.29), (COUCH_X - 0.30 + k * 0.30,
                                                                         HY - WALL_T - 0.08, 1.29), 0.025, mat, n=12)
    # waste bins by the sink
    for i, mat in enumerate(('Grey', 'Teal')):
        x = x1 - 0.75 - i * 0.32
        p.lathe([(0.0, 0.13), (0.45, 0.15), (0.48, 0.15)], 18, mat, center=(x, -1.05, 0), axis='Z', cap0=True)


def build_body():
    def f(p):
        partitions(p)
        couch(p)
        desk(p)
        sink_and_wall(p)
    return mk('Body', f)


def build_curtain():
    def f(p):
        n = 36
        x0, x1 = -HX + WALL_T + 0.02, HX - WALL_T - 0.02
        w = (x1 - x0) / n
        y = -HY + 0.10
        for i in range(n):                                                         # pleats
            xc = x0 + (i + 0.5) * w
            ang = 0.45 if i % 2 else -0.45
            p.box((xc, y, 0.25 + (TRACK_Z - 0.03 - 0.25) / 2), (w / 0.90, 0.008, TRACK_Z - 0.03 - 0.25), 'Curtain',
                  rot=Euler((0, 0, ang)))
        for i in range(0, n, 2):                                                   # gliders
            xc = x0 + (i + 0.5) * w
            p.box((xc, y, TRACK_Z - 0.015), (0.02, 0.02, 0.03), 'Grey')
    return mk('Curtain', f, bevel=0.0)


def build_box():
    build_materials()
    body = build_body()
    curtain = build_curtain()
    bo, lamps, _ = SP.build_status_panel(MATS, STATUS)
    return [body, curtain] + SP.name_beacon(bo, lamps)


LOD_PLAN = {'Body': (0.45, 0.15), 'Curtain': (0.5, 0.2), **SP.BEACON_LODS}
TEXEL_WEIGHTS = {'Body': 1.0, 'Curtain': 0.8, 'Beacon': 1.5}
NODES = {'Body': ((0, 0, 0), 'ConsultationBox'),
         'Curtain': ((HX - WALL_T, -HY + 0.10, 0), 'ConsultationBox'),
         **SP.beacon_nodes(STATUS, 'ConsultationBox'),
         'PatientSocket': ((COUCH_X, 0.10, COUCH_Z + 0.02), 'ConsultationBox'),
         'StaffSocket': ((0.75, 0.30, 0), 'ConsultationBox'),
         'VisitorSocket': ((0.10, 0.75, 0), 'ConsultationBox'),
         'EntrySocket': ((0, -1.60, 0), 'ConsultationBox')}


def build_preview():
    """Preview with the curtain drawn open (scale 0.3 about its right end)."""
    objs = build_box()
    import bpy
    from mathutils import Matrix
    for o in objs:
        if o.name == 'Curtain':
            piv = Matrix.Translation((HX - WALL_T, -HY + 0.10, 0))
            o.matrix_world = piv @ Matrix.Diagonal((0.3, 1, 1, 1)) @ piv.inverted() @ o.matrix_world
    bpy.context.view_layer.update()
    return objs


if __name__ == '__main__':
    SP.station_main('ConsultationBox', build_box, LOD_PLAN, NODES, TEXEL_WEIGHTS)
