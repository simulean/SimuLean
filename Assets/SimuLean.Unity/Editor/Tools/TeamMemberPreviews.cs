using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Shows a resource team's members while the model is edited (review2 #48: a team placed from the Library showed
    /// nothing until Play, its member views being created at run start): one member prefab (else a box) per member at
    /// its spawn point — side by side along the team's right when they share it — in a child that is never saved
    /// (<c>DontSave</c>) and is removed in Play, where the run's member views take over. Clicking one selects the team
    /// (<see cref="UnityResourceTeam"/> is a selection base). View only: nothing in the model changes.
    /// </summary>
    [InitializeOnLoad]
    internal static class TeamMemberPreviews
    {
        public const string Child = "MembersPreview";
        private const float Spacing = 1.4f;
        private static readonly Dictionary<UnityResourceTeam, string> shown = new Dictionary<UnityResourceTeam, string>();
        private static bool scheduled;

        static TeamMemberPreviews()
        {
            // refreshed at once when the scene changes (a delayCall alone never runs while the editor is in the background,
            // e.g. driven by the MCP — as ConnectionCleaner), the delayCall kept for the first load
            EditorApplication.hierarchyChanged += RefreshNow;
            ObjectChangeEvents.changesPublished += (ref ObjectChangeEventStream stream) => RefreshNow();
            Undo.undoRedoPerformed += RefreshNow;
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.ExitingEditMode) RemoveAll();
                else if (s == PlayModeStateChange.EnteredEditMode) { shown.Clear(); Schedule(); }
            };
            Schedule();
        }

        /// <summary>Refreshes the previews on the next editor update (a team or its members changed).</summary>
        public static void Schedule()
        {
            if (scheduled) return;
            scheduled = true;
            EditorApplication.delayCall += Refresh;
        }

        private static bool refreshing;

        private static void RefreshNow()
        {
            if (refreshing) return;   // its own changes raise the events again
            refreshing = true;
            try { Refresh(); }
            finally { refreshing = false; }
        }

        private static void Refresh()
        {
            scheduled = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            foreach (var team in UnityCompat.FindAll<UnityResourceTeam>(false))
                if (team != null) Ensure(team);
        }

        // what the preview shows: rebuilt only when it changes (members, prefab, spawn points, the switch)
        private static string Signature(UnityResourceTeam team)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(team.showMembers).Append('|').Append(team.memberPrefab != null ? UnityCompat.ObjectId(team.memberPrefab) : "-")
              .Append('|').Append(team.members.Count).Append('|').Append(team.craneRigs != null ? team.craneRigs.Count : 0);
            for (int i = 0; i < team.members.Count; i++)
            {
                var p = team.SpawnPointOf(i);
                sb.Append('|').Append(p == team.transform ? "team" : UnityCompat.ObjectId(p));
            }
            return sb.ToString();
        }

        private static void Ensure(UnityResourceTeam team)
        {
            var holder = team.transform.Find(Child);
            string sig = Signature(team);
            if (holder != null && shown.TryGetValue(team, out string old) && old == sig) return;
            if (holder != null) Object.DestroyImmediate(holder.gameObject);
            shown[team] = sig;
            // a gantry already in the scene stands for its member; nothing to show without members
            int n = team.members.Count;
            if (!team.showMembers || n == 0) return;

            var root = new GameObject(Child) { hideFlags = HideFlags.DontSave | HideFlags.NotEditable };
            root.transform.SetParent(team.transform, false);
            int shared = 0;
            for (int i = 0; i < n; i++) if (team.SpawnPointOf(i) == team.transform) shared++;
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                if (team.craneRigs != null && i < team.craneRigs.Count && team.craneRigs[i] != null) continue;
                var spawn = team.SpawnPointOf(i);
                Vector3 position = spawn.position;
                if (spawn == team.transform) position += team.transform.right * ((k++ - (shared - 1) * 0.5f) * Spacing);
                GameObject go;
                if (team.memberPrefab != null) go = Object.Instantiate(team.memberPrefab, position, spawn.rotation);
                else
                {
                    go = new GameObject();
                    go.transform.SetPositionAndRotation(position, spawn.rotation);
                    var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    box.transform.SetParent(go.transform, false);
                    box.transform.localPosition = new Vector3(0f, 0.25f, 0f);
                    box.transform.localScale = new Vector3(0.8f, 0.5f, 1.2f);
                }
                go.name = $"{team.TeamName}_{i + 1} (preview)";
                go.transform.SetParent(root.transform, true);
                var look = go.GetComponentInChildren<OperatorAppearance>(true);
                if (look != null) look.ForMember(i);
                foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
            }
        }

        private static void RemoveAll()
        {
            foreach (var team in UnityCompat.FindAll<UnityResourceTeam>(false))
            {
                var holder = team != null ? team.transform.Find(Child) : null;
                if (holder != null) Object.DestroyImmediate(holder.gameObject);
            }
            shown.Clear();
        }
    }
}
