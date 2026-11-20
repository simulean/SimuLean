using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Shows a resource team's members while the model is edited (a team placed from the Library showed
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
        // what each team's preview shows, and the preview object it was built in: a preview restored by an undo / redo or a
        // scene reload is another object, so it is rebuilt even when the signature is the same (a member drawn
        // at the team, not at its home node)
        private static readonly Dictionary<UnityResourceTeam, (string sig, GameObject holder)> shown = new Dictionary<UnityResourceTeam, (string, GameObject)>();
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

        /// <summary>Brings every team's preview up to date now (<c>simulean_capture</c>: a scene opened in the same editor tick
        /// has none yet, its hierarchy events come later).</summary>
        public static void EnsureAll() => RefreshNow();

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
            sb.Append(team.showMembers).Append('|').Append(team.travel).Append('|').Append(team.memberPrefab != null ? UnityCompat.ObjectId(team.memberPrefab) : "-")
              .Append('|').Append(team.members.Count).Append('|').Append(team.craneRigs != null ? team.craneRigs.Count : 0);
            for (int i = 0; i < team.members.Count; i++)
            {
                var p = PointOf(team, i);
                sb.Append('|').Append(p == team.transform ? "team" : UnityCompat.ObjectId(p)).Append('@').Append(p.position);
            }
            // a crane is fitted around the points it works at: moving one of them changes the preview
            if (FittedCrane(team))
                foreach (var r in team.ReachPoints()) sb.Append('|').Append(r);
            return sb.ToString();
        }

        // the members are cranes the run fits around the team's points (a team that moves its members in the engine: not
        // Abstract, whose members only stand, nor UnityPhysics, whose members drive themselves)
        private static bool FittedCrane(UnityResourceTeam team) =>
            team.travel != TeamTravel.Abstract && team.travel != TeamTravel.UnityPhysics && UnityResourceTeam.IsCraneMember(team.memberPrefab);

        private static void Ensure(UnityResourceTeam team)
        {
            var holder = team.transform.Find(Child);
            string sig = Signature(team);
            if (holder != null && shown.TryGetValue(team, out var old) && old.sig == sig && old.holder == holder.gameObject) return;
            if (holder != null) Object.DestroyImmediate(holder.gameObject);
            shown[team] = (sig, null);
            // a gantry already in the scene stands for its member; nothing to show without members
            int n = team.members.Count;
            if (!team.showMembers || n == 0) return;
            // as the run: an abstract team without a look shows nothing (a token pool, the milk run's drivers), not boxes
            if (team.memberPrefab == null && team.travel == TeamTravel.Abstract) return;

            var root = new GameObject(Child) { hideFlags = HideFlags.DontSave | HideFlags.NotEditable };
            root.transform.SetParent(team.transform, false);
            shown[team] = (sig, root);
            int shared = 0;
            var common = PointOf(team, -1);
            for (int i = 0; i < n; i++) if (PointOf(team, i) == common) shared++;
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                if (team.craneRigs != null && i < team.craneRigs.Count && team.craneRigs[i] != null) continue;
                var spawn = PointOf(team, i);
                Vector3 position = spawn.position;
                if (spawn == common) position += team.transform.right * ((k++ - (shared - 1) * 0.5f) * Spacing);
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
                // a crane stands as in the run: turned, centred and sized around the points it works at
                if (FittedCrane(team) && !StackerCraneView.IsStackerCrane(go.transform))
                    UnityResourceTeam.CraneRigOf(go)?.FitAround(team.ReachPoints(), 1f);
                go.name = $"{team.TeamName}_{i + 1} (preview)";
                go.transform.SetParent(root.transform, true);
                var look = go.GetComponentInChildren<OperatorAppearance>(true);
                if (look != null) look.ForMember(i);
                foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
            }
        }

        /// <summary>Where member <paramref name="i"/> stands before a run: its own spawn point, else the team's home node when its
        /// travel starts the members there (Network, Straight…: a Network forklift was drawn at the team object,
        /// not at its dock), else the team; <paramref name="i"/> = −1: the point the members without their own share.</summary>
        private static Transform PointOf(UnityResourceTeam team, int i)
        {
            var own = i >= 0 ? team.SpawnPointOf(i) : team.transform;
            if (own != team.transform) return own;
            return team.homeNode != null && team.travel != TeamTravel.Abstract ? team.homeNode.transform : team.transform;
        }

        /// <summary>
        /// <see cref="Undo.RevertAllDownToGroup"/> with the previews out of the way: a preview is built outside the
        /// undo system, so a team created and recorded (<c>RegisterCompleteObjectUndo</c>) in the reverted groups was destroyed
        /// with a child the undo did not know — "Child GameObject 'MembersPreview' … became dangling" in the answer of a refused
        /// <c>simulean_apply</c>. The previews are removed first and rebuilt for the teams that remain.
        /// </summary>
        public static void Revert(int group)
        {
            RemoveAll();
            Undo.RevertAllDownToGroup(group);
            RefreshNow();
        }

        private static void RemoveAll()
        {
            foreach (var team in UnityCompat.FindAll<UnityResourceTeam>(true))
            {
                var holder = team != null ? team.transform.Find(Child) : null;
                if (holder != null) Object.DestroyImmediate(holder.gameObject);
            }
            shown.Clear();
        }
    }
}
