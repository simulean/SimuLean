"""
Modular-kit pipeline: many small FBX pieces that share ONE atlas / material.

A kit script (e.g. assets/overhead_crane.py) builds its pieces in an *authored
frame* that matches Unity directly:

    authored +X = Unity +X      authored +Y = Unity +Z      authored +Z = Unity +Y

``rotate_to_export`` turns everything 180 deg about Z just before export so the
standard FBX axis mapping of ``simulean_kit.export_fbx`` lands on that frame.

Piece kinds
    'segment'  a module that tiles along one axis (open at both ends); the Unity
               UnityModularStrip repeats it.  Spans [0, period] on that axis.
    'cap'      closes a run.  Authored with its joint at 0 extending to the
               NEGATIVE side of the axis (the start of a run); the end of a run
               uses the same mesh turned 180 deg, or a dedicated end cap
               authored extending to the positive side.
    'fixed'    an ordinary asset (fixed aspect ratio), optionally rigged with
               named nodes (Unity Transforms).

Seamless tiling
    * procedural noise is made periodic along the tiling axes
      (``simulean_kit.NB.default_period``), so tone, grime and dust match at
      the joints;
    * during the bake every tiling piece is surrounded by *ghost* copies of its
      neighbours, so the baked AO / cavity grime run continuously across joints;
    * tiling pieces are never decimated (a collapse could move the open border
      and crack the joint); their far LOD instead drops small detail parts.

Pieces are laid out side by side along X during the bake (object locations),
so they never occlude each other; the object-space shading is unaffected.
"""

import os

import bpy
from mathutils import Vector, Matrix
from math import pi

import simulean_kit as K

V = Vector
SLOT = 9.0                                   # bake layout pitch between pieces (m)
R180 = Matrix.Rotation(pi, 4, 'Z')


class Piece:
    """name      FBX / Unity name
    kind      'segment' | 'cap' | 'fixed'
    nodes     {node name or None: [part objects]}; None = meshes directly under
              the root (named <Piece>_LODn), otherwise <Node>_LODn under an
              empty <Node> placed at pivots[node]
    lod       {part name: (r1, r2, ...)}  per extra level: decimation ratio,
              1.0 = copy unchanged, None = drop.  Tiling pieces may only use
              1.0 / None.  Parts not listed are kept unchanged at every level.
    levels    number of LOD levels (1 = LOD0 only)
    ghosts    [(piece name, (dx, dy, dz))]: linked copies of that piece placed
              around this one during the bake
    """

    def __init__(self, name, kind, nodes, levels=2, lod=None, ghosts=(), pivots=None,
                 parents=None, sockets=None):
        self.name, self.kind, self.levels = name, kind, levels
        self.nodes = nodes if isinstance(nodes, dict) else {None: list(nodes)}
        self.lod = lod or {}
        self.ghosts = list(ghosts)
        self.pivots = pivots or {}
        self.parents = parents or {}
        self.sockets = sockets or {}
        self.root = None
        self.meshes = {}                     # node -> [LOD meshes]
        self.offset = V((0, 0, 0))

    @property
    def parts(self):
        return [p for ps in self.nodes.values() for p in ps]


# -----------------------------------------------------------------------------
# bake
# -----------------------------------------------------------------------------

def bake_kit(pieces, out_dir, prefix, bake, save, weights=None, ao_distance=0.6):
    """Atlas + bake every part of every piece into one texture set; returns the
    runtime material (assigned to all parts)."""
    parts = [p for pc in pieces for p in pc.parts]
    gap = 0.0025
    K.atlas_unwrap(parts, margin=gap, weights=weights)
    by_name = {pc.name: pc for pc in pieces}
    for i, pc in enumerate(pieces):
        pc.offset = V((i * SLOT, 0, 0))
        for p in pc.parts:
            p.location = p.location + pc.offset
    ghosts = []
    for pc in pieces:
        for src, d in pc.ghosts:
            s = by_name[src]
            for p in s.parts:
                ghosts += K.add_ghosts(p, [pc.offset - s.offset + V(d)])
    scene = bpy.context.scene
    scene.world = scene.world or bpy.data.worlds.new('BakeWorld')
    try:
        scene.world.light_settings.distance = ao_distance
    except AttributeError:
        pass
    # The kit's many small islands sit only `gap` apart, so the default 16 px
    # margin of a later part would paint over its neighbours' islands.
    margin = max(2, int(gap * bake / 2))
    paths = K.bake_texture_set(parts, out_dir, prefix, bake, save, margin=margin)
    K.remove_objects(ghosts)
    for pc in pieces:
        for p in pc.parts:
            p.location = p.location - pc.offset
    mat = K.make_runtime_material(prefix, paths)
    K.assign_single_material(parts, mat)
    return mat


