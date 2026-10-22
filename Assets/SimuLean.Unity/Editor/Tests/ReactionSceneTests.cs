using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SimuLean.Signals;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// A <see cref="UnityReaction"/> is extracted (its elements by id, a plain number as a catalog
    /// number, an expression as an expression), built and bound by a scene run, shown in the Play inspector panel, listed
    /// by the element inspector's section, which also creates new ones; its sentence reads as what it does.
    /// </summary>
    public class ReactionSceneTests
    {
        private static T Add<T>(GameObject root, string name) where T : Component
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform);
            return go.AddComponent<T>();
        }

        /// <summary>Src → Queue → Sink and a reaction on the queue: count its entries in a property, stop the source.</summary>
        private static (UnityReaction reaction, UnityQueue queue, UnitySource src) Build(GameObject root)
        {
            var src = Add<UnitySource>(root, "Src");
            src.mode = SourceMode.InterArrival;
            var queue = Add<UnityQueue>(root, "Queue");
            var sink = Add<UnitySink>(root, "Sink");
            src.nextElements = new List<SElement> { queue };
            queue.nextElements = new List<SElement> { sink };
            var r = Add<UnityReaction>(root, "Count entries");
            r.sources.Add(queue);
            r.condition = "element.content >= 0";
            r.actions.Add(new UnityReaction.ActionSpec { kind = ReactionActionKind.SetElementProperty, name = "seen", value = "element.seen + 1" });
            r.actions.Add(new UnityReaction.ActionSpec { kind = ReactionActionKind.Stop, target = src, duration = 0.5 });
            return (r, queue, src);
        }

        [Test]
        public void ItIsExtractedWithItsElementsAndValues()
        {
            var root = new GameObject("Model") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var (r, queue, src) = Build(root);
                var x = SceneExtractor.Extract(root);
                var rc = x.Config.Reactions.Single();
                Assert.AreEqual("Count entries", rc.Id);
                Assert.AreEqual(x.ElementIds[queue], rc.Sources.Single(), "the source by its element id");
                Assert.AreEqual("element.seen + 1", rc.Parameters["a1.expression"], "an expression stays an expression");
                Assert.AreEqual(x.ElementIds[src], rc.Parameters["a2.target"], "an action's element by its id");
                Assert.AreEqual(0.5, rc.Parameters["a2.duration"]);
                Assert.AreEqual(true, rc.Parameters["enabled"]);
                r.actions[0].value = "3";
                Assert.AreEqual(3.0, SceneExtractor.Extract(root).Config.Reactions[0].Parameters["a1.number"], "a plain number is a number (a catalog parameter)");
                CollectionAssert.AreEquivalent(new[] { "enabled", "a1.number", "a2.duration" },
                    SimuLean.Optimization.ModelParameterCatalog.ForReaction(SceneExtractor.Extract(root).Config.Reactions[0]).Select(p => p.Key).ToArray());
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ASceneRunBindsItAndThePlayPanelShowsIt()
        {
            var root = new GameObject("Model") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var (r, queue, src) = Build(root);
                var clock = new SimClock(1);
                var factory = SceneModelBuilder.Build(clock, new List<SElement>(), root);
                factory.StartModel();
                clock.AdvanceClock(30);
                Assert.IsNotNull(r.Bound, "the run's reaction is bound to the component");
                var q = queue.GetElement();
                Assert.Greater(r.Bound.Fired, 3);
                Assert.IsTrue(q.Properties.TryGetDouble("seen", out double seen) && seen == r.Bound.Fired, "the property counts the firings");
                Assert.Greater(src.GetElement().GetTimeInState(ElementState.STOPPED), 0, "the source was stopped");

                var context = new ElementInspectorContext { Clock = clock, Now = clock.GetSimulationTime() };
                var view = ElementInspectorReport.Build(q, context, false);
                int h = view.Kpis.FindIndex(k => k.Header && k.Label == "Reactions");
                Assert.GreaterOrEqual(h, 0, "the panel has a Reactions group");
                Assert.AreEqual("Count entries", view.Kpis[h + 1].Label);
                StringAssert.Contains("fired " + r.Bound.Fired, view.Kpis[h + 1].Value);
                var srcView = ElementInspectorReport.Build(src.GetElement(), context, false);
                Assert.IsTrue(srcView.Kpis.Any(k => k.Label == "Count entries"), "an element it acts on lists it too");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ClockSignalsTeamsAndLines()
        {
            var root = new GameObject("Model") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var (r, queue, src) = Build(root);
                var team = Add<UnityResourceTeam>(root, "Operators");
                var shift = Add<UnityReaction>(root, "Night shift");
                shift.signal = SignalKind.Every;
                shift.period = 3600;
                shift.sources.Add(queue);   // ignored: a clock signal has no element of its own
                shift.actions.Add(new UnityReaction.ActionSpec { kind = ReactionActionKind.Stop, onTeam = true, team = team, count = 2, state = "OFF_SHIFT" });
                var x = SceneExtractor.Extract(root);
                var rc = x.Config.Reactions.Single(c => c.Id == "Night shift");
                Assert.IsEmpty(rc.Sources, "a clock signal exports no element");
                Assert.AreEqual(3600.0, rc.Parameters["period"]);
                Assert.IsFalse(rc.Parameters.ContainsKey("first"), "no first time: one period after the start");
                Assert.AreEqual(x.TeamIds[team], rc.Parameters["a1.team"], "the team by its id");
                Assert.AreEqual(2.0, rc.Parameters["a1.count"]);
                StringAssert.StartsWith("Every 3600 → stop 2 of Operators", UnityReactionEditor.Sentence(shift));
                CollectionAssert.Contains(ReactionsSection.Of(team), shift, "the team lists it");
                CollectionAssert.DoesNotContain(ReactionsSection.Of(queue), shift, "a clock reaction's leftover element does not count");

                var lines = SceneConnections.All();
                Assert.IsTrue(lines.Any(c => c.Kind == ConnectionKind.ReactionSource && c.From == queue && c.To == r), "a line from the element to the reaction listening to it");
                Assert.IsTrue(lines.Any(c => c.Kind == ConnectionKind.ReactionTarget && c.From == r && c.To == src), "a line from the reaction to the element it acts on");
                Assert.IsTrue(lines.Any(c => c.Kind == ConnectionKind.ReactionTarget && c.From == shift && c.To == team), "and to a team");
                Assert.AreEqual(ConnectionGroup.Logic, SceneConnections.GroupOf(ConnectionKind.ReactionSource));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void TheElementSectionListsAndCreatesReactions()
        {
            var root = new GameObject("Model") { hideFlags = HideFlags.HideAndDontSave };
            GameObject created = null;
            bool organize = ModelHierarchy.AutoOrganize;
            ModelHierarchy.AutoOrganize = false;   // no model groups left in the open scene
            try
            {
                var (r, queue, src) = Build(root);
                CollectionAssert.Contains(ReactionsSection.Of(queue), r, "listens to the queue");
                CollectionAssert.Contains(ReactionsSection.Of(src), r, "acts on the source");
                var fresh = ReactionsSection.Create(queue);
                created = fresh.gameObject;
                CollectionAssert.AreEqual(new[] { queue }, fresh.sources, "a new reaction listens to the element");
                CollectionAssert.Contains(ReactionsSection.Of(queue), fresh);
                StringAssert.StartsWith("When Queue: an item enters, if element.content >= 0 → element.seen = element.seen + 1; stop Src for 0.5",
                                        UnityReactionEditor.Sentence(r));
            }
            finally
            {
                ModelHierarchy.AutoOrganize = organize;
                if (created != null) Object.DestroyImmediate(created);
                Object.DestroyImmediate(root);
            }
        }
    }
}
