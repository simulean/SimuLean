"""
SimuLean procedural source-material library.

Each factory builds a Cycles material whose channels (albedo / roughness /
metallic / emission / normal) are registered through
``simulean_kit.finalize_material`` so they can be baked into the shared atlas.

Materials read *object-space* coordinates.  Build static parts at the world
origin (object space == world space) so decal/mask coordinates are in metres in
the asset's frame; parts that are built locally (wheels, steering wheel) get
local coordinates.

Aesthetic intent: "well-kept industrial equipment" - crisp, clean paint with
restrained wear on exposed edges, soft grime in cavities, a light dust film on
upward faces.  Not a wreck.
"""

import bpy
from math import pi
from simulean_kit import NB, finalize_material

GRIME = (0.030, 0.026, 0.022)
DUST = (0.26, 0.245, 0.22)
BARE_STEEL = (0.42, 0.42, 0.41)


def _new(name):
    m = bpy.data.materials.get(name)
    if m:
        bpy.data.materials.remove(m)
    return bpy.data.materials.new(name)


# -----------------------------------------------------------------------------
# Paint (the workhorse): base colour + decals + wear + grime + dust
# -----------------------------------------------------------------------------

def paint(name, color, rough=0.36, wear=1.0, dirt=1.0, decals=(), color_fn=None,
          edge=0.008, flake=0.0):
    """color: linear RGB tuple. color_fn(nb) may return a colour socket instead
    (e.g. hazard stripes). decals: list of dicts (see _apply_decal)."""
    mat = _new(name)
    nb = NB(mat)
    base = color_fn(nb) if color_fn else color
    # subtle large-scale tonal variation (panels are never perfectly uniform)
    tone = nb.mr(nb.noise(1.4, 3, 0.5), 0.3, 0.7, 0.93, 1.05)
    col = nb.multc(base, nb.vec(tone, tone, tone))
    for d in decals:
        col = _apply_decal(nb, col, d)

    edge = nb.edge_mask(edge)
    n_w = nb.noise(22.0, 10, 0.68)
    chips = nb.mr(nb.m('ADD', nb.m('MULTIPLY', edge, 0.95 * wear), nb.m('SUBTRACT', n_w, 0.5)),
                  0.58, 0.63)
    speck = nb.mr(nb.noise(55.0, 4, 0.6), 0.735, 0.75)
    chips = nb.m('MAXIMUM', chips, nb.m('MULTIPLY', speck, 0.8 * wear))
    primer = nb.mr(nb.m('ADD', nb.m('MULTIPLY', edge, 0.95 * wear), nb.m('SUBTRACT', n_w, 0.5)),
                   0.52, 0.58)

    cav = nb.cavity(0.12, 16)
    grime = nb.m('MULTIPLY', cav, nb.mr(nb.noise(3.0, 6, 0.6), 0.3, 0.7, 0.35, 1.0))
    grime = nb.m('MULTIPLY', grime, 0.85 * dirt)
    dust = nb.m('MULTIPLY', nb.up_mask(0.5, 0.95),
                nb.mr(nb.noise(5.0, 8, 0.62), 0.38, 0.78, 0.15, 1.0))
    dust = nb.m('MULTIPLY', dust, 0.42 * dirt)
    streak = nb.mr(nb.noise(1.0, 6, 0.6, aniso=(38, 38, 1.6)), 0.52, 0.8)
    streak = nb.m('MULTIPLY', streak, nb.m('MULTIPLY', 0.22 * dirt, nb.m('SUBTRACT', 1.0, nb.up_mask(0.3, 0.8))))

    col = nb.mixc(streak, col, nb.multc(col, (0.55, 0.52, 0.48)))
    col = nb.mixc(grime, col, GRIME)
    col = nb.mixc(dust, col, DUST)
    col = nb.mixc(nb.m('MULTIPLY', primer, 0.6), col, (0.09, 0.09, 0.085))
    col = nb.mixc(chips, col, BARE_STEEL)

    r = nb.m('ADD', rough, nb.mr(nb.noise(8.0, 4, 0.5), 0.3, 0.7, -0.04, 0.05))
    r = nb.mixf(grime, r, 0.7)
    r = nb.mixf(dust, r, 0.85)
    r = nb.mixf(chips, r, 0.38)
    metal = nb.m('MULTIPLY', chips, 0.95)
    if flake:
        metal = nb.m('MAXIMUM', metal, flake)

    peel = nb.noise(420.0, 2, 0.5)
    nrm = nb.bump(peel, 0.03, 0.001)
    nrm = nb.bump(nb.m('SUBTRACT', 1.0, chips), 0.35, 0.0006, nrm)
    return finalize_material(nb, col, r, metal, nrm)


