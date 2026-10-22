"""
SimuLean Blender asset kit
==========================

Reusable helpers for building simulation-grade 3D assets procedurally in Blender
(run headless: ``blender -b -P <asset_script>.py``).

Pipeline every asset follows
----------------------------
1. **Model** hard-surface parts from primitives: profiles, lofts, lathes, sweeps,
   boxes, booleans.  Parts are built in *world* coordinates with their origin at
   the world origin (so procedural shaders can use object space = world space).
2. **Finish** each part: small angle-limited bevel -> triangulate -> smooth shading
   + sharp-by-angle -> weighted normals.  Real bevels catch light; weighted normals
   keep large flat faces flat.
3. **Shade** with rich procedural "source" materials (edge wear, cavity grime,
   dust, decals, tread, emissive lenses).  These never leave Blender.
4. **Atlas + bake**: every mesh of the asset is unwrapped into ONE shared UV atlas
   and the source materials are baked to a single PBR texture set
   (Albedo, MetallicSmoothness, Normal, Occlusion, Emission).  In Unity the whole
   asset uses **one material** -> minimal draw-call / state cost.
5. **Rig**: move origins to pivots and parent into an animation hierarchy.
6. **Export** FBX for Unity (Y-up, +Z forward, scale 1, identity rotations).

Axis convention (Blender -> Unity, with the export settings in ``export_fbx``)
    Blender -Y (front)  -> Unity +Z (forward)
    Blender +Z (up)     -> Unity +Y (up)
    Blender +X          -> Unity -X      (so the asset's left/right are preserved)
    Model assets facing Blender -Y.
"""

import bpy
import bmesh
import math
import os
import struct
import zlib
from math import radians, pi, sin, cos, atan2, sqrt

import numpy as np
from mathutils import Vector, Matrix

V = Vector


# =============================================================================
# Scene / render setup
# =============================================================================

def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = 'METRIC'
    scene.unit_settings.scale_length = 1.0
    return scene


def _device_bakes(scene):
    """True if the current Cycles device really bakes: a 16 px emission bake of a
    temporary plane must come out non-black. A backend can be listed and still fail
    (OptiX kernels that do not compile on a driver give an all-black bake and no
    error, which shipped a black Forklift atlas on 2026-10-07)."""
    me = bpy.data.meshes.new('_probe')
    me.from_pydata([(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)], [], [(0, 1, 2, 3)])
    me.uv_layers.new(name='UVMap')
    ob = bpy.data.objects.new('_probe', me)
    scene.collection.objects.link(ob)
    mat = bpy.data.materials.new('_probe'); mat.use_nodes = True
    nt = mat.node_tree; nt.nodes.clear()
    e = nt.nodes.new('ShaderNodeEmission'); e.inputs['Color'].default_value = (1, 1, 1, 1)
    nt.links.new(e.outputs[0], nt.nodes.new('ShaderNodeOutputMaterial').inputs['Surface'])
    me.materials.append(mat)
    img = bpy.data.images.new('_probe', 16, 16)
    tex = nt.nodes.new('ShaderNodeTexImage'); tex.image = img; nt.nodes.active = tex
    samples = scene.cycles.samples
    ok = False
    try:
        bpy.context.view_layer.update()
        for o in scene.objects:
            o.select_set(o == ob)
        bpy.context.view_layer.objects.active = ob
        scene.cycles.samples = 1
        bpy.ops.object.bake(type='EMIT', margin=0)
        px = np.empty(16 * 16 * 4, np.float32)
        img.pixels.foreach_get(px)
        ok = float(px.reshape(-1, 4)[:, :3].max()) > 0.5
    except RuntimeError as ex:
        print(f"[kit] probe bake failed: {ex}")
    finally:
        scene.cycles.samples = samples
        bpy.data.objects.remove(ob); bpy.data.meshes.remove(me)
        bpy.data.materials.remove(mat); bpy.data.images.remove(img)
        bpy.context.view_layer.update()
    return ok


def setup_cycles_gpu(scene, samples=64, backends=('OPTIX', 'CUDA', 'HIP', 'ONEAPI', 'METAL')):
    """Cycles on the first GPU backend in `backends` that actually bakes (probed
    with `_device_bakes`), else the CPU.  Pass ('CUDA', ...) to skip OptiX outright:
    in a live Blender 5.2.2 session OptiX bakes come out black (CUDA is fine)."""
    scene.render.engine = 'CYCLES'
    prefs = bpy.context.preferences.addons['cycles'].preferences
    chosen = None
    for backend in backends:
        try:
            prefs.compute_device_type = backend
        except TypeError:
            continue
        prefs.get_devices()
        if not any(d.type == backend for d in prefs.devices):
            continue
        for d in prefs.devices:
            d.use = d.type == backend
        scene.cycles.device = 'GPU'
        if _device_bakes(scene):
            chosen = backend
            break
        print(f"[kit] {backend} listed but bakes black - skipped")
    if chosen is None:
        try:
            prefs.compute_device_type = 'NONE'
        except TypeError:
            pass
    scene.cycles.device = 'GPU' if chosen else 'CPU'
    scene.cycles.samples = samples
    print(f"[kit] Cycles device: {scene.cycles.device} ({chosen})")
    return chosen


def collection(name, parent=None):
    c = bpy.data.collections.get(name) or bpy.data.collections.new(name)
    if c.name not in (parent or bpy.context.scene.collection).children:
        (parent or bpy.context.scene.collection).children.link(c)
    return c


# =============================================================================
# 2D shape helpers
# =============================================================================

def rrect(w, h, r, seg=4, cx=0.0, cy=0.0, radii=None):
    """Rounded rectangle, CCW. radii order: (+x+y, -x+y, -x-y, +x-y).
    Corner radius is clamped to >= 1 mm so every corner has seg+1 points
    (consistent vertex count -> loftable between different sizes)."""
    radii = radii or (r, r, r, r)
    pts = []
    corners = [(w / 2, h / 2, 0), (-w / 2, h / 2, 90), (-w / 2, -h / 2, 180), (w / 2, -h / 2, 270)]
    for (x, y, a0), rr in zip(corners, radii):
        rr = max(min(rr, w / 2 - 1e-4, h / 2 - 1e-4), 0.001)
        ccx = x - math.copysign(rr, x)
        ccy = y - math.copysign(rr, y)
        for k in range(seg + 1):
            a = radians(a0 + 90.0 * k / seg)
            pts.append((cx + ccx + rr * cos(a), cy + ccy + rr * sin(a)))
    return pts


def circle(r, n=16, cx=0.0, cy=0.0, a0=0.0):
    return [(cx + r * cos(a0 + 2 * pi * i / n), cy + r * sin(a0 + 2 * pi * i / n)) for i in range(n)]


