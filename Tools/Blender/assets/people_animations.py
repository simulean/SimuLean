"""People animation clips.

One clip set for every person: the looks share the operator's MPFB rig and its bone contract (Torso, Leg_L/R,
Shin_L/R, Arm_L/R, Forearm_L/R, CarrySocket — README "Operator"), so the clips are keyed here on the operator rig
(operator.build_preview) and exported as the takes of one armature-only FBX:
Art/Models/Operator/People_Animations.fbx. In Unity (Generic import) the editor tool *Build People Clips* turns
its takes into .anim clips bound below each look's armature object (paths "Root/Torso/…"), and People.controller
plays them (driver: PersonAnimator).

Clips (30 fps, in place, no root motion; loops end where they start):
  Idle, Idle2 (weight shift)      everyone waiting / standing
  Walk (one 1.4 m cycle in 1 s)   walking; the driver scales it to the speed
  CarryIdle, CarryWalk            arms holding the item at CarrySocket
  WorkDesk                        standing at a desk, writing
  Examine                         leaning over a patient, hands forward
  SitIdle                         sitting (pelvis lowered to a 0.46 m seat)
  LieOnBed                        lying on the back (pelvis lowered to the bed top)
  PushWalk                        walking behind a bed / stretcher / wheelchair, hands on its handles (porters)
  HandOver                        reaching forward to give / take something (load and unload steps)
  MachineTend, Weld, Paint        working at a machine panel, welding (knees bent), spray painting

Poses are rotations about armature axes relative to the rest pose (front = -Y, lateral = X, up = Z): a negative
angle about X swings a hanging limb (arm, leg) forward, a positive one leans an upright bone (spine, neck) forward.

Run in an open Blender with MPFB (no --factory-startup):
    blender -b -P Tools/Blender/assets/people_animations.py
or from a session: exec the module, then build_clips(rig) and export(rig, path).
"""
import os
import sys
from math import sin, cos, pi, radians

import bpy
from mathutils import Quaternion, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
OUT = os.path.join(ROOT, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'Operator', 'People_Animations.fbx')
FPS = 30

# bones keyed in every clip (the others keep their rest pose)
DRIVEN = ['Torso', 'spine_01', 'spine_02', 'spine_03', 'neck_01', 'head', 'clavicle_l', 'clavicle_r',
          'Arm_L', 'Arm_R', 'Forearm_L', 'Forearm_R', 'hand_l', 'hand_r',
          'Leg_L', 'Leg_R', 'Shin_L', 'Shin_R', 'foot_l', 'foot_r']

X, Y, Z = Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1))
REST_FLEX = 45.0   # the rest pose bends the elbows 45° forward
RELAX = 30.0       # the forearms hang almost straight


def flex(degrees):
    """The forearm rotation for an elbow flexed `degrees` forward (the rest is already 45°)."""
    return REST_FLEX - degrees


def wave(t, phase=0.0, cycles=1.0):
    return sin(2 * pi * (cycles * t + phase))


# ── clips: t in [0, 1) → {bone: [(axis, degrees), …]}, {bone: Vector armature-space offset} ─────────────────

def idle(t, shift=0.0):
    b = 1.2 * wave(t, cycles=2)                       # breathing, two breaths per loop
    r = {'spine_03': [(X, -b)], 'neck_01': [(Z, 4 * wave(t, 0.1))],
         'Arm_L': [(X, 1.5 * wave(t, 0.25))], 'Arm_R': [(X, -1.5 * wave(t, 0.25))],
         'Forearm_L': [(X, RELAX)], 'Forearm_R': [(X, RELAX)]}   # the rest bends the forearms 45° forward
    if shift:                                         # weight from one foot to the other
        s = shift * wave(t)
        r['Torso'] = [(Y, s)]
        r['spine_01'] = [(Y, -0.6 * s)]
        r['Leg_L'] = [(Y, -s)]
        r['Leg_R'] = [(Y, -s)]
    return r, {}


def walk(t, carry=False):
    swing = 24.0
    r = {
        'Leg_L': [(X, -swing * wave(t))], 'Leg_R': [(X, swing * wave(t))],
        # the knee bends while the leg swings forward (and a little at contact)
        'Shin_L': [(X, 28 * max(0.0, wave(t, -0.15)) + 6)], 'Shin_R': [(X, 28 * max(0.0, wave(t, 0.35)) + 6)],
        'foot_l': [(X, -8 * wave(t, 0.1))], 'foot_r': [(X, 8 * wave(t, 0.1))],
        'Torso': [(Z, 4 * wave(t))], 'spine_02': [(Z, -5 * wave(t)), (X, 3)],
        'neck_01': [(Z, 2 * wave(t))],
    }
    if carry:
        r.update(carry_arms(t, small=True))
    else:
        r['Arm_L'] = [(X, 16 * wave(t))]
        r['Arm_R'] = [(X, -16 * wave(t))]
        r['Forearm_L'] = [(X, RELAX - 6 - 10 * max(0.0, -wave(t)))]
        r['Forearm_R'] = [(X, RELAX - 6 - 10 * max(0.0, wave(t)))]
    return r, {}