def _apply_decal(nb, col, d):
    """d = {image, origin(xyz), u(xyz), v(xyz), size(w,h), normal(xyz), color}.
    Projects `image` onto faces facing `normal` near the decal plane."""
    rel = nb.vm('SUBTRACT', nb.obj, d['origin'])
    u = nb.m('ADD', nb.m('DIVIDE', nb.vm('DOT_PRODUCT', rel, d['u']), d['size'][0]), 0.5)
    v = nb.m('ADD', nb.m('DIVIDE', nb.vm('DOT_PRODUCT', rel, d['v']), d['size'][1]), 0.5)
    _, alpha = nb.image(d['image'], nb.vec(u, v, 0.0), 'CLIP', 'Cubic')
    facing = nb.mr(nb.vm('DOT_PRODUCT', nb.geo.outputs['Normal'], d['normal']), 0.55, 0.75)
    depth = nb.vm('DOT_PRODUCT', rel, d['normal'])
    near = nb.mr(nb.m('ABSOLUTE', depth), d.get('depth', 0.06), d.get('depth', 0.06) * 0.8)
    a = nb.m('MULTIPLY', nb.m('MULTIPLY', alpha, facing), near)
    return nb.mixc(nb.m('MULTIPLY', a, d.get('opacity', 0.95)), col, d['color'])


def hazard_fn(yellow, black=(0.018, 0.018, 0.02), width=0.05, axis=('x', 'z')):
    """Returns a color_fn producing 45deg hazard stripes in the given plane."""
    def fn(nb):
        x, y, z = nb.xyz(nb.obj)
        c = {'x': x, 'y': y, 'z': z}
        s = nb.m('DIVIDE', nb.m('ADD', c[axis[0]], c[axis[1]]), width * 2)
        stripe = nb.m('LESS_THAN', nb.m('FRACT', s), 0.5)
        return nb.mixc(stripe, black, yellow)
    return fn


# -----------------------------------------------------------------------------
# Metals
# -----------------------------------------------------------------------------

def forged_steel(name, top_wear=True):
    """Fork blades: dark mill-scale steel, tops and tips polished by pallets."""
    mat = _new(name)
    nb = NB(mat)
    base = nb.mixc(nb.noise(6.0, 6, 0.6), (0.030, 0.030, 0.032), (0.05, 0.048, 0.046))
    edge = nb.edge_mask(0.006)
    scratch = nb.noise(1.0, 8, 0.7, vec=nb.scaled(60, 1.2, 60))
    top = nb.up_mask(0.85, 0.97)
    x, y, z = nb.xyz(nb.obj)
    low = nb.mr(z, 0.08, 0.05)                   # only the blade (near floor level)
    polish = nb.m('MULTIPLY', nb.m('MULTIPLY', top, low), nb.mr(scratch, 0.3, 0.55))
    polish = nb.m('MAXIMUM', polish, nb.mr(nb.m('ADD', edge, nb.m('SUBTRACT', nb.noise(30, 6), 0.5)), 0.5, 0.6))
    if not top_wear:
        polish = nb.m('MULTIPLY', polish, 0.3)
    col = nb.mixc(polish, base, (0.50, 0.50, 0.49))
    cav = nb.cavity(0.1)
    col = nb.mixc(nb.m('MULTIPLY', cav, 0.6), col, GRIME)
    r = nb.mixf(polish, 0.6, nb.mr(scratch, 0.2, 0.8, 0.18, 0.4))
    metal = nb.mixf(polish, 0.35, 1.0)
    nrm = nb.bump(nb.m('MULTIPLY', scratch, polish), 0.2, 0.0005)
    return finalize_material(nb, col, r, metal, nrm)


