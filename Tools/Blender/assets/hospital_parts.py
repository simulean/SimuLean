"""
Shared parts of the SimuLean hospital library (bed, stretcher, wheelchair, reception, consultation box, waiting
chairs): the hospital palette and a few common builders.

Palette (generic, no marks): off-white enamel and plastic shrouds, a soft teal accent, light grey, chrome tubes,
blue-grey vinyl upholstery, light birch for desks.
"""

from math import pi, cos, sin

from simulean_kit import Part, finish


def materials(M):
    """The hospital materials (to merge into the caller's MATS)."""
    return {
        'White': M.paint('White', (0.78, 0.79, 0.78), rough=0.35, wear=0.15, dirt=0.4),
        'Shroud': M.plastic('Shroud', color=(0.72, 0.73, 0.72), rough=0.45, grain=0.05),
        'Teal': M.paint('Teal', (0.04, 0.30, 0.28), rough=0.35, wear=0.2, dirt=0.4),
        'Grey': M.paint('Grey', (0.33, 0.34, 0.35), rough=0.40, wear=0.2, dirt=0.5),
        'Chrome': M.dark_steel('Chrome', color=(0.70, 0.71, 0.72), rough=0.15, metal=1.0),
        'Vinyl': M.plastic('Vinyl', color=(0.10, 0.26, 0.36), rough=0.55, grain=0.12),
        'Linen': M.plastic('Linen', color=(0.80, 0.81, 0.80), rough=0.95, grain=0.25),
        'Black': M.plastic('Black'),
        'Rubber': M.rubber('Rubber'),
        'Birch': M.wood('Birch', (0.55, 0.42, 0.28), grain='x', rough=0.5, dirt=0.3),
        'Screen': M.lens('Screen', (0.10, 0.25, 0.40)),
    }


def caster(p, x, y, r=0.06, top=None, mat_fork='Chrome', mat_wheel='Rubber', side=(1, 0, 0)):
    """Swivel caster: wheel of radius r on the floor at (x, y) (axle along X), fork up to `top`."""
    top = top if top is not None else 2.6 * r
    w = 0.35 * r
    p.cylinder((x - w, y, r), (x + w, y, r), r, mat_wheel, n=16)                         # tyre
    p.cylinder((x - w - 0.002, y, r), (x + w + 0.002, y, r), 0.45 * r, 'Grey', n=12)      # hub
    for sx in (1, -1):                                                                    # fork legs
        p.box_minmax((x + sx * (w + 0.004) - 0.003, y - 0.012, r), (x + sx * (w + 0.004) + 0.003, y + 0.012,
                                                                    top - 0.01), mat_fork)
    p.box_minmax((x - w - 0.008, y - 0.015, top - 0.015), (x + w + 0.008, y + 0.025, top), mat_fork)  # crown
    p.cylinder((x, y + 0.012, top), (x, y + 0.012, top + 0.02), 0.012, mat_fork, n=10)    # stem


def tube_path(p, pts, r, mat, n=10):
    """A bent tube through `pts` (straight runs between them)."""
    from simulean_kit import circle
    p.sweep(pts, circle(r, n), mat)


def mk(mats, name, fn, bevel=0.003, seg=1):
    p = Part(name)
    fn(p)
    ob = p.build(mats)
    finish(ob, bevel, seg, 40, 40)
    return ob


def ring(n, r, z, cx=0.0, cy=0.0):
    return [(cx + r * cos(2 * pi * k / n), cy + r * sin(2 * pi * k / n), z) for k in range(n)]


def office_chair(p, x, y, face=-1, seat_z=0.47):
    """Swivel office chair at (x, y): 5-star base, gas column, seat and back; the sitter looks towards `face`
    (-1: -Y, +1: +Y), so the back is on the other side."""
    for k in range(5):                                                                    # star base + castors
        a = 2 * pi * k / 5 + pi / 2
        ex, ey = x + 0.30 * cos(a), y + 0.30 * sin(a)
        p.sweep([(x, y, 0.09), (ex, ey, 0.07)], [(-0.018, -0.012), (0.018, -0.012), (0.018, 0.012),
                                                 (-0.018, 0.012)], 'Black', side=(0, 0, 1))
        p.cylinder((ex - 0.012, ey, 0.03), (ex + 0.012, ey, 0.03), 0.03, 'Black', n=10)
    p.cylinder((x, y, 0.08), (x, y, seat_z - 0.06), 0.025, 'Chrome', n=12)                 # gas column
    p.box_minmax((x - 0.08, y - 0.08, seat_z - 0.08), (x + 0.08, y + 0.08, seat_z - 0.05), 'Black')
    p.box_minmax((x - 0.24, y - 0.23, seat_z - 0.05), (x + 0.24, y + 0.23, seat_z + 0.03), 'Vinyl')  # seat
    yb = y - face * 0.25                                                                  # back behind the seat
    p.box_minmax((x - 0.02, yb - 0.015, seat_z - 0.05), (x + 0.02, yb + 0.015, seat_z + 0.20), 'Black')
    p.box_minmax((x - 0.22, yb - 0.03, seat_z + 0.14), (x + 0.22, yb + 0.03, seat_z + 0.58), 'Vinyl')


def workstation(p, x, y, z, face=-1):
    """Monitor + keyboard on a desk top at height z, centred at (x, y); `face` is the side the operator sits on
    (-1: at -Y, +1: at +Y); the screen looks at them and the keyboard is on their side."""
    ys = y - face * 0.12                                                                  # screen further away
    p.box_minmax((x - 0.10, ys - 0.08, z), (x + 0.10, ys + 0.08, z + 0.012), 'Black')    # foot
    p.box_minmax((x - 0.02, ys - 0.015, z), (x + 0.02, ys + 0.015, z + 0.25), 'Black')   # neck
    p.box_minmax((x - 0.27, ys - 0.02, z + 0.12), (x + 0.27, ys + 0.02, z + 0.45), 'Black')   # monitor
    ysf = ys + face * 0.021
    p.box_minmax((x - 0.26, min(ysf, ysf + face * 0.002), z + 0.13), (x + 0.26, max(ysf, ysf + face * 0.002),
                                                                       z + 0.44), 'Screen')
    yk = y + face * 0.12                                                                  # keyboard + mouse
    p.box_minmax((x - 0.22, yk - 0.07, z), (x + 0.22, yk + 0.07, z + 0.02), 'Grey')
    p.box_minmax((x + 0.28, yk - 0.04, z), (x + 0.34, yk + 0.04, z + 0.03), 'Grey')
