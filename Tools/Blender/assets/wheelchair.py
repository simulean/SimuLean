"""
SimuLean asset: manual wheelchair (transit / self-propelled) -- moves a seated patient (pushed by a porter or a
relative).

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/wheelchair.py -- [--bake 2048] [--save 1024] [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/Wheelchair/Wheelchair.fbx   (+ 5 PNG maps)
    Tools/Blender/build/Wheelchair.blend

Real-world reference: standard folding wheelchairs -- seat 0.45 m wide at 0.50 m, overall 0.66 x 1.05 m, 24"
rear wheels with push rims, 200 mm front castors, swing-away footrests, push handles at 0.95 m.  Hospital
palette (hospital_parts), no marks.

Frame as the vehicles: authored front = -Y (Unity +Z = the travel direction), +Z up (Unity +Y), origin on the
floor at the footprint centre; the rear axle is 0.18 m behind it.

Animation hierarchy (Unity names; every name is a bare Transform with <name>_LOD0/1/2 meshes under it):
    Wheelchair              root, on the floor at the footprint centre
    |- Body                 frame, seat, backrest, armrests, footrests, castors (static)
    |- Wheel_L / Wheel_R    rear wheels, pivot on the axle (-+0.31, 0.30, -0.18): spin about local X,
    |                       r = 0.30 m (the vehicle animator needs this radius, not its 0.10 m default)
    |- PatientSocket        on the seat (0, 0.50, 0.02): a seated patient goes here
    '- DriverSocket         where the pushing porter stands (0, 0, -0.80)
"""

import os
import sys
from math import pi, cos, sin

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
import station_parts as SP                  # noqa: E402
import hospital_parts as HP                 # noqa: E402

SEAT_Z = 0.50
SEAT_HW = 0.23                   # seat half width
AXLE_Y, WHEEL_R = 0.18, 0.30
WHEEL_X = 0.31                   # wheel centre plane
FRAME_X = 0.255                  # side frame tubes
TUBE = 0.0125

MATS = {}


def build_materials():
    MATS.update(HP.materials(M))


def mk(name, fn, bevel=0.002):
    return HP.mk(MATS, name, fn, bevel)


def build_body():
    def f(p):
        for sx in (1, -1):
            x = sx * FRAME_X
            # side frame: lower rail, front post (to the footrest), back post (to the push handle)
            HP.tube_path(p, [(x, -0.30, 0.20), (x, AXLE_Y + 0.05, 0.20)], TUBE, 'Chrome')
            HP.tube_path(p, [(x, -0.28, 0.20), (x, -0.28, SEAT_Z - 0.02)], TUBE, 'Chrome')
            HP.tube_path(p, [(x, AXLE_Y + 0.02, 0.20), (x, AXLE_Y + 0.02, SEAT_Z), (x, AXLE_Y + 0.08, 0.90),
                             (x, AXLE_Y + 0.22, 0.92)], TUBE, 'Chrome')
            p.cylinder((x, AXLE_Y + 0.12, 0.915), (x, AXLE_Y + 0.24, 0.925), 0.017, 'Black', n=12)  # handle grip
            HP.tube_path(p, [(x, -0.28, SEAT_Z - 0.02), (x, AXLE_Y + 0.02, SEAT_Z - 0.02)], TUBE, 'Chrome')  # seat rail
            # armrest
            HP.tube_path(p, [(x, -0.18, SEAT_Z - 0.02), (x, -0.18, 0.70), (x, AXLE_Y + 0.05, 0.70)], 0.010,
                         'Chrome')
            p.box_minmax((x - 0.025, -0.16, 0.70), (x + 0.025, AXLE_Y + 0.02, 0.73), 'Black')
            p.box_minmax((sx * (FRAME_X + 0.004) - 0.002, -0.14, SEAT_Z + 0.02), (sx * (FRAME_X + 0.004) + 0.002,
                                                                                  AXLE_Y, 0.69), 'Vinyl')  # side guard
            # footrest hanger + plate
            HP.tube_path(p, [(sx * 0.20, -0.28, SEAT_Z - 0.05), (sx * 0.20, -0.42, 0.14)], 0.011, 'Chrome')
            p.box((sx * 0.20, -0.45, 0.128), (0.16, 0.14, 0.015), 'Grey')
            # axle plate, castor
            p.box_minmax((x - 0.006, AXLE_Y - 0.05, WHEEL_R - 0.04), (x + 0.006, AXLE_Y + 0.06, WHEEL_R + 0.04),
                         'Grey')
            HP.caster(p, sx * FRAME_X, -0.30, 0.10, top=0.20)
            # anti-tip lever at the back
            HP.tube_path(p, [(x, AXLE_Y + 0.05, 0.20), (x, AXLE_Y + 0.25, 0.10)], 0.009, 'Chrome')
            p.cylinder((x - 0.015, AXLE_Y + 0.25, 0.06), (x + 0.015, AXLE_Y + 0.25, 0.06), 0.03, 'Black', n=10)
        # cross brace (folding X) under the seat
        HP.tube_path(p, [(-FRAME_X, -0.05, SEAT_Z - 0.03), (FRAME_X, -0.05, 0.22)], 0.010, 'Chrome')
        HP.tube_path(p, [(FRAME_X, -0.05, SEAT_Z - 0.03), (-FRAME_X, -0.05, 0.22)], 0.010, 'Chrome')
        # seat and back upholstery
        p.extrude(K.rrect(2 * SEAT_HW + 0.04, 0.03, 0.012, 3, 0.0, SEAT_Z + 0.005), 'Y', -0.30, AXLE_Y, 'Vinyl')
        p.box_minmax((-FRAME_X, AXLE_Y + 0.02, SEAT_Z + 0.04), (FRAME_X, AXLE_Y + 0.05, 0.88), 'Vinyl')  # back
    return mk('Body', f)