# -----------------------------------------------------------------------------
# LODs, frame turn, rig, export
# -----------------------------------------------------------------------------

def build_lods(pc, sharp=40.0):
    tiling = pc.kind in ('segment', 'cap')
    for node, parts in pc.nodes.items():
        stem = node or pc.name
        levels = []
        for lvl in range(1, pc.levels):
            keep = []
            for p in parts:
                rule = pc.lod.get(p.name, (1.0,) * (pc.levels - 1))[lvl - 1]
                if rule is None:
                    continue
                if tiling and rule != 1.0:
                    raise ValueError(f'{pc.name}: tiling pieces cannot be decimated ({p.name})')
                keep.append(K.make_lod(p, rule, f'{p.name}_L{lvl}', sharp=sharp))
            if keep:
                levels.append(K.join(keep, f'{stem}_LOD{lvl}'))
        lod0 = K.join(parts, f'{stem}_LOD0')
        pc.meshes[node] = [lod0] + levels


def rotate_to_export(pieces):
    for pc in pieces:
        for meshes in pc.meshes.values():
            for m in meshes:
                m.data.transform(R180)
        pc.pivots = {k: tuple(R180 @ V(v)) for k, v in pc.pivots.items()}
        pc.sockets = {k: tuple(R180 @ V(v)) for k, v in pc.sockets.items()}


def rig(pc):
    root = K.empty(pc.name, (0, 0, 0), size=0.5, display='ARROWS')
    nodes = {None: root}
    for node in pc.nodes:
        if node is not None:
            nodes[node] = K.empty(node, pc.pivots.get(node, (0, 0, 0)), size=0.15)
    for name, loc in pc.sockets.items():
        nodes[name] = K.empty(name, loc, size=0.2, display='CUBE')
    bpy.context.view_layer.update()
    for node, meshes in pc.meshes.items():
        for m in meshes:
            K.bake_into(m, nodes[node])
    bpy.context.view_layer.update()
    for name, n in nodes.items():
        if name is None:
            continue
        parent = pc.parents.get(name)
        K.parent_keep_local(n, nodes[parent] if parent else root)
    bpy.context.view_layer.update()
    pc.root = root
    return root


def export_kit(pieces, out_dir):
    for pc in pieces:
        K.export_fbx(pc.root, os.path.join(out_dir, f'{pc.name}.fbx'))


def report(pieces):
    print('[kit] triangles per LOD (renderers):')
    rows = []
    for pc in pieces:
        n = max(len(m) for m in pc.meshes.values())
        per = []
        for lvl in range(n):
            obs = [ms[lvl] for ms in pc.meshes.values() if len(ms) > lvl]
            per.append((sum(K.tri_count(o) for o in obs), len(obs)))
        rows.append((pc.name, per))
        print(f'    {pc.name:<18} ' + '  '.join(f'LOD{i} {t:>6} ({r})' for i, (t, r) in enumerate(per)))
    return rows


# -----------------------------------------------------------------------------
# preview assembly (render-only instances of the exported pieces)
# -----------------------------------------------------------------------------