def fillet(points, radius, seg=4):
    """Round the interior corners of a polyline (list of Vector) with quadratic
    Bezier arcs. Returns a new list of Vector."""
    pts = [V(p) for p in points]
    if len(pts) < 3:
        return pts
    out = [pts[0]]
    for i in range(1, len(pts) - 1):
        a, p, b = pts[i - 1], pts[i], pts[i + 1]
        d1, d2 = (a - p), (b - p)
        l1, l2 = d1.length, d2.length
        d1.normalize(); d2.normalize()
        ang = d1.angle(d2, pi)
        if ang > pi - 1e-3:
            out.append(p)
            continue
        t = radius / math.tan(ang / 2)
        t = min(t, 0.45 * l1, 0.45 * l2)
        s, e = p + d1 * t, p + d2 * t
        for k in range(seg + 1):
            u = k / seg
            out.append((1 - u) ** 2 * s + 2 * u * (1 - u) * p + u ** 2 * e)
    out.append(pts[-1])
    return out


def fillet_closed(points, radii, seg=3):
    """Closed polygon with a fillet radius per corner (0 keeps the corner sharp;
    the finishing bevel then chamfers it).  Returns a list of (u, v) tuples."""
    pts = [V(p) for p in points]
    n = len(pts)
    if not isinstance(radii, (list, tuple)):
        radii = [radii] * n
    out = []
    for i in range(n):
        a, p, b = pts[i - 1], pts[i], pts[(i + 1) % n]
        if radii[i] <= 0:
            out.append(p)
            continue
        d1, d2 = a - p, b - p
        l1, l2 = d1.length, d2.length
        d1.normalize(); d2.normalize()
        ang = d1.angle(d2, pi)
        if ang > pi - 1e-3:
            out.append(p)
            continue
        t = min(radii[i] / math.tan(ang / 2), 0.45 * l1, 0.45 * l2)
        s, e = p + d1 * t, p + d2 * t
        for k in range(seg + 1):
            u = k / seg
            out.append((1 - u) ** 2 * s + 2 * u * (1 - u) * p + u ** 2 * e)
    return [tuple(v) for v in out]


def arc_points(center, radius, a0, a1, n, plane='YZ'):
    """Arc in a principal plane. plane 'YZ': point=(cx, cy+r cos a, cz+r sin a)."""
    c = V(center)
    out = []
    for i in range(n + 1):
        a = a0 + (a1 - a0) * i / n
        if plane == 'YZ':
            out.append(c + V((0, radius * cos(a), radius * sin(a))))
        elif plane == 'XY':
            out.append(c + V((radius * cos(a), radius * sin(a), 0)))
        else:  # XZ
            out.append(c + V((radius * cos(a), 0, radius * sin(a))))
    return out


# =============================================================================
# Part: a bmesh under construction with named material slots
# =============================================================================

class Part:
    def __init__(self, name):
        self.name = name
        self.bm = bmesh.new()
        self.mats = []

    def mi(self, matname):
        if matname not in self.mats:
            self.mats.append(matname)
        return self.mats.index(matname)

    # -- low level -------------------------------------------------------------
    def add(self, verts, faces, mats, recalc=True, smooth=True):
        """verts: list of Vector. faces: index tuples. mats: str or list[str]."""
        bm = self.bm
        bv = [bm.verts.new(V(v)) for v in verts]
        new_faces = []
        for fi, f in enumerate(faces):
            try:
                face = bm.faces.new([bv[i] for i in f])
            except ValueError:
                continue
            m = mats if isinstance(mats, str) else mats[fi]
            face.material_index = self.mi(m)
            face.smooth = smooth
            new_faces.append(face)
        if recalc and new_faces:
            bmesh.ops.recalc_face_normals(bm, faces=new_faces)
        return bv, new_faces

    # -- primitives ------------------------------------------------------------
    def loft(self, rings, mat, band_mats=None, cap0=True, cap1=True, cap_mat=None, wrap=False):
        """Connect rings (lists of Vector, equal length, closed loops)."""
        n = len(rings[0])
        verts = [V(v) for r in rings for v in r]
        faces, fm = [], []
        nr = len(rings)
        pairs = [(i, i + 1) for i in range(nr - 1)] + ([(nr - 1, 0)] if wrap else [])
        for bi, (i, i2) in enumerate(pairs):
            for j in range(n):
                a = i * n + j
                b = i * n + (j + 1) % n
                c = i2 * n + (j + 1) % n
                d = i2 * n + j
                faces.append((a, b, c, d))
                fm.append(band_mats[bi] if band_mats else mat)
        if cap0 and not wrap:
            faces.append(tuple(reversed(range(n))))
            fm.append(cap_mat or mat)
        if cap1 and not wrap:
            last = (nr - 1) * n
            faces.append(tuple(range(last, last + n)))
            fm.append(cap_mat or mat)
        return self.add(verts, faces, fm)

    def extrude(self, profile, axis, a0, a1, mat, cap_mat=None):
        """Extrude a closed 2D profile along a principal axis.
        axis 'X': profile is (y,z); 'Y': (x,z); 'Z': (x,y)."""
        def P(u, v, a):
            return {'X': V((a, u, v)), 'Y': V((u, a, v)), 'Z': V((u, v, a))}[axis]
        rings = [[P(u, v, a0) for u, v in profile], [P(u, v, a1) for u, v in profile]]
        return self.loft(rings, mat, cap_mat=cap_mat)

    def box(self, center, size, mat, rot=None):
        """Axis box (optionally rotated: rot = Matrix 3x3/4x4 or Euler)."""
        M = Matrix.Translation(V(center))
        if rot is not None:
            M = M @ (rot.to_matrix().to_4x4() if hasattr(rot, 'to_matrix') else rot.to_4x4())
        M = M @ Matrix.Diagonal(V((size[0], size[1], size[2], 1.0)))
        res = bmesh.ops.create_cube(self.bm, size=1.0, matrix=M)
        faces = {f for v in res['verts'] for f in v.link_faces}
        idx = self.mi(mat)
        for f in faces:
            f.material_index = idx
            f.smooth = True
        return faces

    def box_minmax(self, lo, hi, mat):
        lo, hi = V(lo), V(hi)
        return self.box((lo + hi) / 2, hi - lo, mat)

    def sphere(self, center, radius, mat, u=12, v=8, scale=(1, 1, 1)):
        M = Matrix.Translation(V(center)) @ Matrix.Diagonal(V((*scale, 1.0)))
        res = bmesh.ops.create_uvsphere(self.bm, u_segments=u, v_segments=v, radius=radius, matrix=M)
        idx = self.mi(mat)
        for f in {f for vv in res['verts'] for f in vv.link_faces}:
            f.material_index = idx
            f.smooth = True

    def lathe(self, profile, segments, mat, center=(0, 0, 0), axis='X', mats=None,
              cap0=True, cap1=True, a0=0.0):
        """Revolve profile [(t, r)] around a principal axis through `center`.
        mats: optional per-profile-segment material list (len(profile)-1)."""
        c = V(center)
        rings = []
        for t, r in profile:
            ring = []
            for i in range(segments):
                a = a0 + 2 * pi * i / segments
                if axis == 'X':
                    ring.append(c + V((t, r * cos(a), r * sin(a))))
                elif axis == 'Y':
                    ring.append(c + V((r * cos(a), t, r * sin(a))))
                else:
                    ring.append(c + V((r * cos(a), r * sin(a), t)))
            rings.append(ring)
        return self.loft(rings, mat, band_mats=mats, cap0=cap0, cap1=cap1)

    def sweep(self, path, section, mat, closed=False, side=None, scales=None, cap=True,
              band_mats=None):
        """Sweep a closed 2D section [(a,b)] along a 3D path.
        side: fixed 'a' axis direction (e.g. X for paths in the YZ plane);
        otherwise rotation-minimising frames are used."""
        path = [V(p) for p in path]
        n = len(path)
        tangents = []
        for i in range(n):
            if closed:
                t = (path[(i + 1) % n] - path[i]).normalized() + (path[i] - path[i - 1]).normalized()
            elif i == 0:
                t = path[1] - path[0]
            elif i == n - 1:
                t = path[-1] - path[-2]
            else:
                t = (path[i + 1] - path[i]).normalized() + (path[i] - path[i - 1]).normalized()
            tangents.append(t.normalized())
        sides = []
        if side is not None:
            s0 = V(side)
            for t in tangents:
                s = s0 - t * s0.dot(t)
                sides.append(s.normalized())
        else:
            t0 = tangents[0]
            ref = V((0, 0, 1)) if abs(t0.z) < 0.9 else V((1, 0, 0))
            s = ref.cross(t0).normalized()
            for t in tangents:
                s = (s - t * s.dot(t)).normalized()
                sides.append(s)
        rings = []
        for i in range(n):
            t, s = tangents[i], sides[i]
            nrm = t.cross(s).normalized()
            k = scales[i] if scales else 1.0
            kx, ky = (k, k) if not isinstance(k, (tuple, list)) else k
            rings.append([path[i] + s * (a * kx) + nrm * (b * ky) for a, b in section])
        return self.loft(rings, mat, band_mats=band_mats, cap0=cap and not closed,
                         cap1=cap and not closed, wrap=closed)

    def cylinder(self, p0, p1, r, mat, n=12, cap=True):
        return self.sweep([p0, p1], circle(r, n), mat, cap=cap)

    def transform_verts(self, verts, M):
        bmesh.ops.transform(self.bm, matrix=M, verts=verts)

    # -- to object -------------------------------------------------------------
    def build(self, materials, coll=None, merge=1e-5):
        bm = self.bm
        if merge:
            bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=merge)
        me = bpy.data.meshes.new(self.name)
        bm.normal_update()
        bm.to_mesh(me)
        bm.free()
        for m in self.mats:
            me.materials.append(materials[m])
        ob = bpy.data.objects.new(self.name, me)
        (coll or bpy.context.scene.collection).objects.link(ob)
        return ob