def chrome(name):
    mat = _new(name)
    nb = NB(mat)
    streak = nb.noise(1.0, 6, 0.6, vec=nb.scaled(2, 2, 90))
    col = nb.mixc(nb.m('MULTIPLY', streak, 0.3), (0.80, 0.80, 0.80), (0.60, 0.60, 0.60))
    r = nb.mr(streak, 0.3, 0.7, 0.05, 0.14)
    return finalize_material(nb, col, r, 1.0)


def dark_steel(name, color=(0.055, 0.055, 0.058), rough=0.45, metal=0.75):
    mat = _new(name)
    nb = NB(mat)
    edge = nb.edge_mask(0.006)
    col = nb.mixc(nb.m('MULTIPLY', edge, 0.6), color, (0.30, 0.30, 0.30))
    cav = nb.cavity(0.1)
    col = nb.mixc(nb.m('MULTIPLY', cav, 0.7), col, GRIME)
    r = nb.m('ADD', rough, nb.mr(nb.noise(12), 0.3, 0.7, -0.05, 0.08))
    return finalize_material(nb, col, r, metal)


def chain(name, pitch=0.019):
    """Leaf chain along world Z: alternating plate / pin pattern in the normal."""
    mat = _new(name)
    nb = NB(mat)
    x, y, z = nb.xyz(nb.obj)
    f = nb.m('FRACT', nb.m('DIVIDE', z, pitch))
    plate = nb.m('ABSOLUTE', nb.m('SUBTRACT', f, 0.5))
    link = nb.mr(plate, 0.42, 0.32)                     # 1 on plates, 0 at pins
    pin = nb.mr(plate, 0.46, 0.5)
    h = nb.m('ADD', nb.m('MULTIPLY', link, 0.6), nb.m('MULTIPLY', pin, 1.0))
    col = nb.mixc(link, (0.05, 0.048, 0.045), (0.16, 0.16, 0.16))
    nrm = nb.bump(h, 0.8, 0.002)
    return finalize_material(nb, col, nb.mixf(link, 0.35, 0.5), 0.8, nrm)


def tread_plate(name):
    """Aluminium diamond (chequer) plate for steps."""
    mat = _new(name)
    nb = NB(mat)
    x, y, z = nb.xyz(nb.obj)
    k = 2 * pi / 0.032
    a = nb.m('SINE', nb.m('MULTIPLY', nb.m('ADD', x, y), k))
    b = nb.m('SINE', nb.m('MULTIPLY', nb.m('SUBTRACT', x, y), k))
    d1 = nb.m('MULTIPLY', nb.mr(a, 0.75, 0.95), nb.mr(nb.m('SINE', nb.m('MULTIPLY', nb.m('SUBTRACT', x, y), k * 0.5)), -0.2, 0.2))
    d2 = nb.m('MULTIPLY', nb.mr(b, 0.75, 0.95), nb.mr(nb.m('SINE', nb.m('MULTIPLY', nb.m('ADD', x, y), k * 0.5)), 0.2, -0.2))
    h = nb.m('MAXIMUM', d1, d2)
    wear = nb.m('MULTIPLY', h, nb.mr(nb.noise(12), 0.4, 0.6))
    col = nb.mixc(wear, (0.35, 0.35, 0.34), (0.70, 0.70, 0.68))
    col = nb.mixc(nb.m('MULTIPLY', nb.cavity(0.05), 0.8), col, GRIME)
    nrm = nb.bump(h, 1.0, 0.0015)
    return finalize_material(nb, col, nb.mixf(wear, 0.5, 0.25), 1.0, nrm)


# -----------------------------------------------------------------------------
# Rubber / plastics
# -----------------------------------------------------------------------------

