using System.Collections.Generic;
using NUnit.Framework;
using SimuLean.Healthcare;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>The Agenda: the scene component extracts its template and the engine builds the agenda.</summary>
    public class AgendaSceneTests
    {
        [Test]
        public void AnAgendaExtractsItsTemplateAndBuildsHeadless()
        {
            var go = new GameObject("Diagnostic unit agenda");
            try
            {
                var a = go.AddComponent<UnityAgenda>();
                a.sessions = "Mon-Fri 08:30-12:30";
                a.slotMinutes = 20f;
                a.reservedPerSession = 2;
                a.overflow = AgendaOverflow.WaitReserved;
                a.noShow = "0.05";
                a.holidays = new List<string> { "2026-12-25" };
                a.cancel = "0.05";
                a.templates = new List<string> { "2026-04-01 | Mon-Fri 08:30-13:10 | 20" };

                var config = SceneExtractor.ExtractElement(a);
                Assert.AreEqual("UnityAgenda", config.Type);
                Assert.AreEqual("Mon-Fri 08:30-12:30", config.GetParameter<string>("sessions"));
                Assert.AreEqual("WaitReserved", config.GetParameter<string>("overflow"));
                CollectionAssert.AreEqual(new[] { "2026-12-25" }, config.GetParameter<List<string>>("holidays"));

                var model = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
                model.BuildModel(new SimulationConfig { Elements = { config } });
                var built = model.Find("Diagnostic unit agenda") as Agenda;
                Assert.IsNotNull(built);
                Assert.AreEqual(2, built.ReservedPerSession);
                Assert.AreEqual(AgendaOverflow.WaitReserved, built.Overflow);
                Assert.AreEqual("0.05", built.NoShow);
                Assert.AreEqual("0.05", built.Cancel);
                Assert.AreEqual(2, built.Templates.Count, "the base template and the one from April");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ItIsInTheHealthcareLibrary()
        {
            var attr = (SimuLeanElementAttribute)System.Attribute.GetCustomAttribute(typeof(UnityAgenda), typeof(SimuLeanElementAttribute));
            Assert.AreEqual(SimuLeanModules.Healthcare, attr.Module);
            CollectionAssert.Contains(SimuLeanModules.Order, SimuLeanModules.Healthcare);
        }
    }
}
