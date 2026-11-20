using NUnit.Framework;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>The station look: a virtual step shows a floor marker instead of a model; none shows nothing.</summary>
    public class StationLookTests
    {
        [Test]
        public void TheOwnModelFollowsTheLookAfterAReloadAndAQueueKeepsItsPadSetting()
        {
            // the "hidden by the look" flag was not saved (after a reload, back to Model left the element
            // invisible), and a queue's showFootprint and the look both set the same renderers
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var own = go.GetComponent<Renderer>();
                var sm = go.AddComponent<StationModel>();
                sm.look = StationModel.Look.Virtual;
                sm.ApplyLook();
                Assert.IsFalse(own.enabled);
                Assert.IsTrue(new UnityEditor.SerializedObject(sm).FindProperty("hidOwn").boolValue,
                              "'hidden by the look' is saved with the scene (a reload keeps it)");
                sm.look = StationModel.Look.Model;
                sm.ApplyLook();
                Assert.IsTrue(own.enabled, "Model look: shown again");

                var q = go.AddComponent<UnityQueue>();
                q.showFootprint = false;
                q.ApplyFootprintVisibility();
                Assert.IsFalse(own.enabled, "a queue without its pad hides it");
                sm.ApplyLook();
                Assert.IsFalse(own.enabled, "the look keeps the queue's choice");
                sm.look = StationModel.Look.Virtual;
                sm.ApplyLook();
                q.showFootprint = true;
                q.ApplyFootprintVisibility();
                Assert.IsFalse(own.enabled, "editing the queue does not show the pad of a virtual station");
                sm.look = StationModel.Look.Model;
                sm.ApplyLook();
                Assert.IsTrue(own.enabled, "Model look and pad on: shown");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void AVirtualStationHidesItsModelAndShowsAMarkerThatIsNeverSaved()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Home test";
            try
            {
                var own = go.GetComponent<Renderer>();
                var sm = go.AddComponent<StationModel>();
                Assert.IsTrue(own.enabled);
                Assert.IsNull(sm.Marker, "Model look: no marker");

                sm.look = StationModel.Look.Virtual;
                sm.virtualCaption = "At home";
                sm.ApplyLook();
                Assert.IsFalse(own.enabled, "the element's own model is hidden");
                Assert.IsNotNull(sm.Marker, "a marker on the floor");
                Assert.AreEqual(HideFlags.DontSave, sm.Marker.gameObject.hideFlags & HideFlags.DontSave, "the marker is never saved");
                Assert.IsNotNull(sm.Marker.Find("Caption"), "with its caption");
                Assert.IsNull(sm.Chosen, "no model while virtual");

                sm.look = StationModel.Look.None;
                sm.ApplyLook();
                Assert.IsFalse(own.enabled);
                Assert.IsNull(sm.Marker, "none: nothing drawn");

                sm.look = StationModel.Look.Model;
                sm.ApplyLook();
                Assert.IsTrue(own.enabled, "back to the model: shown again");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void TheModelLookLeavesAHiddenPadHidden()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var own = go.GetComponent<Renderer>();
                own.enabled = false;   // e.g. a queue's pad switched off
                var sm = go.AddComponent<StationModel>();
                sm.ApplyLook();
                Assert.IsFalse(own.enabled);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ThePickingStationModelGivesTheToteSPlaceAndTheBins()
        {
            // the Picking Station's own model (a pick-to-light workstation) instead of a work table
            var go = new GameObject("Pick");
            try
            {
                var ps = go.AddComponent<UnityPickingStation>();
                LibraryPresets.AddModel(go, ("PickingStation", "PickingStation/PickingStation.fbx"));
                Assert.IsNotNull(ps.itemPosition, "the container's place is bound to the model's WorkSocket");
                Assert.AreEqual("WorkSocket", ps.itemPosition.name);
                Assert.Less(ps.WorkPoint().z, 0f, "the tote's place is on the worktop, in front (-Z) of the bins");
                var bins = ps.Bins();
                Assert.AreEqual(12, bins.Count, "3 levels x 4 bins");
                foreach (var b in bins)
                {
                    Assert.Greater(b.position.z, 0f, "the bins are behind the worktop (+Z)");
                    Assert.Greater(b.position.y, ps.WorkPoint().y, "and above it");
                }
                Assert.Less(bins[0].position.x, bins[3].position.x, "left to right as seen from the front");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
