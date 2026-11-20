using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The inverse commands of the SimuLean MCP: undo a connection, remove an object, rename one — so an agent
    /// that must correct its model never falls back to the generic editor tools. Each is one undo step and answers what it
    /// changed besides the object itself (references cleared, texts that still name it).
    /// </summary>
    public static class SimuLeanEditCommands
    {
        private const string Tag = "simulean";

        [CliCommand("simulean_disconnect", "Undo simulean_connect: 'to' leaves the Next Elements of 'from' (the outputs after it move " +
                                           "up one index); between two transport nodes, their track is removed. Undoable.", Tags = new[] { Tag })]
        public static object Disconnect(
            [CliArg("from", "Upstream element (name or path).", Required = true)] string from,
            [CliArg("to", "Downstream element (name or path).", Required = true)] string to)
            => AgentReply.Of(() => DisconnectOf(from, to));

        internal static object DisconnectOf(string from, string to)
        {
            GameObject fa = SimuLeanCommands.FindObject(from), tb = SimuLeanCommands.FindObject(to);
            var na = fa.GetComponent<UnityTransportNode>();
            var nb = tb.GetComponent<UnityTransportNode>();
            if (na != null && nb != null)
            {
                bool Track(UnityTransportNetwork.EdgeDefinition e) => (e.nodeA == na && e.nodeB == nb) || (e.nodeA == nb && e.nodeB == na);
                var net = UnityCompat.FindAll<UnityTransportNetwork>(true).FirstOrDefault(n => n != null && n.edges.Any(Track))
                          ?? throw new ArgumentException($"No track between '{na.name}' and '{nb.name}'.");
                Undo.RegisterCompleteObjectUndo(net, "Disconnect " + na.name);
                net.edges.RemoveAll(Track);
                EditorUtility.SetDirty(net);
                EditorSceneManager.MarkSceneDirty(net.gameObject.scene);
                return new { network = net.name, removed = na.name + " <-> " + nb.name };
            }
            var a = fa.GetComponent<SElement>() ?? throw new ArgumentException($"'{from}' is not a SimuLean element.");
            var b = tb.GetComponent<SElement>() ?? throw new ArgumentException($"'{to}' is not a SimuLean element.");
            int i = a.nextElements == null ? -1 : a.nextElements.IndexOf(b);
            if (i < 0)
                throw new ArgumentException($"'{a.name}' does not send to '{b.name}' (its next elements: " +
                                            $"{(a.nextElements == null || a.nextElements.Count == 0 ? "none" : string.Join(", ", a.nextElements.Where(n => n != null).Select(n => n.name)))}).");
            var warnings = new List<string>();
            Undo.RegisterCompleteObjectUndo(a, "Disconnect " + a.name);
            a.nextElements.RemoveAt(i);
            if (i < a.nextElements.Count)
                warnings.Add($"The outputs of '{a.name}' after '{b.name}' moved up one index: routing by index (round robin, " +
                             "probability weights, an expression's output number) now reads them one place earlier.");
            EditorUtility.SetDirty(a);
            EditorSceneManager.MarkSceneDirty(a.gameObject.scene);
            return new { from = a.name, next = a.nextElements.Where(e => e != null).Select(e => e.name).ToList(), warnings };
        }

        [CliCommand("simulean_reorder", "Put the Next Elements of an element in a new order (the order routing by index reads: round " +
                                        "robin, probability weights, an expression's output number): 'next' lists the same elements, " +
                                        "';'-separated, in their new order. Undoable.", Tags = new[] { Tag })]
        public static object Reorder(
            [CliArg("object", "The element whose Next Elements are reordered (name or path).", Required = true)] string @object,
            [CliArg("next", "Its Next Elements in their new order, e.g. 'Line B;Line A' (every one of them, each once).", Required = true)] string next)
            => AgentReply.Of(() => ReorderOf(@object, next));

        internal static object ReorderOf(string name, string next)
        {
            var go = SimuLeanCommands.FindObject(name);
            var a = go.GetComponent<SElement>() ?? throw new ArgumentException($"'{name}' is not a SimuLean element.");
            var current = (a.nextElements ?? new List<SElement>()).Where(e => e != null).ToList();
            string Now() => current.Count == 0 ? "none" : string.Join(";", current.Select(e => e.name));
            var order = new List<SElement>();
            foreach (string part in (next ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                var hit = current.Where(e => e.name == part || SimuLeanCommands.PathOf(e.transform) == part).ToList();
                if (hit.Count == 0) throw new ArgumentException($"next: '{part}' is not a Next Element of '{a.name}' (its next elements: {Now()}).");
                var e = hit.FirstOrDefault(h => !order.Contains(h))
                        ?? throw new ArgumentException($"next: '{part}' is listed twice (its next elements: {Now()}).");
                order.Add(e);
            }
            if (order.Count != current.Count)
                throw new ArgumentException($"next: give every Next Element of '{a.name}' once, in the new order — {order.Count} of {current.Count} " +
                                            $"given (its next elements: {Now()}).");
            Undo.IncrementCurrentGroup();                       // its own undo step (a simulean_apply collapses its steps)
            Undo.SetCurrentGroupName("Reorder " + a.name);
            Undo.RegisterCompleteObjectUndo(a, "Reorder " + a.name);
            a.nextElements = order;
            EditorUtility.SetDirty(a);
            EditorSceneManager.MarkSceneDirty(a.gameObject.scene);
            return new { @object = a.name, next = order.Select(e => e.name).ToList() };
        }

        [CliCommand("simulean_remove","Delete a SimuLean object (element, team, list, data table, reaction, tool…) with what it " +
                                       "contains; every reference to it in other objects (Next Elements, processBy.team, pushToList, " +
                                       "dataTable, reaction / downtime targets…) is cleared and listed, and texts that still name it " +
                                       "are warned. One undo step.", Tags = new[] { Tag })]
        public static object Remove([CliArg("object", "The object (name or path).", Required = true)] string @object,
            [CliArg("key", "Not taken: the whole object is removed. Given, nothing is removed and the answer says how to clear a field.")] string key = "")
            => AgentReply.Of(() =>
            {
                // the MCP server drops arguments a command does not declare: a 'key' meant to remove one field deleted the
                // whole object, so it is declared only to be refused
                if (!string.IsNullOrWhiteSpace(key))
                    throw new ArgumentException($"simulean_remove deletes the whole object '{@object}' and takes no key: nothing was removed. " +
                                                $"To clear '{key}', set it empty: simulean_apply operations=[{{\"op\": \"set\", \"object\": \"{@object}\", " +
                                                $"\"key\": \"{key}\", \"value\": \"\"}}] (a list: simulean_set value=[]).");
                return RemoveOf(@object);
            });

        internal static object RemoveOf(string name)
        {
            var go = SimuLeanCommands.FindObject(name);
            if (!go.GetComponents<MonoBehaviour>().Any(SimuLeanCommands.IsSimuLean))
                throw new ArgumentException($"'{go.name}' is not a SimuLean object (an organiser group, a model, a camera…): " +
                                            "simulean_remove deletes elements, teams, lists, tables and tools only.");
            var inside = new HashSet<GameObject>(go.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject));
            bool Refers(Object o) => o is GameObject g ? inside.Contains(g) : o is Component c && c != null && inside.Contains(c.gameObject);
            var contained = inside.Where(g => g != go && g.GetComponents<MonoBehaviour>().Any(SimuLeanCommands.IsSimuLean)).Select(g => g.name).ToList();
            string removed = go.name, path = SimuLeanCommands.PathOf(go.transform);

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Remove " + removed);
            var cleared = new List<string>();
            try
            {
                foreach (var mb in Others(inside))
                {
                    var so = new SerializedObject(mb);
                    var hits = new List<string>();
                    var p = so.GetIterator();
                    while (p.Next(true))
                        if (p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue != null && Refers(p.objectReferenceValue))
                            hits.Add(p.propertyPath);
                    if (hits.Count == 0) continue;
                    // the whole object recorded first: an undo then restores its references after the removed object is back
                    // (recorded by ApplyModifiedProperties, they came back as none)
                    Undo.RegisterCompleteObjectUndo(mb, "Remove " + removed);
                    // list entries removed from the last (the indexes before stay valid), single fields set to none
                    foreach (string h in hits.OrderByDescending(h => Regex.Match(h, @"\[(\d+)\]$") is Match x && x.Success ? int.Parse(x.Groups[1].Value) : -1))
                    {
                        var m = Regex.Match(h, @"^(?<list>.+)\.Array\.data\[(?<i>\d+)\]$");
                        if (m.Success)
                        {
                            var list = so.FindProperty(m.Groups["list"].Value);
                            int i = int.Parse(m.Groups["i"].Value);
                            list.GetArrayElementAtIndex(i).objectReferenceValue = null;
                            list.DeleteArrayElementAtIndex(i);
                        }
                        else so.FindProperty(h).objectReferenceValue = null;
                        cleared.Add($"{mb.gameObject.name}.{SimuLeanCommands.Friendly(h)}");
                    }
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                var warnings = Mentions(removed, inside).Select(m => $"{m} still names '{removed}' in a text: change it, or the model refers to nothing.").ToList();
                Undo.DestroyObjectImmediate(go);
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                return new { removed, path, contained, referencesCleared = cleared, warnings };
            }
            catch
            {
                TeamMemberPreviews.Revert(group);
                throw;
            }
            finally { Undo.CollapseUndoOperations(group); }
        }

        [CliCommand("simulean_move", "Move an element, a team or a tool on the floor to x / z (world, metres; its height kept) with what " +
                                     "it holds: a rack its lanes, a team its members (those with a spawn point or a home node stay there). " +
                                     "Answers where it stood and stands, its footprint, the footprints it now overlaps and, when its place " +
                                     "enters a run, the warning 'travel distances changed': an element a Straight team walks to (one " +
                                     "without a location node), a Straight team without a home node (where its members start), a " +
                                     "transport node (its tracks' lengths). Undoable.", Tags = new[] { Tag })]
        public static object Move(
            [CliArg("object", "The element, team or tool (name or path).", Required = true)] string @object,
            [CliArg("x", "World X to move it to (m).", Required = true)] float x,
            [CliArg("z", "World Z to move it to (m).", Required = true)] float z)
            => AgentReply.Of(() => MoveOf(@object, x, z));

        internal static object MoveOf(string name, float x, float z)
        {
            var go = SimuLeanCommands.FindObject(name);
            if (!go.GetComponents<MonoBehaviour>().Any(SimuLeanCommands.IsSimuLean))
                throw new ArgumentException($"'{go.name}' is not a SimuLean object (an organiser group, a model, a camera…): " +
                                            "simulean_move moves elements, teams and tools only.");
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(z) || float.IsInfinity(z))
                throw new ArgumentException($"x / z: {x} / {z} — give a place on the floor in metres.");
            // the track lengths as they were, to say how much they change
            var before = TrackLengths(go);
            var from = go.transform.position;
            Undo.IncrementCurrentGroup();                       // its own undo step (a simulean_apply collapses its steps)
            Undo.SetCurrentGroupName("Move " + go.name);
            Undo.RecordObject(go.transform, "Move " + go.name);
            go.transform.position = new Vector3(x, from.y, z);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var overlaps = ModelLayout.Overlaps(ModelLayout.Elements(), new[] { go.name });
            var warnings = new List<string>();
            var notes = new List<string>();
            MoveEffects(go, before, warnings, notes);
            return new
            {
                moved = go.name,
                from = new[] { (float)Math.Round(from.x, 2), (float)Math.Round(from.z, 2) },
                at = new[] { (float)Math.Round(x, 2), (float)Math.Round(z, 2) },
                size = ModelLayout.SizeOf(go),
                overlaps,
                note = notes.Count > 0 ? string.Join(" ", notes) : null,
                warnings,
            };
        }

        /// <summary>The length of every track with an automatic length (euclidean) that has an end among the transport nodes
        /// <paramref name="go"/> holds, keyed by the track's ends.</summary>
        private static Dictionary<string, float> TrackLengths(GameObject go)
        {
            var lengths = new Dictionary<string, float>(StringComparer.Ordinal);
            var nodes = new HashSet<UnityTransportNode>(go.GetComponentsInChildren<UnityTransportNode>(true));
            if (nodes.Count == 0) return lengths;
            foreach (var net in UnityCompat.FindAll<UnityTransportNetwork>(true))
                foreach (var e in net?.edges ?? new List<UnityTransportNetwork.EdgeDefinition>())
                    if (e?.nodeA != null && e.nodeB != null && e.manualLength < 0 && (nodes.Contains(e.nodeA) || nodes.Contains(e.nodeB)))
                        lengths[TrackName(e)] = Vector3.Distance(e.nodeA.transform.position, e.nodeB.transform.position);
            return lengths;
        }

        private static string TrackName(UnityTransportNetwork.EdgeDefinition e) => $"{e.nodeA.NodeId} <-> {e.nodeB.NodeId}";

        /// <summary>
        /// What a move changes in a run (<paramref name="warnings"/>) or does not (<paramref name="notes"/>). A place enters a
        /// run only through travel: a Straight team walks to an element's own place when the element has no location node
        /// (SceneExtractor.AddStraightTravelPoints) and starts at the team's own place when it has no home node; a transport
        /// node's tracks of automatic length measure the distance between their nodes. A location node, a home node and a
        /// track of manual length do not move with the object.
        /// </summary>
        private static void MoveEffects(GameObject go, Dictionary<string, float> before, List<string> warnings, List<string> notes)
        {
            var straight = UnityCompat.FindAll<UnityResourceTeam>()
                                      .Where(t => t != null && t.gameObject.activeInHierarchy && t.travel == TeamTravel.Straight).ToList();
            var elements = go.GetComponentsInChildren<SElement>(true);
            var walkedTo = elements.Where(e => e.location == null).Select(e => e.name).ToList();
            if (walkedTo.Count > 0 && straight.Count > 0)
                warnings.Add($"travel distances changed: {Names(walkedTo)} {(walkedTo.Count == 1 ? "has" : "have")} no location node, so a Straight " +
                             $"team walks to {(walkedTo.Count == 1 ? "its" : "their")} own place (the model's Straight teams: {Names(straight.Select(t => t.name))}) — " +
                             "its walking times, and the results, change.");
            var located = elements.Where(e => e.location != null && !e.location.transform.IsChildOf(go.transform)).ToList();
            if (located.Count > 0)
                notes.Add("Travel uses the location node, which did not move: " +
                          string.Join(", ", located.Select(e => $"'{e.name}' at '{e.location.NodeId}'")) + " (move the node with simulean_move to change distances).");
            foreach (var team in go.GetComponentsInChildren<UnityResourceTeam>(true))
            {
                if (team.travel == TeamTravel.Straight && team.homeNode == null)
                    warnings.Add($"travel distances changed: the Straight team '{team.name}' has no home node, so its members start from the team's " +
                                 "place — their first walks, and the results, change.");
                else if (team.homeNode != null && !team.homeNode.transform.IsChildOf(go.transform))
                    notes.Add($"The members of '{team.name}' start at its home node '{team.homeNode.NodeId}', which did not move.");
                else if (team.travel != TeamTravel.Straight)
                    notes.Add($"'{team.name}' travels {team.travel}: the team's own place does not change a run.");
            }
            var after = TrackLengths(go);
            var changed = after.Where(kv => before.TryGetValue(kv.Key, out float b) && Math.Abs(b - kv.Value) > 1e-3f)
                               .Select(kv => $"{kv.Key} {M(before[kv.Key])} → {M(kv.Value)} m").ToList();
            if (changed.Count > 0)
                warnings.Add($"travel distances changed: the tracks of the transport node now measure {string.Join(", ", changed)} — every travel " +
                             "over them, and the results, change.");
            if (elements.Length == 0 && go.GetComponentInChildren<UnityResourceTeam>(true) == null && go.GetComponentInChildren<UnityTransportNode>(true) == null)
                notes.Add($"'{go.name}' is a tool: its place does not change a run.");
        }

        private static string M(float metres) => metres.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        private static string Names(IEnumerable<string> names)
        {
            var list = names.ToList();
            return list.Count <= 5 ? string.Join(", ", list.Select(n => $"'{n}'")) : string.Join(", ", list.Take(5).Select(n => $"'{n}'")) + $" and {list.Count - 5} more";
        }

        [CliCommand("simulean_rename","Rename a SimuLean object (its GameObject name, and the team / list / table name it carries): the " +
                                       "new name must be free; texts that name the old one (expressions, queries, specs) are warned, not " +
                                       "changed. Undoable.", Tags = new[] { Tag })]
        public static object Rename(
            [CliArg("object", "The object (name or path).", Required = true)] string @object,
            [CliArg("newName", "The new name.", Required = true)] string newName)
            => AgentReply.Of(() => RenameOf(@object, newName));

        internal static object RenameOf(string target, string name)
        {
            var go = SimuLeanCommands.FindObject(target);
            if (!go.GetComponents<MonoBehaviour>().Any(SimuLeanCommands.IsSimuLean))
                throw new ArgumentException($"'{go.name}' is not a SimuLean object.");
            name = (name ?? "").Trim();
            if (name == go.name) throw new ArgumentException($"'{go.name}' already has that name.");
            SimuLeanCommands.EnsureFreeName(name, go);
            string old = go.name;
            var warnings = new List<string>();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.RegisterCompleteObjectUndo(go, "Rename " + old);
            go.name = name;
            // a name of its own that differs from the GameObject's would keep the old one in the model
            if (go.GetComponent<UnityResourceTeam>() is UnityResourceTeam team)
            {
                if (!string.IsNullOrEmpty(team.teamName)) { Undo.RegisterCompleteObjectUndo(team, "Rename " + old); team.teamName = name; }
                if (team.members.Count > 0)
                    warnings.Add($"Its members keep their names ({string.Join(", ", team.members.Take(3).Select(m => m.name))}{(team.members.Count > 3 ? ", …" : "")}).");
            }
            if (go.GetComponent<UnitySimList>() is UnitySimList list && !string.IsNullOrEmpty(list.listName)) { Undo.RegisterCompleteObjectUndo(list, "Rename " + old); list.listName = name; }
            if (go.GetComponent<UnityDataTable>() is UnityDataTable table && !string.IsNullOrWhiteSpace(table.tableName)) { Undo.RegisterCompleteObjectUndo(table, "Rename " + old); table.tableName = name; }
            warnings.AddRange(Mentions(old, null).Select(m => $"{m} still names '{old}' in a text: change it to '{name}'."));
            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(go.scene);
            return new { from = old, to = name, path = SimuLeanCommands.PathOf(go.transform), warnings };
        }

        // the SimuLean components of the open scenes outside the removed object
        private static IEnumerable<MonoBehaviour> Others(HashSet<GameObject> inside)
            => UnityCompat.FindAll<MonoBehaviour>(true).Where(mb => mb != null && SimuLeanCommands.IsSimuLean(mb) && mb.gameObject.scene.IsValid()
                                                                    && !EditorUtility.IsPersistent(mb) && (inside == null || !inside.Contains(mb.gameObject)));

        /// <summary>The text fields (expressions, queries, specs) that name <paramref name="name"/> as a word: "Object.field".</summary>
        private static List<string> Mentions(string name, HashSet<GameObject> inside)
        {
            var word = new Regex(@"(?<![\w])" + Regex.Escape(name) + @"(?![\w])");
            var found = new List<string>();
            foreach (var mb in Others(inside))
            {
                var p = new SerializedObject(mb).GetIterator();
                while (p.Next(true))
                    if (p.propertyType == SerializedPropertyType.String && !string.IsNullOrEmpty(p.stringValue) && word.IsMatch(p.stringValue)
                        && !(p.propertyPath == "m_Name"))
                        found.Add($"{mb.gameObject.name}.{SimuLeanCommands.Friendly(p.propertyPath)}");
            }
            return found.Distinct().ToList();
        }

    }
}
