"""
Export a built SimuLean asset for FlexSim: one file per LOD, textures embedded.

Run (after the asset script has written Tools/Blender/build/<Asset>.blend):
    blender -b --factory-startup -P Tools/Blender/export_flexsim.py -- --asset Forklift

Outputs  Tools/Blender/build/FlexSim/<Asset>_LOD{0,1,2}.fbx / .glb  (+ <Asset>_AlbedoAO.png)

FlexSim specifics
  - Z-up, metres (FlexSim's native frame); the FBX carries no axis conversion.
  - The asset is turned to face +X: FlexSim task executers travel along local +X.
  - FlexSim has no ambient-occlusion input, so the baked AO is multiplied into the
    albedo (<Asset>_AlbedoAO.png) and that is the diffuse texture of the export.
  - Only the asset hierarchy is exported (no preview props, lights or cameras); the
    named transforms (Mast, Carriage, Wheel_FL, ...) are kept for FlexSim's
    Animation Creator.
  - The .glb is the fallback format (glTF is Y-up by spec; the exporter converts).
"""

import os
import sys
import argparse
from math import radians

import bpy
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
BUILD_DIR = os.path.join(HERE, 'build')
MODELS_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models')
AO_STRENGTH = 0.85


def args():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--asset', default='Forklift')
    ap.add_argument('--no-glb', action='store_true')
    return ap.parse_args(argv)


def albedo_with_ao(asset, out_dir):
    src = os.path.join(MODELS_DIR, asset)
    alb = bpy.data.images.load(os.path.join(src, f'{asset}_Albedo.png'))
    ao = bpy.data.images.load(os.path.join(src, f'{asset}_Occlusion.png'))
    assert tuple(alb.size) == tuple(ao.size), 'albedo / occlusion size mismatch'
    w, h = alb.size
    a = np.empty(w * h * 4, np.float32); alb.pixels.foreach_get(a); a = a.reshape(-1, 4)
    o = np.empty(w * h * 4, np.float32); ao.pixels.foreach_get(o); o = o.reshape(-1, 4)
    a[:, :3] *= (1.0 - AO_STRENGTH) + AO_STRENGTH * o[:, :1]
    img = bpy.data.images.new(f'{asset}_AlbedoAO', w, h, alpha=False)
    img.pixels.foreach_set(a.ravel())
    img.filepath_raw = os.path.join(out_dir, f'{asset}_AlbedoAO.png')
    img.file_format = 'PNG'
    img.save()
    return img, src


def flexsim_material(asset, albedo, src):
    mat = bpy.data.materials.new(f'{asset}_FlexSim')
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
    bsdf.inputs['Metallic'].default_value = 0.0
    bsdf.inputs['Roughness'].default_value = 0.55
    tex = nt.nodes.new('ShaderNodeTexImage'); tex.image = albedo
    nt.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
    nimg = bpy.data.images.load(os.path.join(src, f'{asset}_Normal.png'))
    nimg.colorspace_settings.name = 'Non-Color'
    ntex = nt.nodes.new('ShaderNodeTexImage'); ntex.image = nimg
    nmap = nt.nodes.new('ShaderNodeNormalMap')
    nt.links.new(ntex.outputs['Color'], nmap.inputs['Color'])
    nt.links.new(nmap.outputs['Normal'], bsdf.inputs['Normal'])
    epath = os.path.join(src, f'{asset}_Emission.png')
    if os.path.exists(epath):
        etex = nt.nodes.new('ShaderNodeTexImage'); etex.image = bpy.data.images.load(epath)
        nt.links.new(etex.outputs['Color'], bsdf.inputs['Emission Color'])
        bsdf.inputs['Emission Strength'].default_value = 1.0
    return mat


def select(objs, active):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.hide_set(False); o.hide_viewport = False; o.select_set(True)
    bpy.context.view_layer.objects.active = active


def main():
    a = args()
    bpy.ops.wm.open_mainfile(filepath=os.path.join(BUILD_DIR, f'{a.asset}.blend'))
    out = os.path.join(BUILD_DIR, 'FlexSim')
    os.makedirs(out, exist_ok=True)

    root = bpy.data.objects[a.asset]
    tree = [root] + list(root.children_recursive)
    empties = [o for o in tree if o.type == 'EMPTY']
    meshes = [o for o in tree if o.type == 'MESH' and '_LOD' in o.name]   # drops props (Pallet)

    albedo, src = albedo_with_ao(a.asset, out)
    mat = flexsim_material(a.asset, albedo, src)
    for m in meshes:
        m.data.materials.clear(); m.data.materials.append(mat)

    root.rotation_euler.z += radians(90)      # front (-Y) -> +X
    bpy.context.view_layer.update()

    levels = sorted({int(m.name.rsplit('_LOD', 1)[1]) for m in meshes})
    for lvl in levels:
        lod = [m for m in meshes if m.name.endswith(f'_LOD{lvl}')]
        tris = 0
        for m in lod:
            m.data.calc_loop_triangles(); tris += len(m.data.loop_triangles)
        select(empties + lod, root)
        base = os.path.join(out, f'{a.asset}_LOD{lvl}')
        bpy.ops.export_scene.fbx(
            filepath=base + '.fbx', use_selection=True, object_types={'EMPTY', 'MESH'},
            apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL',
            axis_forward='Y', axis_up='Z', bake_space_transform=False,
            use_mesh_modifiers=True, mesh_smooth_type='OFF', use_tspace=True,
            use_triangles=True, add_leaf_bones=False, bake_anim=False,
            path_mode='COPY', embed_textures=True, use_custom_props=False)
        if not a.no_glb:
            bpy.ops.export_scene.gltf(
                filepath=base + '.glb', export_format='GLB', use_selection=True,
                export_yup=True, export_apply=False, export_animations=False,
                export_materials='EXPORT', export_tangents=True)
        print(f'[flexsim] LOD{lvl}: {tris} tris, {len(lod)} meshes -> {base}.fbx')


main()