def tire(name, radius, lugs=28, chevron=9.0):
    """Super-elastic forklift tyre. Built in wheel-local space (axle = X)."""
    mat = _new(name)
    nb = NB(mat)
    x, y, z = nb.xyz(nb.obj)
    theta = nb.m('ARCTAN2', z, y)
    rr = nb.m('SQRT', nb.m('ADD', nb.m('MULTIPLY', y, y), nb.m('MULTIPLY', z, z)))
    tread = nb.mr(rr, radius - 0.007, radius - 0.002)
    ax = nb.m('ABSOLUTE', x)
    u = nb.m('ADD', nb.m('MULTIPLY', theta, lugs / (2 * pi)), nb.m('MULTIPLY', ax, chevron))
    lug = nb.mr(nb.m('ABSOLUTE', nb.m('SUBTRACT', nb.m('FRACT', u), 0.5)), 0.2, 0.26)
    center = nb.mr(ax, 0.008, 0.012)                       # circumferential groove
    h = nb.m('MULTIPLY', nb.m('MULTIPLY', lug, center), tread)
    side = nb.m('SUBTRACT', 1.0, tread)
    ring = nb.mr(nb.m('ABSOLUTE', nb.m('SUBTRACT', rr, radius * 0.80)), 0.004, 0.002)
    h = nb.m('ADD', h, nb.m('MULTIPLY', ring, side))
    dusty = nb.m('MULTIPLY', tread, nb.mr(nb.noise(18, 6), 0.35, 0.7, 0.2, 0.7))
    col = nb.mixc(dusty, (0.016, 0.016, 0.017), (0.075, 0.07, 0.065))
    col = nb.mixc(nb.m('MULTIPLY', h, 0.25), col, (0.03, 0.03, 0.03))
    r = nb.mixf(tread, 0.62, 0.88)
    nrm = nb.bump(h, 1.0, 0.004)
    return finalize_material(nb, col, r, 0.0, nrm)


def rubber(name, color=(0.02, 0.02, 0.021), rough=0.75, ribs=None):
    mat = _new(name)
    nb = NB(mat)
    col = nb.mixc(nb.m('MULTIPLY', nb.up_mask(), nb.mr(nb.noise(6, 6), 0.4, 0.8, 0.0, 0.5)), color, DUST)
    nrm = None
    if ribs:
        x, y, z = nb.xyz(nb.obj)
        c = {'x': x, 'y': y, 'z': z}[ribs[0]]
        h = nb.m('LESS_THAN', nb.m('FRACT', nb.m('DIVIDE', c, ribs[1])), 0.45)
        nrm = nb.bump(h, 0.8, 0.002)
    return finalize_material(nb, col, rough, 0.0, nrm)


def wood(name, color=(0.50, 0.33, 0.17), grain='y', rough=0.78, dirt=1.0):
    """Sawn softwood (pallets, crates): streaky grain along `grain` ('x' | 'y' | 'z', the board's long
    axis in object space), a few dark knots, cavity grime and dust."""
    mat = _new(name)
    nb = NB(mat)
    aniso = {'x': (1.0, 28.0, 28.0), 'y': (28.0, 1.0, 28.0), 'z': (28.0, 28.0, 1.0)}[grain]
    g = nb.noise(4.0, 4, 0.6, aniso=aniso)
    light, dark = tuple(min(1.0, c * 1.18) for c in color), tuple(c * 0.62 for c in color)
    col = nb.mixc(nb.mr(g, 0.32, 0.68), dark, light)
    tone = nb.mr(nb.noise(1.2, 3, 0.5), 0.3, 0.7, 0.88, 1.08)            # board-to-board tone
    col = nb.multc(col, nb.vec(tone, tone, tone))
    knots = nb.mr(nb.noise(9.0, 2, 0.5), 0.74, 0.80)
    col = nb.mixc(nb.m('MULTIPLY', knots, 0.8), col, tuple(c * 0.35 for c in color))
    grime = nb.m('MULTIPLY', nb.cavity(0.06, 16), 0.8 * dirt)
    col = nb.mixc(grime, col, GRIME)
    dust = nb.m('MULTIPLY', nb.up_mask(0.5, 0.95), nb.mr(nb.noise(5.0, 6, 0.6), 0.4, 0.8, 0.1, 0.5 * dirt))
    col = nb.mixc(dust, col, DUST)
    nrm = nb.bump(g, 0.25, 0.0008)
    return finalize_material(nb, col, rough, 0.0, nrm)


