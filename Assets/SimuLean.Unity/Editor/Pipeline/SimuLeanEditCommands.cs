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

        private static object DisconnectOf(string from, string to)
        {
            GameObject fa = SimuLeanCommands.FindObject(from), tb = SimuLeanCommands.FindObject(to);
            var na = fa.GetComponent<UnityTransportNode>();
            var nb = tb.GetComponent<UnityTransportNode>();
            if (na != null && nb != null)
            {
                bool Track(UnityTransportNetwork.EdgeDefinition e) => (e.nodeA == na && e.nodeB == nb) || (e.nodeA == nb && e.nodeB == na);
                var net = UnityCompat.FindAll<UnityTransportNetwork>(true).FirstOrDefault(n => n != null && n.edges.Any(Track))
                          ?? throw new ArgumentException($"No track between '{na.name}' and '{nb.name}'.");
                Undo.RecordObject(net, "Disconnect " + na.name);
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
            Undo.RecordObject(a, "Disconnect " + a.name);
            a.nextElements.RemoveAt(i);
            if (i < a.nextElements.Count)
                warnings.Add($"The outputs of '{a.name}' after '{b.name}' moved up one index: routing by index (round robin, " +
                             "probability weights, an expression's output number) now reads them one place earlier.");
            EditorUtility.SetDirty(a);
            EditorSceneManager.MarkSceneDirty(a.gameObject.scene);
            return new { from = a.name, next = a.nextElements.Where(e => e != null).Select(e => e.name).ToList(), warnings };
        }

        [CliCommand("simulean_remove", "Delete a SimuLean object (element, team, list, data table, reaction, tool…) with what it " +
                                       "contains; every reference to it in other objects (Next Elements, processBy.team, pushToList, " +
                                       "dataTable, reaction / downtime targets…) is cleared and listed, and texts that still name it " +
                                       "are warned. One undo step.", Tags = new[] { Tag })]
        public static object Remove([CliArg("object", "The object (name or path).", Required = true)] string @object)
            => AgentReply.Of(() => RemoveOf(@object));

        private static object RemoveOf(string name)
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
                        cleared.Add($"{mb.gameObject.name}.{Friendly(h)}");
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
                Undo.RevertAllDownToGroup(group);
                throw;
            }
            finally { Undo.CollapseUndoOperations(group); }
        }

        [CliCommand("simulean_rename", "Rename a SimuLean object (its GameObject name, and the team / list / table name it carries): the " +
                                       "new name must be free; texts that name the old one (expressions, queries, specs) are warned, not " +
                                       "changed. Undoable.", Tags = new[] { Tag })]
        public static object Rename(
            [CliArg("object", "The object (name or path).", Required = true)] string @object,
            [CliArg("name", "The new name.", Required = true)] string name)
            => AgentReply.Of(() => RenameOf(@object, name));

        private static object RenameOf(string target, string name)
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
            Undo.RecordObject(go, "Rename " + old);
            go.name = name;
            // a name of its own that differs from the GameObject's would keep the old one in the model
            if (go.GetComponent<UnityResourceTeam>() is UnityResourceTeam team)
            {
                if (!string.IsNullOrEmpty(team.teamName)) { Undo.RecordObject(team, "Rename " + old); team.teamName = name; }
                if (team.members.Count > 0)
                    warnings.Add($"Its members keep their names ({string.Join(", ", team.members.Take(3).Select(m => m.name))}{(team.members.Count > 3 ? ", …" : "")}).");
            }
            if (go.GetComponent<UnitySimList>() is UnitySimList list && !string.IsNullOrEmpty(list.listName)) { Undo.RecordObject(list, "Rename " + old); list.listName = name; }
            if (go.GetComponent<UnityDataTable>() is UnityDataTable table && !string.IsNullOrWhiteSpace(table.tableName)) { Undo.RecordObject(table, "Rename " + old); table.tableName = name; }
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
                        found.Add($"{mb.gameObject.name}.{Friendly(p.propertyPath)}");
            }
            return found.Distinct().ToList();
        }

        // "nextElements.Array.data[2]" → "nextElements[2]"
        private static string Friendly(string path) => path.Replace(".Array.data[", "[");
    }
}
