"""
SimuLean asset: hospital reception / admission desk -- the visual of a service station (registration,
admission, information): patients queue at the front, staff sit behind.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/reception_desk.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/ReceptionDesk/ReceptionDesk.fbx   (+ 5 PNG maps)
    Tools/Blender/build/ReceptionDesk.blend

Real-world reference: modular hospital reception counters -- 2.4 m wide, a 1.10 m transaction ledge for standing
patients, a lowered 0.75 m section for wheelchair users at one end, two staff workstations at 0.74 m behind,
a low cabinet and a printer.  Hospital palette (hospital_parts): white fascia with a teal band, birch tops.

Frame: authored front = -Y (Unity +Z: the public side, where patients stand), +Z up (Unity +Y), origin on the
floor at the centre of the counter.

Animation hierarchy (Unity names; every name is a bare Transform with <name>_LOD0/1/2 meshes under it):
    ReceptionDesk           root, on the floor at the counter centre
    |- Body                 counter, worktops, workstations, chairs, cabinet (static)
    |- Beacon / Lamp_*      status indicator on the fascia (station_parts.build_status_panel)
    |- ServiceSocket_1 / _2 where a patient is served, in front of each workstation (-+0.55, 0, 0.75)
    '- StaffSocket_1 / _2   the staff chairs (-+0.55, 0, -0.55)
"""

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
import station_parts as SP                  # noqa: E402
import hospital_parts as HP                 # noqa: E402

HX = 1.20                        # half width
Y_FRONT, Y_BACK = -0.35, 0.40    # fascia face, back of the staff worktop
LEDGE_Z, WORK_Z = 1.10, 0.74
LOW_X = (0.40, 1.20)             # lowered (accessible) section
SEATS = (-0.55, 0.55)
STATUS = (-0.95, Y_FRONT, 0.95)

MATS = {}


def build_materials():
    MATS.update(HP.materials(M))
    MATS.update(SP.beacon_materials(M))


def mk(name, fn, bevel=0.003):
    return HP.mk(MATS, name, fn, bevel)


def build_body():
    def f(p):
        x0, x1 = -HX, HX
        lx0 = LOW_X[0]
        # fascia (high part, then the lowered part)
        p.box_minmax((x0, Y_FRONT, 0.0), (lx0, Y_FRONT + 0.03, LEDGE_Z - 0.03), 'White')
        p.box_minmax((lx0, Y_FRONT, 0.0), (x1, Y_FRONT + 0.03, WORK_Z - 0.03), 'White')
        p.box_minmax((x0, Y_FRONT - 0.005, 0.70), (lx0, Y_FRONT, 0.78), 'Teal')                # band
        p.box_minmax((lx0, Y_FRONT - 0.005, 0.40), (x1, Y_FRONT, 0.48), 'Teal')
        p.box_minmax((x0, Y_FRONT - 0.004, 0.0), (x1, Y_FRONT, 0.08), 'Grey')                  # kick plate
        for x in (x0, x1 - 0.03):                                                              # end panels
            top = LEDGE_Z if x < lx0 else WORK_Z
            p.box_minmax((x, Y_FRONT, 0.0), (x + 0.03, Y_BACK, top - 0.03 if x < lx0 else top), 'White')
        p.box_minmax((lx0 - 0.03, Y_FRONT, WORK_Z), (lx0, Y_FRONT + 0.32, LEDGE_Z - 0.03), 'White')   # step
        # transaction ledge (high) and the lowered top
        p.box_minmax((x0 - 0.02, Y_FRONT - 0.06, LEDGE_Z - 0.03), (lx0, Y_FRONT + 0.32, LEDGE_Z), 'Birch')
        p.box_minmax((lx0, Y_FRONT - 0.06, WORK_Z - 0.03), (x1 + 0.02, Y_BACK, WORK_Z), 'Birch')
        # staff worktop behind the high part, on legs
        p.box_minmax((x0 + 0.03, Y_FRONT + 0.32, WORK_Z - 0.03), (lx0, Y_BACK, WORK_Z), 'Birch')
        p.box_minmax((x0 + 0.03, Y_FRONT + 0.03, WORK_Z - 0.03), (lx0, Y_FRONT + 0.32, WORK_Z), 'Birch')
        for x in (-0.05, lx0 - 0.04):
            p.box_minmax((x - 0.02, Y_BACK - 0.05, 0.0), (x + 0.02, Y_BACK - 0.01, WORK_Z - 0.03), 'Grey')
        # two workstations facing the staff (screens towards +Y, staff on the +Y side)
        for i, x in enumerate(SEATS):
            HP.workstation(p, x, Y_FRONT + 0.24, WORK_Z, face=1)
            HP.office_chair(p, x, Y_BACK + 0.15, face=-1)
        # pedestal drawers under the left worktop, printer, document trays
        p.box_minmax((x0 + 0.05, Y_FRONT + 0.05, 0.02), (x0 + 0.45, Y_FRONT + 0.55, WORK_Z - 0.05), 'White')
        for z in (0.20, 0.42, 0.60):
            p.box_minmax((x0 + 0.15, Y_FRONT + 0.545, z), (x0 + 0.35, Y_FRONT + 0.555, z + 0.02), 'Chrome')
        p.box_minmax((-0.05 - 0.20, Y_BACK - 0.30, WORK_Z), (-0.05 + 0.20, Y_BACK - 0.02, WORK_Z + 0.22), 'Grey')
        p.box_minmax((-0.05 - 0.16, Y_BACK - 0.32, WORK_Z + 0.08), (-0.05 + 0.16, Y_BACK - 0.30, WORK_Z + 0.10),
                     'Black')
        p.box_minmax((0.95, Y_FRONT + 0.10, WORK_Z), (1.15, Y_FRONT + 0.36, WORK_Z + 0.08), 'Grey')   # tray
        # a hand-sanitiser dispenser and a card reader on the ledge
        p.box_minmax((-1.12, Y_FRONT + 0.02, LEDGE_Z), (-1.04, Y_FRONT + 0.10, LEDGE_Z + 0.20), 'White')
        p.box_minmax((-0.40, Y_FRONT + 0.0, LEDGE_Z), (-0.30, Y_FRONT + 0.14, LEDGE_Z + 0.04), 'Black')
    return mk('Body', f)


def build_desk():
    build_materials()
    body = build_body()
    bo, lamps, _ = SP.build_status_panel(MATS, STATUS)
    return [body] + SP.name_beacon(bo, lamps)


LOD_PLAN = {'Body': (0.45, 0.15), **SP.BEACON_LODS}
TEXEL_WEIGHTS = {'Body': 1.0, 'Beacon': 1.5}
NODES = {'Body': ((0, 0, 0), 'ReceptionDesk'),
         **SP.beacon_nodes(STATUS, 'ReceptionDesk'),
         'ServiceSocket_1': ((SEATS[0], -0.75, 0), 'ReceptionDesk'),
         'ServiceSocket_2': ((SEATS[1], -0.75, 0), 'ReceptionDesk'),
         'StaffSocket_1': ((SEATS[0], Y_BACK + 0.15, 0), 'ReceptionDesk'),
         'StaffSocket_2': ((SEATS[1], Y_BACK + 0.15, 0), 'ReceptionDesk')}


def build_preview():
    return build_desk()


if __name__ == '__main__':
    SP.station_main('ReceptionDesk', build_desk, LOD_PLAN, NODES, TEXEL_WEIGHTS)