def carry_arms(t, small=False):
    j = 1.5 * wave(t, cycles=2) if small else 0.0
    return {'Arm_L': [(X, -18 + j)], 'Arm_R': [(X, -18 + j)],
            'Forearm_L': [(X, flex(72))], 'Forearm_R': [(X, flex(72))],
            'clavicle_l': [(Z, -4)], 'clavicle_r': [(Z, 4)]}


def carry_idle(t):
    r, loc = idle(t)
    r.update(carry_arms(t))
    return r, loc


def work_desk(t):
    w = wave(t, cycles=4)                             # the writing hand
    return {'spine_02': [(X, 6)], 'spine_03': [(X, 4)], 'neck_01': [(X, 14)], 'head': [(X, 8), (Z, 3 * wave(t))],
            'Arm_L': [(X, -14), (Z, -6)], 'Forearm_L': [(X, flex(78))],
            'Arm_R': [(X, -20 + 2 * w), (Z, 6)], 'Forearm_R': [(X, flex(70 + 3 * w))],
            'hand_r': [(Z, 8 * wave(t, cycles=8))]}, {}


def examine(t):
    a, b = wave(t, cycles=2), wave(t, 0.3, cycles=3)
    return {'Torso': [(X, 4)], 'spine_02': [(X, 9)], 'spine_03': [(X, 7)], 'neck_01': [(X, 16)],
            'head': [(Z, 5 * wave(t))],
            'Arm_L': [(X, -26 + 4 * a), (Z, -8)], 'Forearm_L': [(X, flex(58 + 6 * b))],
            'Arm_R': [(X, -30 - 4 * b), (Z, 8)], 'Forearm_R': [(X, flex(52 - 6 * a))]}, {}


def sit_idle(t):
    b = 1.0 * wave(t, cycles=2)
    return ({'Leg_L': [(X, -86), (Y, -4)], 'Leg_R': [(X, -86), (Y, 4)],
             'Shin_L': [(X, 88)], 'Shin_R': [(X, 88)],
             'spine_01': [(X, -4)], 'spine_03': [(X, -b)], 'neck_01': [(X, 6), (Z, 6 * wave(t, 0.2))],
             'Arm_L': [(X, -14)], 'Arm_R': [(X, -14)], 'Forearm_L': [(X, flex(52))], 'Forearm_R': [(X, flex(52))]},
            {'Torso': Vector((0, 0.06, -0.47))})


def lie_on_bed(t):
    b = 0.8 * wave(t, cycles=2)
    return ({'Torso': [(X, -90)], 'spine_03': [(X, -b)], 'neck_01': [(X, 14)],
             'Arm_L': [(X, -4), (Y, 6)], 'Arm_R': [(X, -4), (Y, -6)], 'Forearm_L': [(X, flex(2))], 'Forearm_R': [(X, flex(2))]},
            {'Torso': Vector((0, 0.15, -0.72))})   # pelvis 15 cm towards the head: the feet end on a 1.8 m couch


def push_walk(t):
    """Walking behind a bed / stretcher / wheelchair: hands on the handles at hip-chest height, leaning a little."""
    r, loc = walk(t)
    r.update({'spine_01': [(X, 5)], 'spine_02': [(Z, -2 * wave(t)), (X, 4)],
              'Arm_L': [(X, -38), (Z, -4)], 'Arm_R': [(X, -38), (Z, 4)],
              'Forearm_L': [(X, flex(40))], 'Forearm_R': [(X, flex(40))]})
    return r, loc


def hand_over(t):
    """Handing something over (or taking it): the right arm reaching forward and back once a loop."""
    k = 0.5 - 0.5 * cos(2 * pi * t)                   # 0 → 1 → 0
    r, loc = idle(t)
    r.update({'spine_02': [(X, 4 * k)], 'Arm_R': [(X, -20 - 45 * k)], 'Forearm_R': [(X, flex(60 - 45 * k))],
              'neck_01': [(X, 8)]})
    return r, loc


def machine_tend(t):
    """At a machine panel: both hands up at the controls, small presses."""
    a, b = wave(t, cycles=3), wave(t, 0.4, cycles=2)
    return {'spine_02': [(X, 4)], 'neck_01': [(X, 6), (Z, 4 * wave(t))],
            'Arm_L': [(X, -34 + 3 * b), (Z, -6)], 'Forearm_L': [(X, flex(88))],
            'Arm_R': [(X, -40 - 4 * a), (Z, 6)], 'Forearm_R': [(X, flex(84 + 6 * a))]}, {}