# =============================================================================
# Object-level operations
# =============================================================================

def apply_modifiers(ob):
    """Bake the modifier stack into the mesh (no bpy.ops, background-safe)."""
    dg = bpy.context.evaluated_depsgraph_get()
    ob_eval = ob.evaluated_get(dg)
    me = bpy.data.meshes.new_from_object(ob_eval, preserve_all_data_layers=True, depsgraph=dg)
    old = ob.data
    ob.modifiers.clear()
    ob.data = me
    me.name = old.name
    bpy.data.meshes.remove(old)


def boolean(ob, cutters, op='DIFFERENCE'):
    """Apply boolean(s) with the EXACT solver and delete the cutter objects."""
    cutters = cutters if isinstance(cutters, (list, tuple)) else [cutters]
    for c in cutters:
        m = ob.modifiers.new('bool', 'BOOLEAN')
        m.operation = op
        m.solver = 'EXACT'
        m.object = c
        c.hide_render = True
        c.hide_set(True)
        apply_modifiers(ob)
    for c in cutters:
        bpy.data.objects.remove(c, do_unlink=True)


def cull_faces(ob, predicate):
    """Delete faces for which predicate(center, normal) is True (hidden faces
    waste atlas space and triangles)."""
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    bm.normal_update()
    dead = [f for f in bm.faces if predicate(f.calc_center_median(), f.normal)]
    bmesh.ops.delete(bm, geom=dead, context='FACES')
    bm.to_mesh(ob.data)
    bm.free()


def finish(ob, bevel=0.006, segments=2, angle=30.0, sharp=35.0, weighted=True, profile=0.5):
    """Bevel (angle limited) -> triangulate -> smooth + sharp edges -> weighted normals."""
    if bevel:
        m = ob.modifiers.new('bevel', 'BEVEL')
        m.width = bevel
        m.segments = segments
        m.limit_method = 'ANGLE'
        m.angle_limit = radians(angle)
        m.use_clamp_overlap = True
        m.profile = profile
        m.miter_outer = 'MITER_ARC'
    t = ob.modifiers.new('tri', 'TRIANGULATE')
    t.quad_method = 'BEAUTY'
    t.ngon_method = 'BEAUTY'
    apply_modifiers(ob)
    me = ob.data
    if hasattr(me, 'shade_smooth'):
        me.shade_smooth()
    else:
        for p in me.polygons:
            p.use_smooth = True
    if hasattr(me, 'set_sharp_from_angle'):
        me.set_sharp_from_angle(angle=radians(sharp))
    if weighted:
        w = ob.modifiers.new('wn', 'WEIGHTED_NORMAL')
        w.mode = 'FACE_AREA'
        w.weight = 50
        w.keep_sharp = True
        apply_modifiers(ob)
    return ob


def join(objs, name):
    objs = [o for o in objs if o is not None]
    target = objs[0]
    if len(objs) == 1:
        target.name = target.data.name = name
        return target
    with bpy.context.temp_override(active_object=target, object=target,
                                   selected_objects=objs, selected_editable_objects=objs):
        bpy.ops.object.join()
    target.name = name
    target.data.name = name
    return target


def set_origin(ob, pivot):
    """Move the object origin to `pivot` (world) without moving the geometry."""
    pivot = V(pivot)
    delta = pivot - ob.matrix_world.translation
    ob.data.transform(Matrix.Translation(-delta))
    ob.location = ob.location + delta


def empty(name, location, coll=None, size=0.1, display='PLAIN_AXES'):
    e = bpy.data.objects.new(name, None)
    e.empty_display_type = display
    e.empty_display_size = size
    e.location = V(location)
    (coll or bpy.context.scene.collection).objects.link(e)
    return e


def parent_keep_local(child, parent):
    """Parent with an identity parent-inverse so child.location is the true local
    offset (what Unity will show as localPosition)."""
    wm = child.matrix_world.copy()
    child.parent = parent
    child.matrix_parent_inverse.identity()
    child.matrix_world = wm


def add_ghosts(ob, offsets):
    """Linked duplicates of `ob` shifted by `offsets` (world). Left unselected
    during the bake they stand in for the neighbouring tiles, so baked AO and the
    cavity grime run continuously across the seam of a tiling module."""
    ghosts = []
    for i, d in enumerate(offsets):
        g = ob.copy()
        g.name = f'{ob.name}_ghost{i}'
        for c in ob.users_collection:
            c.objects.link(g)
        g.location = ob.location + V(d)
        ghosts.append(g)
    return ghosts


