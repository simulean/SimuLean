using System.Collections.Generic;
using SimuLean.Collections;
using SimuLean.Serialization;

namespace SimuLean.Unity
{
    /// <summary>
    /// A resource team's config for the engine (<see cref="ResourceTeamConfig"/>): the scene's team read as the headless
    /// model needs it. The travel decisions are <see cref="ExtractionRules"/>' (tested headless); this class reads the
    /// component and the scene objects it points at.
    /// </summary>
    public static class UnityResourceTeamExtractor
    {
        /// <summary>The team for the headless model (<paramref name="id"/> = unique id the elements refer to).</summary>
        /// <param name="sceneRun">True for a run in the scene: Unity physics times the moves (external travel); false
        /// (headless): the calibration of the measured times.</param>
        public static ResourceTeamConfig Extract(UnityResourceTeam team, string id, string matrixId = null, string gridId = null, bool sceneRun = false)
        {
            var travel = team.travel;
            var tc = new ResourceTeamConfig
            {
                Id = id, Name = team.TeamName,
                RequestOrder = !string.IsNullOrWhiteSpace(team.requestOrderName) ? team.requestOrderName.Trim() : OrderSpec(team.requestOrder),
                ResourceOrder = OrderSpec(team.resourceOrder),
                NamedOrders = NamedOrders(team.namedOrders),
                Rule = RuleParameterValue.Merge(null, team.ruleParameters),
                Travel = ExtractionRules.EngineTravel(travel), Speed = team.speed,
                HomeNode = ExtractionRules.HomeNode(travel, team.homeNode != null ? team.homeNode.NodeId : null, id),
                ReturnHome = travel != TeamTravel.Abstract && team.returnHome,
                Matrix = ExtractionRules.UsesMatrix(travel, team.physicsEstimate) ? matrixId : null,
                WalkGrid = ExtractionRules.UsesWalkGrid(travel, team.physicsEstimate) ? gridId : null,
                Traffic = travel == TeamTravel.Network && team.traffic,
                AxisSpeed = new double[] { team.axisSpeed.x, team.axisSpeed.y, team.axisSpeed.z },
                AxisAccel = new double[] { team.axisAccel.x, team.axisAccel.y, team.axisAccel.z },
                AxesSequential = team.axesSequential,
                Units = team.units,
                JobOnMemberStop = team.jobOnMemberStop,
            };
            if (travel == TeamTravel.UnityPhysics && sceneRun)
            {
                tc.Travel = TravelKind.External;
                tc.CalibrationBase = ExtractionRules.EstimateTravel(team.physicsEstimate);
            }
            else if (travel == TeamTravel.UnityPhysics)
            {
                // Unity decides the times: headless runs need the measured times, fitted.
                if (team.calibration == null || team.calibration.IsEmpty)
                    throw new SceneNotHeadlessException(
                        $"Resource team '{team.TeamName}' moves with Unity physics and has no calibration: it cannot run headless. " +
                        "Run the scene, then use Travel Calibration ▸ Fit from this run.");
                team.calibration.ApplyTo(tc, ExtractionRules.EstimateTravel(team.physicsEstimate));
            }
            for (int i = 0; i < team.members.Count; i++)
            {
                var m = team.members[i];
                var mc = new ResourceMemberConfig
                {
                    Name = ExtractionRules.MemberName(m?.name, team.TeamName, i),
                    LoadTime = m?.loadTime ?? 0, UnloadTime = m?.unloadTime ?? 0,
                };
                if (m?.properties != null)   // name = expression records (LabelAssignments)
                    foreach (var l in m.properties)
                        if (!string.IsNullOrWhiteSpace(l)) mc.Properties.Add(l.Trim());
                tc.Members.Add(mc);
            }
            return tc;
        }

        private static List<NamedOrderSettings> NamedOrders(List<NamedOrder> namedOrders)
        {
            if (namedOrders == null || namedOrders.Count == 0) return null;
            var list = new List<NamedOrderSettings>();
            foreach (var o in namedOrders)
                if (o != null && !string.IsNullOrWhiteSpace(o.name)) list.Add(NamedOrderSettings.Of(o.name.Trim(), (o.query ?? "").Trim()));
            return list;
        }

        // Only the order part is used (filters make no sense for a team's order).
        private static string OrderSpec(ListQuerySpec spec)
            => spec == null || string.IsNullOrEmpty(spec.orderBy) ? "" : new ListQuerySpec { orderBy = spec.orderBy, descending = spec.descending }.Encode();
    }
}