def weld(t):
    """Welding: knees bent, leaning over the work, the torch hand steady forward-down, the other guiding."""
    j = 1.2 * wave(t, cycles=4)
    return ({'Leg_L': [(X, -28)], 'Leg_R': [(X, -28)], 'Shin_L': [(X, 40)], 'Shin_R': [(X, 40)],
             'foot_l': [(X, -10)], 'foot_r': [(X, -10)],
             'spine_01': [(X, 10)], 'spine_02': [(X, 12)], 'neck_01': [(X, 18)],
             'Arm_R': [(X, -48 + j)], 'Forearm_R': [(X, flex(30))],
             'Arm_L': [(X, -40), (Z, -10)], 'Forearm_L': [(X, flex(50))]},
            {'Torso': Vector((0, 0.08, -0.10))})


def paint(t):
    """Spray painting: the right arm sweeping side to side at chest height, the body following a little."""
    w = wave(t, cycles=2)
    return {'Torso': [(Z, 6 * w)], 'spine_02': [(Z, 6 * w), (X, 3)],
            'Arm_R': [(X, -62), (Y, 18 * w)], 'Forearm_R': [(X, flex(20))],
            'Arm_L': [(X, -10)], 'Forearm_L': [(X, flex(30))]}, {}


CLIPS = {
    'Idle': (90, lambda t: idle(t)),
    'Idle2': (120, lambda t: idle(t, shift=3.0)),
    'Walk': (30, lambda t: walk(t)),
    'CarryIdle': (90, carry_idle),
    'CarryWalk': (30, lambda t: walk(t, carry=True)),
    'WorkDesk': (120, work_desk),
    'Examine': (120, examine),
    'SitIdle': (120, sit_idle),
    'LieOnBed': (120, lie_on_bed),
    'PushWalk': (30, push_walk),
    'HandOver': (60, hand_over),
    'MachineTend': (120, machine_tend),
    'Weld': (120, weld),
    'Paint': (60, paint),
}


# ── keying ────────────────────────────────────────────────────────────────────────────────────────────────────

def pose(rig, rotations, offsets):
    """Rotations about armature axes and armature-space offsets, both relative to the rest pose."""
    for name in DRIVEN:
        pb = rig.pose.bones.get(name)
        if pb is None:
            continue
        rest = pb.bone.matrix_local.to_quaternion()
        q = Quaternion()
        for axis, deg in rotations.get(name, []):
            q = Quaternion(axis, radians(deg)) @ q
        pb.rotation_mode = 'QUATERNION'
        pb.rotation_quaternion = rest.inverted() @ q @ rest
        off = offsets.get(name)
        pb.location = (pb.bone.matrix_local.to_3x3().inverted() @ off) if off is not None else Vector()


def build_clips(rig):
    """One action per clip (fake user, so the FBX exporter writes each as a take)."""
    scene = bpy.context.scene
    scene.render.fps = FPS
    rig.animation_data_create()
    actions = []
    for name, (frames, fn) in CLIPS.items():
        old = bpy.data.actions.get(name)
        if old is not None:
            bpy.data.actions.remove(old)
        act = bpy.data.actions.new(name)
        act.use_fake_user = True
        rig.animation_data.action = act
        for f in range(0, frames + 1, 2):
            rotations, offsets = fn((f % frames) / frames)
            pose(rig, rotations, offsets)
            for n in DRIVEN:
                pb = rig.pose.bones.get(n)
                if pb is None:
                    continue
                pb.keyframe_insert('rotation_quaternion', frame=f, group=n)
                pb.keyframe_insert('location', frame=f, group=n)
        act.frame_range = (0, frames)
        actions.append(act)
    rig.animation_data.action = None
    pose(rig, {}, {})
    print(f'[people_animations] {len(actions)} clips:', ', '.join(a.name for a in actions))
    return actions


def export(rig, path=OUT):
    """The armature with every action as a take (no meshes: the looks have their own)."""
    if bpy.context.object is not None and bpy.context.object.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={'ARMATURE'},
                             add_leaf_bones=False, bake_anim=True, bake_anim_use_all_actions=True,
                             bake_anim_use_nla_strips=False, bake_anim_force_startend_keying=True,
                             bake_anim_simplify_factor=0.0, bake_anim_step=1.0,
                             apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z',
                             axis_up='Y', primary_bone_axis='Y', secondary_bone_axis='X',
                             use_armature_deform_only=False, path_mode='STRIP')
    print(f'[people_animations] exported {path}')


def main():
    sys.path.insert(0, HERE)
    sys.path.insert(0, os.path.dirname(HERE))
    import importlib.util
    spec = importlib.util.spec_from_file_location('sl_operator', os.path.join(HERE, 'operator.py'))
    op = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(op)
    objs = op.build_preview('M', helmet=False)
    rig = next(o for o in objs if o.type == 'ARMATURE')
    build_clips(rig)
    export(rig)


if __name__ == '__main__':
    main()