def remove_objects(objs):
    for o in objs:
        bpy.data.objects.remove(o, do_unlink=True)


def cull_open_ends(ob, axis, a0, a1, eps=1e-4):
    """Delete the end faces of a tiling module (faces lying in the planes
    axis=a0 / axis=a1) so it is open where it meets its neighbour."""
    i = 'xyz'.index(axis)
    cull_faces(ob, lambda c, n: abs(n[i]) > 0.99 and (abs(c[i] - a0) < eps or abs(c[i] - a1) < eps))


def cap_from_boundary(src, name, axis, value, eps=1e-4):
    """New object: the open boundary loops of `src` in the plane axis=value,
    filled with flat faces (normals away from the module).  Matches the
    finished (bevelled) section exactly, so it closes the end of a tiled run."""
    import bmesh as _bm
    i = 'xyz'.index(axis)
    bm = _bm.new()
    bm.from_mesh(src.data)
    edges = [e for e in bm.edges if e.is_boundary and
             all(abs(v.co[i] - value) < eps for v in e.verts)]
    res = _bm.ops.holes_fill(bm, edges=edges, sides=0)
    new = set(res['faces'])
    _bm.ops.delete(bm, geom=[f for f in bm.faces if f not in new], context='FACES')
    _bm.ops.triangulate(bm, faces=bm.faces[:], quad_method='BEAUTY', ngon_method='BEAUTY')
    outward = -1.0 if value <= (src.bound_box[0][i] + src.bound_box[6][i]) / 2 else 1.0
    bm.normal_update()
    for f in bm.faces:
        if f.normal[i] * outward < 0:
            f.normal_flip()
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    for m in src.data.materials:
        me.materials.append(m)
    for p in me.polygons:
        p.use_smooth = False
    ob = bpy.data.objects.new(name, me)
    for c in src.users_collection:
        c.objects.link(ob)
    ob.matrix_world = src.matrix_world.copy()
    return ob


def rotate_z180(objs):
    """Turn objects 180 deg about world Z keeping identity rotations (mesh data
    and locations are rotated).  Kit modules are authored with +X = Unity +X
    and +Y = Unity +Z; after this turn the FBX export maps them exactly so."""
    R = Matrix.Rotation(pi, 4, 'Z')
    for o in objs:
        if o.type == 'MESH':
            o.data.transform(R)
        o.location = R @ o.location


def tri_count(ob):
    return sum(len(p.vertices) - 2 for p in ob.data.polygons)


def make_lod(ob, ratio, name, sharp=35.0, symmetry=None):
    """Decimated copy of `ob` (same transform, UVs and material) for a lower LOD.
    Quadric collapse keeps UVs interpolated, so the LOD0 atlas still maps; the
    normals are rebuilt (sharp-by-angle + weighted normals) on the new topology."""
    lod = ob.copy()
    lod.data = ob.data.copy()
    lod.name = name
    lod.data.name = name
    for c in ob.users_collection:
        c.objects.link(lod)
    if ratio < 1.0:
        m = lod.modifiers.new('decimate', 'DECIMATE')
        m.decimate_type = 'COLLAPSE'
        m.ratio = ratio
        m.use_collapse_triangulate = True
        if symmetry:
            m.use_symmetry = True
            m.symmetry_axis = symmetry
        apply_modifiers(lod)
    me = lod.data
    if hasattr(me, 'set_sharp_from_angle'):
        me.set_sharp_from_angle(angle=radians(sharp))
    w = lod.modifiers.new('wn', 'WEIGHTED_NORMAL')
    w.mode = 'FACE_AREA'
    w.weight = 50
    w.keep_sharp = True
    apply_modifiers(lod)
    return lod


def bake_into(mesh, node):
    """Re-express `mesh` in `node`'s space so it can sit under it with an
    identity local transform (Unity: localPosition 0, localRotation identity)."""
    rel = node.matrix_world.inverted() @ mesh.matrix_world
    mesh.parent = None
    mesh.data.transform(rel)
    mesh.parent = node
    mesh.matrix_parent_inverse.identity()
    mesh.matrix_basis = Matrix.Identity(4)


# =============================================================================
# Shader node builder
# =============================================================================

def _sock(sockets, ident):
    for s in sockets:
        if s.identifier == ident:
            return s
    raise KeyError(ident)


