using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>The people clips: one set for every look, the controller, the driver on the people and its rule.</summary>
    public class PeopleAnimationTests
    {
        [Test]
        public void EveryClipBindsOnEveryLook()
        {
            var clips = Directory.GetFiles(PeopleAnimationBuilder.ClipFolder, "*.anim")
                .Select(p => AssetDatabase.LoadAssetAtPath<AnimationClip>(p)).ToList();
            Assert.GreaterOrEqual(clips.Count, 9);
            var catalog = AssetDatabase.LoadAssetAtPath<PeopleLooks>("Assets/SimuLean.Unity/Prefabs/ResourceMembers/PeopleLooks.asset");
            foreach (var look in catalog.looks)
                foreach (var model in new[] { look.male, look.female })
                {
                    if (model == null) continue;
                    var armature = model.GetComponentsInChildren<Transform>(true).First(t => t.name == "Root").parent;
                    foreach (var clip in clips)
                        foreach (var b in AnimationUtility.GetCurveBindings(clip))
                            Assert.IsNotNull(armature.Find(b.path), $"{model.name}: {clip.name} animates a missing bone {b.path}");
                }
        }

        [Test]
        public void TheControllerHasEveryClipAndThePeopleItsDriver()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(PeopleAnimationBuilder.ControllerPath);
            Assert.IsNotNull(controller);
            var states = controller.layers[0].stateMachine.states.Select(s => s.state.name).ToList();
            foreach (var name in new[] { PersonAnimator.Idle, PersonAnimator.Walk, PersonAnimator.CarryWalk, PersonAnimator.WorkDesk,
                                         PersonAnimator.Examine, PersonAnimator.SitIdle, PersonAnimator.LieOnBed })
                CollectionAssert.Contains(states, name);
            foreach (var path in PeopleAnimationBuilder.People)
            {
                var driver = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentInChildren<PersonAnimator>(true);
                Assert.IsNotNull(driver, path);
                Assert.AreSame(controller, driver.controller, path);
            }
            var doctor = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SimuLean.Unity/Prefabs/ResourceMembers/Doctor.prefab");
            Assert.IsNotNull(doctor.GetComponentInChildren<PersonAnimator>(true), "a role variant inherits the driver");
        }

        [Test]
        public void TheDriverPicksTheClipFromTheModel()
        {
            var P = PersonAnimator.Pose.Stand;
            Assert.AreEqual(PersonAnimator.SitIdle, PersonAnimator.ClipFor(PersonAnimator.Pose.Sit, false, false, false, null, false, false));
            Assert.AreEqual(PersonAnimator.LieOnBed, PersonAnimator.ClipFor(PersonAnimator.Pose.Lie, false, false, false, null, false, false));
            Assert.AreEqual(PersonAnimator.Walk, PersonAnimator.ClipFor(P, true, false, false, null, false, false));
            Assert.AreEqual(PersonAnimator.CarryWalk, PersonAnimator.ClipFor(P, true, true, false, null, false, false));
            Assert.AreEqual(PersonAnimator.PushWalk, PersonAnimator.ClipFor(P, true, true, true, null, false, false), "a porter pushes");
            Assert.AreEqual(PersonAnimator.HandOver, PersonAnimator.ClipFor(P, false, true, false, null, true, false));
            Assert.AreEqual(PersonAnimator.Examine, PersonAnimator.ClipFor(P, false, false, false, PersonAnimator.Work.Examine, false, false));
            Assert.AreEqual(PersonAnimator.WorkDesk, PersonAnimator.ClipFor(P, false, false, false, PersonAnimator.Work.Desk, false, false));
            Assert.AreEqual(PersonAnimator.MachineTend, PersonAnimator.ClipFor(P, false, false, false, PersonAnimator.Work.Machine, false, false));
            Assert.AreEqual(PersonAnimator.CarryIdle, PersonAnimator.ClipFor(P, false, true, false, null, false, false));
            Assert.AreEqual(PersonAnimator.Idle2, PersonAnimator.ClipFor(P, false, false, false, null, false, true));
            Assert.AreEqual(PersonAnimator.Work.Examine, PersonAnimator.WorkAt("ConsultationBox"));
            Assert.AreEqual(PersonAnimator.Work.Machine, PersonAnimator.WorkAt("CncMachine"));
            Assert.AreEqual(PersonAnimator.Work.Weld, PersonAnimator.WorkAt("WeldPositioner"));
            Assert.AreEqual(PersonAnimator.Work.Paint, PersonAnimator.WorkAt("PaintBooth"));
            Assert.AreEqual(PersonAnimator.Work.Desk, PersonAnimator.WorkAt("ReceptionDesk"));
        }
    }
}