def build_wheel(name, sx):
    x = sx * WHEEL_X
    c = (0.0, AXLE_Y, WHEEL_R)            # lathe centre: the profile's t is the absolute x

    def f(p):
        w = 0.016
        p.lathe([(x - w, WHEEL_R - 0.022), (x - w, WHEEL_R - 0.006), (x - 0.008, WHEEL_R), (x + 0.008, WHEEL_R),
                 (x + w, WHEEL_R - 0.006), (x + w, WHEEL_R - 0.022)], 40, 'Rubber', center=c, axis='X',
                cap0=False, cap1=False)                                                       # tyre
        p.lathe([(x - 0.012, WHEEL_R - 0.040), (x - 0.012, WHEEL_R - 0.022), (x + 0.012, WHEEL_R - 0.022),
                 (x + 0.012, WHEEL_R - 0.040), (x - 0.012, WHEEL_R - 0.040)], 40, 'Grey', center=c, axis='X',
                cap0=False, cap1=False)                                                       # rim
        p.cylinder((x - 0.035, AXLE_Y, WHEEL_R), (x + 0.035, AXLE_Y, WHEEL_R), 0.03, 'Grey', n=14)  # hub
        n = 18
        for k in range(n):                                                                    # spokes
            a = 2 * pi * k / n
            hx = x + (0.025 if k % 2 else -0.025)
            p.cylinder((hx, AXLE_Y + 0.03 * cos(a), WHEEL_R + 0.03 * sin(a)),
                       (x, AXLE_Y + (WHEEL_R - 0.04) * cos(a + 0.15), WHEEL_R + (WHEEL_R - 0.04) * sin(a + 0.15)),
                       0.0018, 'Chrome', n=4, cap=False)
        xr = x + sx * 0.035                                                                   # push rim
        rr = WHEEL_R - 0.035
        ring = [(xr, AXLE_Y + rr * cos(2 * pi * k / 32), WHEEL_R + rr * sin(2 * pi * k / 32)) for k in range(32)]
        p.sweep(ring, K.circle(0.009, 8), 'Chrome', closed=True, side=(1, 0, 0))
        for k in range(6):
            a = 2 * pi * k / 6
            p.cylinder((x + sx * 0.015, AXLE_Y + rr * cos(a), WHEEL_R + rr * sin(a)),
                       (xr, AXLE_Y + rr * cos(a), WHEEL_R + rr * sin(a)), 0.004, 'Chrome', n=6)
    return mk(name, f, bevel=0.0)


def build_chair():
    build_materials()
    return [build_body(), build_wheel('Wheel_L', -1), build_wheel('Wheel_R', 1)]


LOD_PLAN = {'Body': (0.45, 0.15), 'Wheel_L': (0.4, 0.12), 'Wheel_R': (0.4, 0.12)}
TEXEL_WEIGHTS = {'Body': 1.2, 'Wheel_L': 0.8, 'Wheel_R': 0.8}
NODES = {'Body': ((0, 0, 0), 'Wheelchair'),
         'Wheel_L': ((-WHEEL_X, AXLE_Y, WHEEL_R), 'Wheelchair'),
         'Wheel_R': ((WHEEL_X, AXLE_Y, WHEEL_R), 'Wheelchair'),
         'PatientSocket': ((0, -0.02, SEAT_Z + 0.02), 'Wheelchair'),
         'DriverSocket': ((0, 0.80, 0), 'Wheelchair')}


def build_preview():
    return build_chair()


if __name__ == '__main__':
    SP.station_main('Wheelchair', build_chair, LOD_PLAN, NODES, TEXEL_WEIGHTS)