def plastic(name, color=(0.022, 0.022, 0.024), rough=0.55, grain=0.08):
    mat = _new(name)
    nb = NB(mat)
    cav = nb.cavity(0.08)
    col = nb.mixc(nb.m('MULTIPLY', cav, 0.5), color, GRIME)
    col = nb.mixc(nb.m('MULTIPLY', nb.up_mask(), nb.mr(nb.noise(6, 6), 0.4, 0.8, 0.0, 0.35)), col, DUST)
    nrm = nb.bump(nb.noise(900, 2), grain, 0.0005)
    return finalize_material(nb, col, rough, 0.0, nrm)


def vinyl(name, color=(0.018, 0.018, 0.02)):
    mat = _new(name)
    nb = NB(mat)
    leather = nb.noise(260, 4, 0.7)
    col = nb.mixc(nb.m('MULTIPLY', nb.edge_mask(0.012, 0.1, 0.5), 0.6), color, (0.05, 0.05, 0.052))
    nrm = nb.bump(leather, 0.25, 0.0008)
    return finalize_material(nb, col, nb.mr(leather, 0.3, 0.7, 0.38, 0.5), 0.0, nrm)


# -----------------------------------------------------------------------------
# Lights / glass / screens
# -----------------------------------------------------------------------------

def lens(name, color, emit=1.0, facets=None):
    """Emissive lamp lens. Emission is baked as colour (<=1); intensity is set on
    the runtime material (Unity: Emission colour HDR)."""
    mat = _new(name)
    nb = NB(mat)
    x, y, z = nb.xyz(nb.obj)
    h = nb.m('MULTIPLY', nb.m('SINE', nb.m('MULTIPLY', x, 2 * pi / 0.006)),
             nb.m('SINE', nb.m('MULTIPLY', z, 2 * pi / 0.006)))
    nrm = nb.bump(h, 0.15, 0.0005)
    base = tuple(c * 0.6 for c in color)
    e = tuple(min(1.0, c * emit) for c in color)
    return finalize_material(nb, base, 0.08, 0.0, nrm, emit=e)


def screen(name, origin, u_axis, v_axis, size):
    """Operator display: dark glass with a simple emissive UI.
    The screen rectangle is given in object space (origin = lower-left corner,
    u/v unit axes, size = (w, h)) so the UI survives joining/atlasing."""
    mat = _new(name)
    nb = NB(mat)
    rel = nb.vm('SUBTRACT', nb.obj, origin)
    gx = nb.m('DIVIDE', nb.vm('DOT_PRODUCT', rel, u_axis), size[0])
    gy = nb.m('DIVIDE', nb.vm('DOT_PRODUCT', rel, v_axis), size[1])
    bars = nb.m('MULTIPLY', nb.m('LESS_THAN', nb.m('FRACT', nb.m('MULTIPLY', gx, 10)), 0.7),
                nb.m('MULTIPLY', nb.mr(gy, 0.15, 0.16), nb.mr(gy, 0.35, 0.34)))
    bars = nb.m('MULTIPLY', bars, nb.m('LESS_THAN', gx, 0.72))
    frame = nb.m('MULTIPLY', nb.mr(gy, 0.55, 0.56), nb.mr(gy, 0.85, 0.84))
    txt = nb.m('MULTIPLY', frame, nb.m('LESS_THAN', nb.m('FRACT', nb.m('MULTIPLY', gx, 26)), 0.5))
    txt = nb.m('MULTIPLY', txt, nb.m('LESS_THAN', gx, 0.5))
    em = nb.mixc(bars, (0.0, 0.0, 0.0), (0.15, 0.85, 0.35))
    em = nb.mixc(nb.m('MULTIPLY', txt, 0.8), em, (0.55, 0.8, 1.0))
    return finalize_material(nb, (0.01, 0.012, 0.014), 0.12, 0.0, None, emit=em)