class NB:
    """Terse node-graph builder. Values may be sockets, nodes (first output) or
    constants."""

    # {axis: period in m}. Object-space noise becomes periodic along these axes so
    # modules that tile in Unity (wall courses, rails, girders) show no seam.
    # Set it before building a module's materials; it is copied per material.
    default_period = {}

    def __init__(self, mat, period=None):
        mat.use_nodes = True
        self.mat = mat
        self.nt = mat.node_tree
        self.nt.nodes.clear()
        self._tc = None
        self._geo = None
        self.period = dict(NB.default_period if period is None else period)

    def link(self, value, sock):
        if isinstance(value, bpy.types.NodeSocket):
            self.nt.links.new(value, sock)
        elif isinstance(value, bpy.types.Node):
            self.nt.links.new(value.outputs[0], sock)
        elif value is not None:
            try:
                sock.default_value = value
            except (TypeError, ValueError):
                if len(value) == 3:
                    sock.default_value = (*value, 1.0)
                else:
                    raise

    def node(self, type_, props=None, **inputs):
        n = self.nt.nodes.new(type_)
        for k, v in (props or {}).items():
            setattr(n, k, v)
        for k, v in inputs.items():
            self.link(v, n.inputs[k.replace('__', ' ')])
        return n

    # --- coordinates / geometry ---
    @property
    def obj(self):
        if self._tc is None:
            self._tc = self.node('ShaderNodeTexCoord')
        return self._tc.outputs['Object']

    @property
    def geo(self):
        if self._geo is None:
            self._geo = self.node('ShaderNodeNewGeometry')
        return self._geo

    def xyz(self, vec):
        n = self.node('ShaderNodeSeparateXYZ', Vector=vec)
        return n.outputs['X'], n.outputs['Y'], n.outputs['Z']

    def vec(self, x, y, z):
        return self.node('ShaderNodeCombineXYZ', X=x, Y=y, Z=z).outputs[0]

    # --- math ---
    def m(self, op, a, b=None, c=None, clamp=False):
        n = self.nt.nodes.new('ShaderNodeMath')
        n.operation = op
        n.use_clamp = clamp
        self.link(a, n.inputs[0])
        if b is not None:
            self.link(b, n.inputs[1])
        if c is not None:
            self.link(c, n.inputs[2])
        return n.outputs[0]

    def vm(self, op, a, b=None, scale=None):
        n = self.nt.nodes.new('ShaderNodeVectorMath')
        n.operation = op
        self.link(a, n.inputs[0])
        if b is not None:
            self.link(b, n.inputs[1])
        if scale is not None:
            self.link(scale, n.inputs['Scale'])
        if op in ('DOT_PRODUCT', 'LENGTH', 'DISTANCE'):
            return n.outputs['Value']
        return n.outputs['Vector']

    def mr(self, value, fmin, fmax, tmin=0.0, tmax=1.0, smooth=False, clamp=True):
        n = self.node('ShaderNodeMapRange', props={'clamp': clamp,
                      'interpolation_type': 'SMOOTHSTEP' if smooth else 'LINEAR'})
        self.link(value, n.inputs['Value'])
        self.link(fmin, n.inputs['From Min'])
        self.link(fmax, n.inputs['From Max'])
        self.link(tmin, n.inputs['To Min'])
        self.link(tmax, n.inputs['To Max'])
        return n.outputs['Result']

    def mixc(self, fac, a, b):
        n = self.nt.nodes.new('ShaderNodeMix')
        n.data_type = 'RGBA'
        n.clamp_factor = True
        self.link(fac, _sock(n.inputs, 'Factor_Float'))
        self.link(a, _sock(n.inputs, 'A_Color'))
        self.link(b, _sock(n.inputs, 'B_Color'))
        return _sock(n.outputs, 'Result_Color')

    def mixf(self, fac, a, b):
        n = self.nt.nodes.new('ShaderNodeMix')
        n.data_type = 'FLOAT'
        n.clamp_factor = True
        self.link(fac, _sock(n.inputs, 'Factor_Float'))
        self.link(a, _sock(n.inputs, 'A_Float'))
        self.link(b, _sock(n.inputs, 'B_Float'))
        return _sock(n.outputs, 'Result_Float')

    def multc(self, a, b):
        n = self.nt.nodes.new('ShaderNodeMix')
        n.data_type = 'RGBA'
        n.blend_type = 'MULTIPLY'
        self.link(1.0, _sock(n.inputs, 'Factor_Float'))
        self.link(a, _sock(n.inputs, 'A_Color'))
        self.link(b, _sock(n.inputs, 'B_Color'))
        return _sock(n.outputs, 'Result_Color')

    # --- textures ---
    def noise(self, scale, detail=4.0, rough=0.5, vec=None, dist=0.0, aniso=None):
        """Noise over object space (scaled per axis by `aniso`) or over `vec`.
        Object-space noise honours self.period (see default_period)."""
        n = self.node('ShaderNodeTexNoise', Scale=scale, Detail=detail, Roughness=rough,
                      Distortion=dist)
        if vec is None and self.period:
            v, w = self._periodic_coords(aniso or (1.0, 1.0, 1.0))
            n.noise_dimensions = '4D'
            self.link(v, n.inputs['Vector'])
            self.link(w, n.inputs['W'])
        else:
            if vec is None:
                vec = self.scaled(*aniso) if aniso else self.obj
            self.link(vec, n.inputs['Vector'])
        return n.outputs['Fac']

    def _periodic_coords(self, aniso):
        """Wrap each periodic axis onto a circle of circumference period*scale, so
        4D noise repeats exactly every period while keeping its metric scale.
        One periodic axis keeps the other two; two keep none (4 components)."""
        x, y, z = self.xyz(self.obj)
        c = {'x': x, 'y': y, 'z': z}
        a = dict(zip('xyz', aniso))
        comps = []
        for ax, per in self.period.items():
            t = self.m('MULTIPLY', c[ax], 2 * pi / per)
            r = per * a[ax] / (2 * pi)
            comps += [self.m('MULTIPLY', self.m('COSINE', t), r),
                      self.m('MULTIPLY', self.m('SINE', t), r)]
        comps += [self.m('MULTIPLY', c[ax], a[ax]) for ax in 'xyz' if ax not in self.period]
        comps = (comps + [0.0] * 4)[:4]
        return self.vec(*comps[:3]), comps[3]

    def scaled(self, sx, sy, sz, vec=None):
        return self.vm('MULTIPLY', vec if vec is not None else self.obj, (sx, sy, sz))

    def image(self, img, vec, extension='CLIP', interp='Cubic'):
        n = self.node('ShaderNodeTexImage', props={'image': img, 'extension': extension,
                                                   'interpolation': interp})
        self.link(vec, n.inputs['Vector'])
        return n.outputs['Color'], n.outputs['Alpha']

    # --- surface masks ---
    def bevel_normal(self, radius=0.008, samples=8):
        n = self.node('ShaderNodeBevel', props={'samples': samples}, Radius=radius)
        return n.outputs['Normal']

    def edge_mask(self, distance=0.007, lo=0.12, hi=0.45, samples=16):
        """Convex edge / small-radius mask from *inside* ambient occlusion: rays shot
        into the solid hit the neighbouring face only near convex edges, so the mask
        follows real curvature and ignores faceting / weighted-normal skew (normal-
        comparison masks light up whole curved low-poly surfaces).  Needs closed,
        thick-enough solids (sheets thinner than `distance` read as all-edge)."""
        n = self.node('ShaderNodeAmbientOcclusion', props={'samples': samples,
                      'inside': True, 'only_local': True}, Distance=distance)
        return self.mr(self.m('SUBTRACT', 1.0, n.outputs['AO']), lo, hi)

    def cavity(self, distance=0.12, samples=16):
        n = self.node('ShaderNodeAmbientOcclusion', props={'samples': samples,
                      'only_local': False}, Distance=distance)
        return self.mr(n.outputs['AO'], 1.0, 0.3)      # 0 open .. 1 occluded

    def up_mask(self, lo=0.55, hi=0.95):
        _, _, nz = self.xyz(self.geo.outputs['Normal'])
        return self.mr(nz, lo, hi)

    def bump(self, height, strength, distance, normal=None):
        n = self.node('ShaderNodeBump', Strength=strength, Distance=distance, Height=height)
        if normal is not None:
            self.link(normal, n.inputs['Normal'])
        return n.outputs['Normal']


# =============================================================================
# Material "channels" contract
# =============================================================================
# Every source material is described by channel sockets:
#   albedo (color, linear), rough (float), metal (float), emit (color),
#   normal (vector, world space shading normal incl. bevel/bump)
# The material output is wired to a Principled BSDF for preview/normal baking, and
# re-wired to an Emission shader per channel for baking.

MATERIAL_CHANNELS = {}


def finalize_material(nb, albedo, rough, metal, normal=None, emit=(0, 0, 0), alpha=None):
    bsdf = nb.node('ShaderNodeBsdfPrincipled')
    nb.link(albedo, bsdf.inputs['Base Color'])
    nb.link(rough, bsdf.inputs['Roughness'])
    nb.link(metal, bsdf.inputs['Metallic'])
    if normal is not None:
        nb.link(normal, bsdf.inputs['Normal'])
    nb.link(emit, bsdf.inputs['Emission Color'])
    bsdf.inputs['Emission Strength'].default_value = 1.0
    out = nb.node('ShaderNodeOutputMaterial')
    nb.nt.links.new(bsdf.outputs[0], out.inputs['Surface'])
    emit_node = nb.node('ShaderNodeEmission')
    emit_node.name = 'BAKE_EMIT'

    def as_sock(v):
        if isinstance(v, bpy.types.NodeSocket):
            return v
        if isinstance(v, (int, float)):
            n = nb.node('ShaderNodeValue')
            n.outputs[0].default_value = float(v)
            return n.outputs[0]
        n = nb.node('ShaderNodeRGB')
        n.outputs[0].default_value = (*v[:3], 1.0)
        return n.outputs[0]

    MATERIAL_CHANNELS[nb.mat.name] = {
        'albedo': as_sock(albedo), 'rough': as_sock(rough), 'metal': as_sock(metal),
        'emit': as_sock(emit), 'alpha': as_sock(1.0 if alpha is None else alpha),
        'bsdf': bsdf, 'out': out, 'emit_node': emit_node,
    }
    return nb.mat