def place(pc, M_auth, coll, level=0, node_pose=None):
    """Linked copies of `pc`'s LOD meshes placed with authored-frame matrix
    M_auth (previews are rendered in the authored frame).  node_pose: {node:
    authored-frame Matrix} applied about the node's pivot (e.g. wheel spin,
    rope scale, hook drop).  Returns the copies."""
    Rinv = R180.inverted()
    out = []
    level = min(level, pc.levels - 1)        # like a LODGroup: the last level holds
    for node, meshes in pc.meshes.items():
        if len(meshes) <= level:             # node dropped at this level
            continue
        m = meshes[level]
        c = m.copy()
        coll.objects.link(c)
        c.parent = None
        W = Rinv @ m.matrix_world
        if node_pose and node in node_pose:
            piv = W.to_translation()
            W = Matrix.Translation(piv) @ node_pose[node] @ Matrix.Translation(-piv) @ W
        c.matrix_world = M_auth @ W
        c.hide_render = False
        out.append(c)
    return out


def extent(pc, axis='y'):
    """Size of a piece's LOD0 along an authored axis (caps: their length)."""
    i = 'xyz'.index(axis)
    vals = []
    for meshes in pc.meshes.values():
        m = meshes[0]
        W = R180.inverted() @ m.matrix_world
        vals += [(W @ v.co)[i] for v in m.data.vertices]
    return max(vals) - min(vals)


def strip(coll, length, seg, cap=None, end_cap=None, center=None, axis='y', level=0,
          base=Matrix.Identity(4), seg_len=2.0, cap_len=0.0, end_len=None):
    """Mirror of UnityModularStrip for previews: caps + stretched segments along
    `axis` in the authored frame, placed with `base`."""
    i = 'xyz'.index(axis)
    end_len = cap_len if end_len is None else end_len
    run = length - cap_len - (end_len if (cap or end_cap) else 0.0)
    if not cap:
        run = length
    n = max(1, round(run / seg_len))
    k = run / (n * seg_len)
    unit = V((0, 0, 0)); unit[i] = 1.0
    S = Matrix.Identity(4); S[i][i] = k
    out = []
    a0 = cap_len if cap else 0.0
    mid = n // 2
    for j in range(n):
        T = Matrix.Translation(unit * (a0 + j * seg_len * k))
        out += place(center if (center and j == mid) else seg, base @ T @ S, coll, level)
    if cap:
        out += place(cap, base @ Matrix.Translation(unit * cap_len), coll, level)
        if end_cap:
            out += place(end_cap, base @ Matrix.Translation(unit * (length - end_len)), coll, level)
        else:
            up = 'Z' if axis != 'z' else 'X'
            out += place(cap, base @ Matrix.Translation(unit * (length - cap_len)) @
                         Matrix.Rotation(pi, 4, up), coll, level)
    return out


def hide_kit(pieces):
    for pc in pieces:
        for o in [pc.root] + list(pc.root.children_recursive):
            o.hide_render = True


# -----------------------------------------------------------------------------
# preview studio (render-only; scaled for building-size scenes)
# -----------------------------------------------------------------------------

