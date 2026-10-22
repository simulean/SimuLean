using SimuLean.Resources;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Inspector for <see cref="UnityResourceTeam"/>: members (with an "add N" button), the request and resource
    /// orders, and in Play mode each member's state, utilisation and tasks done (read-only).
    /// </summary>
    [CustomEditor(typeof(UnityResourceTeam))]
    public class UnityResourceTeamEditor : UnityEditor.Editor
    {
        private int addCount = 1;

        // The inspector standard's header (review2 #41): the team's name, kind and members, the member's look as its icon;
        // one name — the GameObject's — and, for a team that still has its own Team Name, the choice to keep one of them.
        private void DrawHeader(UnityResourceTeam team)
        {
            Texture icon = null;
            if (team.memberPrefab != null)
            {
                icon = AssetPreview.GetAssetPreview(team.memberPrefab);
                if (icon == null && AssetPreview.IsLoadingAssetPreview(UnityCompat.Entity(team.memberPrefab))) Repaint();
            }
            if (icon == null) icon = AssetPreview.GetMiniThumbnail(team);
            string kind = team.travel == TeamTravel.Abstract ? "no travel" : $"{team.travel} travel";
            SimuLeanGUI.Header(team.TeamName, $"Resource Team · {team.members.Count} member(s) · {kind}", null, Color.gray, icon);
            EditorGUILayout.LabelField("Operators, vehicles, cranes or robots that stations call for their process (Process) or to " +
                                       "carry their items (Transport).", EditorStyles.wordWrappedMiniLabel);
            var teamName = serializedObject.FindProperty("teamName");
            if (!string.IsNullOrEmpty(teamName.stringValue) && teamName.stringValue != team.gameObject.name)
            {
                EditorGUILayout.HelpBox($"The results call this team '{teamName.stringValue}' (its Team Name) while the GameObject is " +
                                        $"'{team.gameObject.name}'. Keep one name:", MessageType.Warning);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button($"Rename the GameObject '{teamName.stringValue}'"))
                    {
                        Undo.RecordObject(team.gameObject, "Rename team");
                        team.gameObject.name = teamName.stringValue;
                        teamName.stringValue = "";
                    }
                    if (GUILayout.Button($"Use '{team.gameObject.name}'")) teamName.stringValue = "";
                }
            }
            if (team.members.Count == 0)
                EditorGUILayout.HelpBox("No members: add some below, or no request can be served.", MessageType.Warning);
            ReactionsSection.DrawFor(team);   // Reactions that send members off / call them back
            EditorGUILayout.Space(4);
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var team = (UnityResourceTeam)target;
            DrawHeader(team);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("members"), true);
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            using (new EditorGUILayout.HorizontalScope())
            {
                addCount = Mathf.Max(1, EditorGUILayout.IntField("Add members", addCount));
                if (GUILayout.Button("Add", GUILayout.Width(60)))
                {
                    serializedObject.ApplyModifiedProperties();
                    Undo.RecordObject(team, "Add team members");
                    team.AddMembers(addCount);
                    EditorUtility.SetDirty(team);
                    serializedObject.Update();
                }
            }
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("requestOrder"),
                new GUIContent("Request Order", "Which request is served first (empty = first come, first served)."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("resourceOrder"),
                new GUIContent("Resource Order", "Which free member takes a request (empty = longest idle first; distance = the nearest)."));
            EditorGUILayout.Space();
            var unitsProp = serializedObject.FindProperty("units");
            EditorGUILayout.PropertyField(unitsProp);
            bool physical = unitsProp.enumValueIndex == (int)SimuLean.Serialization.ResourceUnits.Physical;
            EditorGUILayout.LabelField(" ", UnitsNote(physical), EditorStyles.miniLabel);
            var travel = serializedObject.FindProperty("travel");
            EditorGUILayout.PropertyField(travel);
            if (travel.enumValueIndex != (int)TeamTravel.Abstract)
            {
                EditorGUI.indentLevel++;
                if (travel.enumValueIndex == (int)TeamTravel.Network)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("network"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("traffic"));
                }
                if (travel.enumValueIndex == (int)TeamTravel.Matrix) EditorGUILayout.PropertyField(serializedObject.FindProperty("matrix"));
                if (travel.enumValueIndex == (int)TeamTravel.Grid) EditorGUILayout.PropertyField(serializedObject.FindProperty("walkGrid"));
                if (travel.enumValueIndex == (int)TeamTravel.UnityPhysics)
                {
                    var est = serializedObject.FindProperty("physicsEstimate");
                    EditorGUILayout.PropertyField(est, new GUIContent("Estimate (distances)"));
                    string field = est.enumValueIndex == (int)PhysicsEstimate.Matrix ? "matrix" : est.enumValueIndex == (int)PhysicsEstimate.Network ? "network" : "walkGrid";
                    EditorGUILayout.PropertyField(serializedObject.FindProperty(field));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("calibration"));
                    var cal = serializedObject.FindProperty("calibration").objectReferenceValue as TravelCalibrationAsset;
                    EditorGUILayout.HelpBox(cal == null || cal.IsEmpty
                        ? "Unity decides the travel times (NavMeshAgent or the member prefab's IPhysicsMover). Scene only, paced clock; " +
                          "calibrate it (Travel Calibration tool) to run it headless."
                        : $"Calibrated: {cal.samples} moves, R² {cal.r2:F2} ({cal.source}).", MessageType.Info);
                }
                if (travel.enumValueIndex == (int)TeamTravel.Axes)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("axisSpeed"), new GUIContent($"Axis Speed ({(physical ? "m/s" : "dist/unit")})"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("axisAccel"), new GUIContent($"Axis Accel ({(physical ? "m/s²" : "dist/unit²")})"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("axesSequential"));
                }
                EditorGUILayout.PropertyField(serializedObject.FindProperty("homeNode"));
                if (travel.enumValueIndex != (int)TeamTravel.Axes)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("speed"), new GUIContent($"Speed ({(physical ? "m/s" : "dist/unit")})"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("memberPrefab"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("craneRigs"), true);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("carriedItemOffset"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("showMembers"));
                if (serializedObject.FindProperty("homeNode").objectReferenceValue == null)
                    EditorGUILayout.HelpBox("Spatial travel needs a home node.", MessageType.Warning);
                EditorGUI.indentLevel--;
            }
            serializedObject.ApplyModifiedProperties();

            if (members(team) == 0)
                EditorGUILayout.HelpBox("The team has no members: its requests will never be served.", MessageType.Warning);
            if (!Application.isPlaying || team.Dispatcher == null) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live", EditorStyles.boldLabel);
            var d = team.Dispatcher;
            EditorGUILayout.LabelField($"Served {d.Served}  |  waiting requests {d.Requests.Count}");
            if (team.travel == TeamTravel.Network && team.traffic && team.Network != null)
            {
                var tc = team.Network.Traffic;
                EditorGUILayout.LabelField($"Traffic: {tc.Waits} waits ({tc.TotalWait:F1} waited), {tc.DeadlocksResolved} deadlocks resolved");
            }
            foreach (TaskExecutor ex in team.Executors)
                EditorGUILayout.LabelField(ex.GetName(),
                    $"{ex.CurrentState.Name,-18} util {ex.Utilization:P0}  tasks {ex.TasksDone}" +
                    (ex.Location != null ? $"  at {ex.Location}" : ""));
        }

        private static int members(UnityResourceTeam t) => t.members?.Count ?? 0;

        // What the numbers mean with the scene clock's time unit.
        private static string UnitsNote(bool physical)
        {
            var clock = UnityCompat.FindAny<UnitySimClock>();
            double unit = clock != null && clock.TimeUnitSeconds > 0 ? clock.TimeUnitSeconds : 1.0;
            string u = unit == 1 ? "1 s" : unit % 3600 == 0 ? $"{unit / 3600:0.##} h" : unit % 60 == 0 ? $"{unit / 60:0.##} min" : $"{unit:0.###} s";
            return physical
                ? $"speeds in m/s, load / unload in s — converted with the time unit ({u})"
                : $"speeds per time unit, load / unload in time units (1 unit = {u})";
        }
    }
}