def mirror(name):
    mat = _new(name)
    nb = NB(mat)
    return finalize_material(nb, (0.85, 0.87, 0.9), 0.03, 1.0, None)


# -----------------------------------------------------------------------------
# Building / structural surfaces
# -----------------------------------------------------------------------------

def galvanised(name, rough=0.42):
    """Hot-dip galvanised steel: light zinc with faint spangle and cavity grime."""
    mat = _new(name)
    nb = NB(mat)
    spangle = nb.noise(90.0, 2, 0.5)
    col = nb.mixc(nb.mr(spangle, 0.35, 0.65), (0.40, 0.41, 0.42), (0.56, 0.57, 0.58))
    col = nb.mixc(nb.mr(nb.noise(4.0, 6, 0.6), 0.45, 0.75, 0.0, 0.35), col, (0.30, 0.30, 0.29))
    col = nb.mixc(nb.m('MULTIPLY', nb.cavity(0.06), 0.7), col, GRIME)
    r = nb.m('ADD', rough, nb.mr(spangle, 0.3, 0.7, -0.08, 0.08))
    return finalize_material(nb, col, r, 1.0)


def concrete(name, color=(0.30, 0.29, 0.27), rough=0.85, pores=1.0, splash=0.0, edge=0.01):
    """Precast / cast concrete: tonal mottling, formwork pores, chipped arrises,
    optional dirt splash rising from the floor (splash = height in m)."""
    mat = _new(name)
    nb = NB(mat)
    tone = nb.noise(2.2, 6, 0.6)
    col = nb.mixc(nb.mr(tone, 0.3, 0.7), tuple(c * 0.86 for c in color), tuple(c * 1.08 for c in color))
    blot = nb.mr(nb.noise(0.9, 4, 0.5), 0.55, 0.75)
    col = nb.mixc(nb.m('MULTIPLY', blot, 0.35), col, tuple(c * 0.7 for c in color))
    pore = nb.mr(nb.noise(160.0, 2, 0.5), 0.70, 0.76)
    pore = nb.m('MULTIPLY', pore, pores)
    col = nb.mixc(nb.m('MULTIPLY', pore, 0.8), col, tuple(c * 0.35 for c in color))
    chip = nb.m('MULTIPLY', nb.edge_mask(edge, 0.15, 0.5), nb.mr(nb.noise(30.0, 6, 0.6), 0.45, 0.6))
    col = nb.mixc(chip, col, tuple(min(1.0, c * 1.25) for c in color))
    col = nb.mixc(nb.m('MULTIPLY', nb.cavity(0.1), 0.6), col, GRIME)
    if splash:
        x, y, z = nb.xyz(nb.obj)
        low = nb.m('MULTIPLY', nb.mr(z, splash, 0.0), nb.mr(nb.noise(6.0, 6, 0.6), 0.25, 0.75, 0.3, 1.0))
        col = nb.mixc(nb.m('MULTIPLY', low, 0.7), col, (0.08, 0.07, 0.06))
    r = nb.m('ADD', rough, nb.mr(tone, 0.3, 0.7, -0.06, 0.05))
    nrm = nb.bump(nb.m('ADD', nb.m('MULTIPLY', nb.noise(40.0, 6, 0.7), 0.4), nb.m('MULTIPLY', pore, -1.0)),
                  0.35, 0.0015)
    return finalize_material(nb, col, r, 0.0, nrm)


def glass(name, tint=(0.018, 0.024, 0.030)):
    """Opaque glazing for real-time use: dark, glossy, faint dust at the frame.
    Reflections come from the engine's probes (Unity: smoothness 0.95)."""
    mat = _new(name)
    nb = NB(mat)
    dust = nb.m('MULTIPLY', nb.cavity(0.08), 0.8)
    col = nb.mixc(dust, tint, (0.10, 0.10, 0.095))
    smear = nb.mr(nb.noise(3.0, 6, 0.6), 0.4, 0.8, 0.03, 0.12)
    r = nb.mixf(dust, smear, 0.6)
    return finalize_material(nb, col, r, 0.0)