def adopt_material(mat, source_uv):
    """Register a material not built with NB (e.g. an imported Principled BSDF material) so
    bake_texture_set can bake it: its Base Color / Roughness / Metallic / Emission / Alpha inputs
    (linked sockets, or their values) become the bake channels.  Its image textures are pinned to
    `source_uv`, so the bake can target another (atlas) UV map."""
    nt = mat.node_tree
    bsdf = next((n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED'), None)
    out = next((n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL' and n.is_active_output), None) or \
        next((n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL'), None)
    if bsdf is None or out is None:
        raise RuntimeError(f"[kit] adopt_material: {mat.name} has no Principled BSDF / output")
    for n in list(nt.nodes):
        if n.type == 'TEX_IMAGE' and not n.inputs['Vector'].is_linked:
            uv = nt.nodes.new('ShaderNodeUVMap')
            uv.uv_map = source_uv
            nt.links.new(uv.outputs['UV'], n.inputs['Vector'])

    def chan(name, fallback):
        inp = bsdf.inputs.get(name)
        if inp is not None and inp.is_linked:
            return inp.links[0].from_socket
        v = inp.default_value if inp is not None else fallback
        if hasattr(v, '__len__'):
            n = nt.nodes.new('ShaderNodeRGB')
            n.outputs[0].default_value = (v[0], v[1], v[2], 1.0)
        else:
            n = nt.nodes.new('ShaderNodeValue')
            n.outputs[0].default_value = float(v)
        return n.outputs[0]

    emit_node = nt.nodes.new('ShaderNodeEmission')
    emit_node.name = 'BAKE_EMIT'
    emit_col = bsdf.inputs.get('Emission Color')
    strength = bsdf.inputs.get('Emission Strength')
    glows = strength is not None and (strength.is_linked or strength.default_value > 0)
    MATERIAL_CHANNELS[mat.name] = {
        'albedo': chan('Base Color', (0.8, 0.8, 0.8)), 'rough': chan('Roughness', 0.5),
        'metal': chan('Metallic', 0.0), 'alpha': chan('Alpha', 1.0),
        'emit': chan('Emission Color', (0, 0, 0)) if glows and emit_col is not None else chan('__none__', (0, 0, 0)),
        'bsdf': bsdf, 'out': out, 'emit_node': emit_node,
    }
    return mat


# =============================================================================
# UV atlas + baking
# =============================================================================

def select_only(objs, active=None):
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = active or objs[0]


def atlas_unwrap(objs, angle=55.0, margin=0.0035, weights=None, default_weight=1.0, project=True):
    """One shared UV atlas across all objects (multi-object edit mode).
    weights: {object_name: texel-density factor} (e.g. 0.7 for dark, rarely seen
    parts) - applied before packing so important surfaces get more pixels.
    Objects not listed get `default_weight`.
    project=False keeps the active UV map's islands (e.g. an imported character's
    hand-made UVs) and only scales and packs them into the atlas."""
    select_only(objs)
    for o in objs:
        if not o.data.uv_layers:
            o.data.uv_layers.new(name='UVMap')
    if project:
        bpy.ops.object.mode_set(mode='EDIT')
        bpy.ops.mesh.select_all(action='SELECT')
        bpy.ops.uv.smart_project(angle_limit=radians(angle), island_margin=margin,
                                 area_weight=0.0, correct_aspect=True, scale_to_bounds=False)
        bpy.ops.object.mode_set(mode='OBJECT')
    else:
        bpy.ops.object.mode_set(mode='EDIT')
        bpy.ops.mesh.select_all(action='SELECT')
        bpy.ops.uv.select_all(action='SELECT')
        bpy.ops.uv.average_islands_scale()          # one texel density (by 3D area), then the weights
        bpy.ops.object.mode_set(mode='OBJECT')
    for o in objs:
        k = (weights or {}).get(o.name, default_weight)
        if k != 1.0:
            uv = o.data.uv_layers.active.data
            co = np.empty(len(uv) * 2, np.float32)
            uv.foreach_get('uv', co)
            uv.foreach_set('uv', co * k)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.select_all(action='SELECT')
    for kw in (dict(rotate=True, rotate_method='AXIS_ALIGNED', scale=True, margin=margin,
                    shape_method='CONCAVE'),
               dict(rotate=True, scale=True, margin=margin)):
        try:
            bpy.ops.uv.pack_islands(**kw)
            break
        except TypeError:
            continue
    bpy.ops.object.mode_set(mode='OBJECT')


def _bake_target(mat, img):
    nt = mat.node_tree
    n = nt.nodes.get('BAKE_TARGET')
    if n is None:
        n = nt.nodes.new('ShaderNodeTexImage')
        n.name = 'BAKE_TARGET'
    n.image = img
    nt.nodes.active = n
    n.select = True


def bake_channel(objs, mats, channel, img, samples=16, margin=16):
    scene = bpy.context.scene
    scene.cycles.samples = samples
    for m in mats:
        ch = MATERIAL_CHANNELS[m.name]
        links = m.node_tree.links
        out = ch['out']
        for l in list(out.inputs['Surface'].links):
            links.remove(l)
        if channel in ('albedo', 'rough', 'metal', 'emit', 'alpha'):
            src = ch[channel]
            e = ch['emit_node']
            for l in list(e.inputs['Color'].links):
                links.remove(l)
            if src.type in ('VALUE', 'INT'):
                cv = m.node_tree.nodes.new('ShaderNodeCombineXYZ')
                links.new(src, cv.inputs[0]); links.new(src, cv.inputs[1]); links.new(src, cv.inputs[2])
                links.new(cv.outputs[0], e.inputs['Color'])
            else:
                links.new(src, e.inputs['Color'])
            e.inputs['Strength'].default_value = 1.0
            links.new(e.outputs[0], out.inputs['Surface'])
        else:
            links.new(ch['bsdf'].outputs[0], out.inputs['Surface'])
        _bake_target(m, img)
    select_only(objs)
    bake_type = {'albedo': 'EMIT', 'rough': 'EMIT', 'metal': 'EMIT', 'emit': 'EMIT', 'alpha': 'EMIT',
                 'normal': 'NORMAL', 'ao': 'AO'}[channel]
    # ADJACENT_FACES continues each island's texels across its UV seams; EXTEND
    # just smears the border pixel and lets neighbouring islands bleed in.
    kw = dict(type=bake_type, margin=margin, margin_type='ADJACENT_FACES', use_clear=True)
    if bake_type == 'NORMAL':
        kw.update(normal_space='TANGENT')
    print(f"[kit] baking {channel} ({img.size[0]}px, {samples} spp)...")
    try:
        bpy.ops.object.bake(**kw)
    except TypeError:
        kw['margin_type'] = 'EXTEND'
        bpy.ops.object.bake(**kw)
    # restore preview wiring
    for m in mats:
        ch = MATERIAL_CHANNELS[m.name]
        links = m.node_tree.links
        for l in list(ch['out'].inputs['Surface'].links):
            links.remove(l)
        links.new(ch['bsdf'].outputs[0], ch['out'].inputs['Surface'])


def new_image(name, size, non_color=False):
    old = bpy.data.images.get(name)
    if old:
        bpy.data.images.remove(old)
    img = bpy.data.images.new(name, size, size, alpha=False, float_buffer=False)
    if non_color:
        img.colorspace_settings.name = 'Non-Color'
    return img


def image_array(img):
    w, h = img.size
    a = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(a)
    return a.reshape(h, w, 4)


def downsample(arr, factor):
    if factor == 1:
        return arr
    h, w, c = arr.shape
    return arr.reshape(h // factor, factor, w // factor, factor, c).mean(axis=(1, 3))


def write_png(path, arr, channels):
    """arr float [0..1] (H,W,4) bottom-up (Blender order). Writes 8-bit PNG."""
    data = np.clip(arr[::-1, :, :channels] * 255.0 + 0.5, 0, 255).astype(np.uint8)
    h, w = data.shape[:2]
    raw = np.concatenate([np.zeros((h, 1), np.uint8), data.reshape(h, w * channels)], axis=1)
    ctype = {1: 0, 3: 2, 4: 6}[channels]

    def chunk(tag, payload):
        c = struct.pack('>I', len(payload)) + tag + payload
        return c + struct.pack('>I', zlib.crc32(tag + payload) & 0xffffffff)

    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, ctype, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(raw.tobytes(), 7))
    png += chunk(b'IEND', b'')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'wb') as f:
        f.write(png)
    print(f"[kit] wrote {path} ({w}x{h}, {channels}ch)")


def bake_texture_set(objs, out_dir, prefix, bake_size=4096, save_size=2048,
                     samples_color=48, samples_ao=256, margin=16, alpha=False):
    """Bake the full PBR set for `objs` and write Unity-ready PNGs:
        <prefix>_Albedo.png             sRGB RGB
        <prefix>_MetallicSmoothness.png  linear RGBA  (R = metallic, A = smoothness)
        <prefix>_Normal.png             linear RGB   (OpenGL / Unity convention)
        <prefix>_Occlusion.png          linear gray
        <prefix>_Emission.png           sRGB RGB
    margin: bake bleed in bake pixels. Blender writes each object's margin in
    turn, so a later object's margin overwrites earlier objects' islands: keep
    it below half the atlas island gap (see bake_kit).
    alpha: also bake the materials' Alpha into the albedo's A channel (RGBA PNG: cut-out
    cards such as hair; Unity's postprocessor then makes the material Cutout).
    Returns dict of file paths."""
    mats = sorted({s.material for o in objs for s in o.material_slots if s.material},
                  key=lambda m: m.name)
    f = bake_size // save_size
    imgs = {
        'albedo': new_image(prefix + '_bake_albedo', bake_size),
        'rough': new_image(prefix + '_bake_rough', bake_size, True),
        'metal': new_image(prefix + '_bake_metal', bake_size, True),
        'emit': new_image(prefix + '_bake_emit', bake_size),
        'normal': new_image(prefix + '_bake_normal', bake_size, True),
        'ao': new_image(prefix + '_bake_ao', bake_size, True),
    }
    spp = {'albedo': samples_color, 'rough': samples_color, 'metal': samples_color,
           'emit': 4, 'normal': 16, 'ao': samples_ao, 'alpha': samples_color}
    if alpha:
        imgs['alpha'] = new_image(prefix + '_bake_alpha', bake_size, True)
    for ch in ('albedo', 'rough', 'metal', 'emit', 'normal', 'ao') + (('alpha',) if alpha else ()):
        bake_channel(objs, mats, ch, imgs[ch], samples=spp[ch], margin=margin)

    arr = {k: downsample(image_array(v), f) for k, v in imgs.items()}
    # A device that fails silently bakes all-black: never write such a set.
    for k in ('albedo', 'ao'):
        if float(arr[k][..., :3].max()) <= 0.0:
            raise RuntimeError(f"[kit] the {k} bake came out all black (Cycles device "
                               f"{bpy.context.scene.cycles.device}); retry with --cpu")
    paths = {}
    p = lambda s: os.path.join(out_dir, f'{prefix}_{s}.png')

    if alpha:
        rgba = arr['albedo'].copy()
        rgba[..., 3] = arr['alpha'][..., 0]
        paths['albedo'] = p('Albedo'); write_png(paths['albedo'], rgba, 4)
    else:
        paths['albedo'] = p('Albedo'); write_png(paths['albedo'], arr['albedo'], 3)
    paths['emission'] = p('Emission'); write_png(paths['emission'], arr['emit'], 3)

    ms = np.zeros_like(arr['metal'])
    ms[..., 0] = ms[..., 1] = ms[..., 2] = arr['metal'][..., 0]
    ms[..., 3] = 1.0 - arr['rough'][..., 0]
    paths['metallic'] = p('MetallicSmoothness'); write_png(paths['metallic'], ms, 4)

    n = arr['normal'][..., :3] * 2.0 - 1.0
    n /= np.maximum(np.linalg.norm(n, axis=2, keepdims=True), 1e-6)
    nn = np.ones_like(arr['normal'])
    nn[..., :3] = n * 0.5 + 0.5
    paths['normal'] = p('Normal'); write_png(paths['normal'], nn, 3)

    ao = arr['ao'].copy()
    ao[..., 1] = ao[..., 2] = ao[..., 0]
    paths['occlusion'] = p('Occlusion'); write_png(paths['occlusion'], ao, 3)
    return paths


def make_runtime_material(name, paths, emission_strength=2.0):
    """The single material the exported asset uses (mirrors Unity Standard)."""
    mat = bpy.data.materials.new(name)
    nb = NB(mat)
    uv = nb.node('ShaderNodeUVMap').outputs[0]
    def load(key, non_color):
        img = bpy.data.images.load(paths[key], check_existing=True)
        if non_color:
            img.colorspace_settings.name = 'Non-Color'
        return img
    alb, _ = nb.image(load('albedo', False), uv, 'EXTEND', 'Linear')
    ms_img = load('metallic', True)
    ms_img.alpha_mode = 'CHANNEL_PACKED'
    msc, msa = nb.image(ms_img, uv, 'EXTEND', 'Linear')
    rough = nb.m('SUBTRACT', 1.0, msa)
    metal = nb.xyz(msc)[0]
    nrm_c, _ = nb.image(load('normal', True), uv, 'EXTEND', 'Linear')
    nm = nb.node('ShaderNodeNormalMap', Color=nrm_c)
    em, _ = nb.image(load('emission', False), uv, 'EXTEND', 'Linear')
    bsdf = nb.node('ShaderNodeBsdfPrincipled')
    nb.link(alb, bsdf.inputs['Base Color'])
    nb.link(rough, bsdf.inputs['Roughness'])
    nb.link(metal, bsdf.inputs['Metallic'])
    nb.link(nm.outputs[0], bsdf.inputs['Normal'])
    nb.link(em, bsdf.inputs['Emission Color'])
    bsdf.inputs['Emission Strength'].default_value = emission_strength
    out = nb.node('ShaderNodeOutputMaterial')
    nb.nt.links.new(bsdf.outputs[0], out.inputs['Surface'])
    return mat


def assign_single_material(objs, mat):
    for o in objs:
        o.data.materials.clear()
        o.data.materials.append(mat)
        for p in o.data.polygons:
            p.material_index = 0


# =============================================================================
# Decal images (text rendered with Workbench)
# =============================================================================

def render_text_image(text, path, height_px=256, font_path=None, bold_offset=0.0,
                      margin=0.08, supersample=2):
    """Render `text` (white on transparent) to a PNG and return (image, aspect).

    bold_offset thickens the glyphs by that many font units. It is applied as a
    round dilation of the rendered alpha, NOT as the curve `offset`: offsetting a
    font outline leaves long miter spikes at its sharp corners (the "u" / "L" / "n"
    spikes of the first logo decals)."""
    scene = bpy.data.scenes.new('DecalScene')
    cu = bpy.data.curves.new('decal_txt', 'FONT')
    cu.body = text
    cu.align_x = 'CENTER'
    cu.align_y = 'CENTER'
    if font_path and os.path.exists(font_path):
        cu.font = bpy.data.fonts.load(font_path, check_existing=True)
    ob = bpy.data.objects.new('decal_txt', cu)
    scene.collection.objects.link(ob)
    cam_data = bpy.data.cameras.new('decal_cam')
    cam_data.type = 'ORTHO'
    cam = bpy.data.objects.new('decal_cam', cam_data)
    scene.collection.objects.link(cam)
    cam.location = (0, 0, 5)
    scene.camera = cam
    dg = bpy.context.evaluated_depsgraph_get()
    dg.update()
    # measure text bounds from evaluated mesh; leave room for the dilation
    ob_eval = ob.evaluated_get(dg)
    me = ob_eval.to_mesh()
    xs = [v.co.x for v in me.vertices]; ys = [v.co.y for v in me.vertices]
    ob_eval.to_mesh_clear()
    w = max(xs) - min(xs) + 2 * bold_offset
    h = max(ys) - min(ys) + 2 * bold_offset
    cx, cy = (max(xs) + min(xs)) / 2, (max(ys) + min(ys)) / 2
    w *= (1 + margin); h *= (1 + margin * 2)
    cam.location = (cx, cy, 5)
    cam_data.ortho_scale = max(w, h)
    ss = max(1, int(supersample))
    res_y = height_px * ss
    res_x = int(round(height_px * w / h)) * ss
    scene.render.resolution_y = res_y
    scene.render.resolution_x = res_x
    scene.render.resolution_percentage = 100
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.render.film_transparent = True
    scene.display.shading.light = 'FLAT'
    scene.display.shading.color_type = 'SINGLE'
    scene.display.shading.single_color = (1, 1, 1)
    scene.display.render_aa = '32'
    scene.view_settings.view_transform = 'Standard'
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True, scene=scene.name)
    aspect = w / h
    bpy.data.objects.remove(ob); bpy.data.objects.remove(cam)
    bpy.data.scenes.remove(scene)

    img = bpy.data.images.load(path, check_existing=False)
    import numpy as np
    px = np.empty(res_x * res_y * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    px = px.reshape(res_y, res_x, 4)
    alpha = px[:, :, 3].copy()
    r = bold_offset * res_y / h                       # dilation radius in pixels
    if r >= 0.5:
        ri = int(np.ceil(r))
        pad = np.pad(alpha, ri)
        out = alpha.copy()
        for dy in range(-ri, ri + 1):
            for dx in range(-ri, ri + 1):
                d = (dx * dx + dy * dy) ** 0.5
                if d > r + 0.5:
                    continue
                cover = min(1.0, r + 0.5 - d)         # soft rim keeps the edge anti-aliased
                sh = pad[ri + dy:ri + dy + res_y, ri + dx:ri + dx + res_x]
                np.maximum(out, sh * cover, out=out)
        alpha = out
    px[:, :, :3] = 1.0
    px[:, :, 3] = alpha
    img.pixels.foreach_set(px.ravel())
    if ss > 1:
        img.scale(res_x // ss, res_y // ss)
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    return img, aspect


# =============================================================================
# Export
# =============================================================================

def export_fbx(root, path):
    """Export `root` and its descendants for Unity (scale 1, Y-up, +Z forward,
    identity local rotations on every node).

    The space conversion is done here, not by the exporter's
    bake_space_transform: that option only handles the root and its direct
    children.  For deeper nodes it mixes Blender and FBX parent spaces, which
    swaps their local y/z offsets and leaves a stray 90 deg X rotation (the
    Beacon_LOD0 / Bay_A / LoadSocket import bug).  With G the Blender->FBX axis
    matrix, every node needs FBX local G.L.G^-1 and every mesh G.v.  So we
    temporarily set each descendant's local to G.L.G^-1, transform its mesh by
    G, and give the root L.G^-1.  The exporter (bake off) then pre-multiplies
    G onto the root only.  Blender world placement is unchanged throughout, and
    everything is restored after export."""
    from bpy_extras.io_utils import axis_conversion
    G = axis_conversion(to_forward='-Z', to_up='Y').to_4x4()
    Gi = G.inverted()
    kids = list(root.children_recursive)
    saved = {o.name: (o.matrix_basis.copy(), o.matrix_parent_inverse.copy()) for o in [root] + kids}
    meshes = {o.data for o in kids if o.type == 'MESH'}     # each mesh once, even if shared
    for o in kids:
        local = o.matrix_parent_inverse @ o.matrix_basis
        o.matrix_parent_inverse.identity()
        o.matrix_basis = G @ local @ Gi
    for me in meshes:
        me.transform(G)
    root.matrix_basis = root.matrix_basis @ Gi
    bpy.context.view_layer.update()

    objs = [root] + kids
    select_only(objs, root)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    try:
        bpy.ops.export_scene.fbx(
            filepath=path, use_selection=True, object_types={'EMPTY', 'MESH'},
            apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL',
            axis_forward='-Z', axis_up='Y', bake_space_transform=False,
            use_mesh_modifiers=True, mesh_smooth_type='OFF', use_tspace=True,
            use_triangles=True, add_leaf_bones=False, bake_anim=False,
            path_mode='STRIP', embed_textures=False, use_custom_props=False)
    finally:
        for me in meshes:
            me.transform(Gi)
        for o in [root] + kids:
            o.matrix_basis, o.matrix_parent_inverse = saved[o.name]
        bpy.context.view_layer.update()
    print(f"[kit] exported {path}")