def studio(scene, k=1.0, floor=80.0, lanes=False):
    """Concrete floor, dim world and a three-light area rig scaled by k (k=1
    suits a 2 m asset; lights keep their irradiance as k grows)."""
    props = K.collection('PreviewProps')
    p = K.Part('Floor_Studio')
    p.box((0, 0, -0.05), (floor, floor, 0.1), 'StudioFloor')
    mat = bpy.data.materials.new('StudioFloor')
    nb = K.NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    base = nb.mixc(nb.noise(0.8, 8, 0.6), (0.20, 0.195, 0.185), (0.26, 0.25, 0.235))
    stains = nb.mr(nb.noise(0.35, 6, 0.7), 0.55, 0.72)
    base = nb.mixc(nb.m('MULTIPLY', stains, 0.55), base, (0.07, 0.065, 0.06))
    joints = nb.m('MAXIMUM',
                  nb.mr(nb.m('ABSOLUTE', nb.m('SUBTRACT', nb.m('FRACT', nb.m('DIVIDE', x, 6.0)), 0.5)), 0.4985, 0.4995),
                  nb.mr(nb.m('ABSOLUTE', nb.m('SUBTRACT', nb.m('FRACT', nb.m('DIVIDE', y, 6.0)), 0.5)), 0.4985, 0.4995))
    base = nb.mixc(nb.m('MULTIPLY', joints, 0.7), base, (0.06, 0.055, 0.05))
    if lanes:
        lane = nb.m('LESS_THAN', nb.m('ABSOLUTE', nb.m('SUBTRACT', nb.m('ABSOLUTE', x), lanes)), 0.05)
        base = nb.mixc(nb.m('MULTIPLY', lane, nb.mr(nb.noise(30, 4), 0.3, 0.45)), base, (0.75, 0.50, 0.02))
    rough = nb.mixf(stains, nb.mr(nb.noise(4, 6), 0.3, 0.7, 0.55, 0.8), 0.35)
    K.finalize_material(nb, base, rough, 0.0, nb.bump(nb.noise(60, 8, 0.7), 0.15, 0.002))
    p.build({'StudioFloor': mat}, coll=props)

    world = bpy.data.worlds.new('Studio')
    scene.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes['Background']
    bg.inputs['Color'].default_value = (0.052, 0.046, 0.043, 1)
    bg.inputs['Strength'].default_value = 0.55

    def area(name, loc, target, size, power, color):
        ld = bpy.data.lights.new(name, 'AREA')
        ld.size = size * k
        ld.energy = power * k * k
        ld.color = color
        ob = bpy.data.objects.new(name, ld)
        props.objects.link(ob)
        ob.location = V(loc) * k
        ob.rotation_euler = (V(target) * k - ob.location).to_track_quat('-Z', 'Y').to_euler()
        return ob
    area('Key', (4.5, -4.0, 6.5), (0, 0.3, 0.8), 4.0, 1500, (1.0, 0.95, 0.88))
    area('Fill', (-6.0, -2.5, 3.0), (0, 0.3, 1.0), 6.0, 380, (0.82, 0.88, 1.0))
    area('Rim', (-2.0, 6.5, 4.5), (0, 0.3, 1.2), 3.0, 1800, (1.0, 0.95, 0.9))
    try:
        scene.view_settings.view_transform = 'AgX'
        scene.view_settings.look = 'AgX - Punchy'
    except TypeError:
        pass
    return props


def scale_figure(coll, loc, rot=0.0):
    """1.78 m clay mannequin for scale."""
    mat = bpy.data.materials.get('Clay')
    if mat is None:
        mat = bpy.data.materials.new('Clay')
        nb = K.NB(mat, period={})
        K.finalize_material(nb, (0.16, 0.16, 0.165), 0.55, 0.0, nb.bevel_normal(0.01))
    p = K.Part('ScaleFigure')
    for s in (1, -1):
        p.box((s * 0.085, 0, 0.44), (0.12, 0.14, 0.86), 'Clay')
        p.box((s * 0.25, 0, 1.13), (0.09, 0.11, 0.62), 'Clay')
    p.box((0, 0, 1.18), (0.40, 0.21, 0.64), 'Clay')
    p.box((0, 0, 1.54), (0.10, 0.10, 0.08), 'Clay')
    p.box((0, 0, 1.675), (0.19, 0.21, 0.21), 'Clay')
    ob = p.build({'Clay': mat}, coll=coll)
    K.finish(ob, 0.02, 3, 40)
    ob.location = loc
    ob.rotation_euler = (0, 0, rot)
    return ob


def camera(scene, name, loc, target, lens=35, ortho=None):
    cd = bpy.data.cameras.new(name)
    cd.lens = lens
    cd.sensor_width = 36
    cd.clip_end = 500
    if ortho:
        cd.type = 'ORTHO'
        cd.ortho_scale = ortho
    ob = bpy.data.objects.new(name, cd)
    scene.collection.objects.link(ob)
    ob.location = loc
    ob.rotation_euler = (V(target) - V(loc)).to_track_quat('-Z', 'Y').to_euler()
    return ob


def render(scene, cam, path, res=(1600, 1000), samples=128):
    scene.camera = cam
    scene.render.resolution_x, scene.render.resolution_y = res
    scene.render.resolution_percentage = 100
    scene.cycles.samples = samples
    scene.cycles.use_denoising = True
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGB'
    scene.render.filepath = path
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.render.render(write_still=True)
    print(f'[kit] render -> {path}')
