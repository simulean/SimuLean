using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Building in bulk through the MCP: an agent built 293
    /// elements by calling the commands from C# loops through <c>eval</c>; another needed 1,787 <c>simulean_set</c> calls.
    /// <c>simulean_apply</c> runs a list of edits in one call (all or nothing, one undo step), <c>simulean_repeat</c> copies
    /// a group of objects N times with its inner connections, <c>simulean_invoke</c> runs a component action marked for
    /// agents (<see cref="SimuLeanActionAttribute"/>) or a context-menu action.
    /// </summary>
    public static partial class SimuLeanCommands
    {
        [CliCommand("simulean_apply", "Run a list of edits in one call, all or nothing (one undo step; at the first error every edit " +
                                      "before it is undone and the error names the operation). JSON array of {\"op\": …}: add " +
                                      "(type name x y z parent), set (element key value), connect / disconnect (from to), remove " +
                                      "(object), rename (object name), add_team (name members travel x y z parent), invoke (object " +
                                      "action), repeat (group count offset names connectFrom connectTo).", Tags = new[] { Tag })]
        public static object Apply(
            [CliArg("operations", "JSON array, e.g. [{\"op\":\"add\",\"type\":\"UnityQueue\",\"name\":\"Q1\",\"x\":2}, " +
                                  "{\"op\":\"set\",\"element\":\"Q1\",\"key\":\"capacity\",\"value\":5}, {\"op\":\"connect\",\"from\":\"Src\",\"to\":\"Q1\"}].",
                    Required = true)] string operations)
            => AgentReply.Of(() => ApplyOf(operations));

        private static object ApplyOf(string operations)
        {
            JArray ops;
            try { ops = JArray.Parse(operations ?? ""); }
            catch (Newtonsoft.Json.JsonException e)
            { throw new ArgumentException($"operations: not a JSON array ({e.Message}). Expected e.g. [{{\"op\": \"connect\", \"from\": \"A\", \"to\": \"B\"}}]."); }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            var done = new List<object>();
            for (int i = 0; i < ops.Count; i++)
            {
                if (!(ops[i] is JObject op)) throw new ArgumentException($"operation {i + 1}: not an object.");
                string kind = Text(op, "op", i).ToLowerInvariant();
                try { done.Add(Brief(kind, RunOperation(kind, op, i))); }
                catch (Exception e)
                {
                    var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                    Undo.RevertAllDownToGroup(group);
                    throw new ArgumentException($"operation {i + 1} ({kind}{Describe(op)}): {inner.Message} — nothing was changed" +
                                                (i > 0 ? $" (the {i} operation(s) before it were undone)." : "."));
                }
            }
            Undo.CollapseUndoOperations(group);
            Undo.SetCurrentGroupName($"SimuLean: {ops.Count} edits");
            return new { applied = ops.Count, results = done };
        }

        private static object RunOperation(string kind, JObject op, int i)
        {
            switch (kind)
            {
                case "add":
                    return AddElement(Text(op, "type", i), Opt(op, "name"), Num(op, "x"), Num(op, "y"), Num(op, "z"), Opt(op, "parent"));
                case "set": return Set(Text(op, "element", i), Text(op, "key", i), Text(op, "value", i));
                case "connect": return Connect(Text(op, "from", i), Text(op, "to", i));
                case "disconnect": return SimuLeanEditCommands.Disconnect(Text(op, "from", i), Text(op, "to", i));
                case "remove": return SimuLeanEditCommands.Remove(Text(op, "object", i));
                case "rename": return SimuLeanEditCommands.Rename(Text(op, "object", i), Text(op, "name", i));
                case "add_team":
                    return AddTeam(Text(op, "name", i), op["members"] != null ? (int)op["members"] : 1, Opt(op, "travel"),
                                   Num(op, "x"), Num(op, "y"), Num(op, "z"), Opt(op, "parent"));
                case "invoke": return Invoke(Text(op, "object", i), Text(op, "action", i));
                case "repeat":
                    return Repeat(Text(op, "group", i), op["count"] != null ? (int)op["count"] : 1, Opt(op, "offset"), Opt(op, "names"),
                                  Opt(op, "connectFrom"), Opt(op, "connectTo"));
            }
            throw new ArgumentException($"'{kind}' is not add, set, connect, disconnect, remove, rename, add_team, invoke or repeat.");
        }

        // a short result per operation (the full answers would fill the agent's context)
        private static object Brief(string kind, object answer)
        {
            var o = JobWait.Json(answer) as JObject;
            if (o == null) return kind;
            var b = new JObject { ["op"] = kind };
            foreach (string k in new[] { "name", "element", "from", "to", "removed", "copies" })
                if (o[k] != null) b[k] = o[k];
            if (o["warnings"] is JArray w && w.Count > 0) b["warnings"] = w;
            return b;
        }

        private static string Describe(JObject op)
        {
            foreach (string k in new[] { "name", "element", "object", "from", "group" })
                if (op[k] != null) return " " + (string)op[k] + (k == "from" && op["to"] != null ? " → " + (string)op["to"] : "");
            return "";
        }

        private static string Text(JObject op, string key, int i)
        {
            var v = op[key];
            if (v == null || v.Type == JTokenType.Null) throw new ArgumentException($"operation {i + 1}: no \"{key}\".");
            return Value(v);
        }

        private static string Opt(JObject op, string key) => op[key] == null || op[key].Type == JTokenType.Null ? "" : Value(op[key]);

        private static float Num(JObject op, string key) => op[key] == null ? 0f : (float)op[key];

        private static string Value(JToken v)
        {
            switch (v.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float: return ((double)v).ToString("R", CultureInfo.InvariantCulture);
                case JTokenType.Boolean: return (bool)v ? "true" : "false";
                case JTokenType.String: return (string)v;
                case JTokenType.Array: return string.Join(",", v.Select(Value));
                default: return v.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        // ── repeat ───────────────────────────────────────────────────────────

        [CliCommand("simulean_repeat", "Copy a group of objects (elements, with their children) N times, each copy moved by the " +
                                       "offset and renamed by the pattern; connections inside the group point to the copy's own " +
                                       "members, connections to objects outside it are kept. connectFrom 'Outside>Member;…' connects " +
                                       "an outside element to each copy's member; connectTo 'Member>Outside;…' each copy's member to " +
                                       "an outside element. One undo step.", Tags = new[] { Tag })]
        public static object Repeat(
            [CliArg("group", "The objects to copy, 'A;B;C' (names or paths).", Required = true)] string group,
            [CliArg("count", "Number of copies.")] int count = 1,
            [CliArg("offset", "Move of each copy from the previous one, 'x,y,z' (e.g. '0,0,6').")] string offset = "",
            [CliArg("names", "Name pattern of the copies: {name} = the original's, {i} = the copy's number 1…N (default '{name}_{i}').")] string names = "",
            [CliArg("connectFrom", "Outside elements feeding each copy: 'Router>Queue;…' (Queue = the member of the group).")] string connectFrom = "",
            [CliArg("connectTo", "Each copy feeding outside elements: 'Station>Exit;…'.")] string connectTo = "")
            => AgentReply.Of(() => RepeatOf(group, count, offset, names, connectFrom, connectTo));

        private static object RepeatOf(string group, int count, string offset, string names, string connectFrom, string connectTo)
        {
            if (count < 1 || count > 500) throw new ArgumentException($"count: {count} is not between 1 and 500.");
            var originals = (group ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(n => FindObject(n.Trim())).Distinct().ToList();
            if (originals.Count == 0) throw new ArgumentException("group: name the objects to copy, 'A;B;C'.");
            if (originals.Any(a => originals.Any(b => a != b && a.transform.IsChildOf(b.transform))))
                throw new ArgumentException("group: an object is inside another of the group (its parent copies it).");
            Vector3 step = string.IsNullOrWhiteSpace(offset) ? Vector3.zero : ParseVector(offset);
            string pattern = string.IsNullOrWhiteSpace(names) ? "{name}_{i}" : names;
            if (!pattern.Contains("{i}")) throw new ArgumentException("names: the pattern needs {i}, the copy's number.");
            var from = Links(connectFrom, "connectFrom");
            var to = Links(connectTo, "connectTo");
            var memberNames = new HashSet<string>(originals.SelectMany(o => o.GetComponentsInChildren<Transform>(true)).Select(t => t.name));
            foreach (var (outside, member) in from) { if (!memberNames.Contains(member)) throw new ArgumentException($"connectFrom: '{member}' is not in the group."); FindObject(outside); }
            foreach (var (member, outside) in to) { if (!memberNames.Contains(member)) throw new ArgumentException($"connectTo: '{member}' is not in the group."); FindObject(outside); }

            Undo.IncrementCurrentGroup();
            int undo = Undo.GetCurrentGroup();
            var copies = new List<List<string>>();
            try
            {
                for (int i = 1; i <= count; i++)
                {
                    var map = new Dictionary<Object, Object>();
                    var made = new List<GameObject>();
                    foreach (var original in originals)
                    {
                        var copy = Object.Instantiate(original, original.transform.parent);
                        copy.transform.SetPositionAndRotation(original.transform.position + step * i, original.transform.rotation);
                        copy.transform.SetSiblingIndex(original.transform.GetSiblingIndex() + 1 + (i - 1) * originals.Count);
                        Undo.RegisterCreatedObjectUndo(copy, "Repeat");
                        Pair(original.transform, copy.transform, map);
                        made.Add(copy);
                    }
                    // names first (unique), then the references inside the group remapped to this copy
                    var renamed = new Dictionary<string, string>();
                    foreach (var copy in made)
                        foreach (var t in copy.GetComponentsInChildren<Transform>(true))
                        {
                            if (!IsModelObject(t.gameObject)) continue;
                            string original = ((GameObject)map.First(kv => kv.Value == t.gameObject).Key).name;
                            string n = pattern.Replace("{name}", original).Replace("{i}", i.ToString(CultureInfo.InvariantCulture));
                            EnsureFreeName(n, t.gameObject);
                            t.gameObject.name = n;
                            renamed[original] = n;
                        }
                    foreach (var copy in made)
                        foreach (var c in copy.GetComponentsInChildren<Component>(true)) Remap(c, map);
                    foreach (var (outside, member) in from) Connect(outside, renamed.TryGetValue(member, out var m1) ? m1 : member);
                    foreach (var (member, outside) in to) Connect(renamed.TryGetValue(member, out var m2) ? m2 : member, outside);
                    copies.Add(renamed.Values.ToList());
                }
            }
            catch
            {
                Undo.RevertAllDownToGroup(undo);
                throw;
            }
            Undo.CollapseUndoOperations(undo);
            Undo.SetCurrentGroupName($"SimuLean: repeat {count}");
            EditorSceneManager.MarkSceneDirty(originals[0].scene);
            return new { copies };
        }

        private static List<(string, string)> Links(string text, string what)
        {
            var list = new List<(string, string)>();
            foreach (string part in (text ?? "").Split(';'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                int gt = part.IndexOf('>');
                if (gt <= 0 || gt == part.Length - 1) throw new ArgumentException($"{what}: '{part}' is not 'A>B'.");
                list.Add((part.Substring(0, gt).Trim(), part.Substring(gt + 1).Trim()));
            }
            return list;
        }

        // the original's objects → the copy's (the same hierarchy): GameObjects and their components by position
        private static void Pair(Transform original, Transform copy, Dictionary<Object, Object> map)
        {
            map[original.gameObject] = copy.gameObject;
            var a = original.GetComponents<Component>();
            var b = copy.GetComponents<Component>();
            for (int k = 0; k < Math.Min(a.Length, b.Length); k++) if (a[k] != null) map[a[k]] = b[k];
            for (int k = 0; k < Math.Min(original.childCount, copy.childCount); k++) Pair(original.GetChild(k), copy.GetChild(k), map);
        }

        private static bool IsModelObject(GameObject go)
            => go.GetComponent<SElement>() != null || go.GetComponent<UnityResourceTeam>() != null
               || go.GetComponent<UnitySimList>() != null || go.GetComponent<UnityDataTable>() != null;

        private static void Remap(Component c, Dictionary<Object, Object> map)
        {
            if (c == null) return;
            var so = new SerializedObject(c);
            var p = so.GetIterator();
            bool changed = false;
            while (p.Next(true))
                if (p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue != null
                    && map.TryGetValue(p.objectReferenceValue, out var copy))
                {
                    p.objectReferenceValue = copy;
                    changed = true;
                }
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Vector3 ParseVector(string text)
        {
            var parts = text.Split(',').Select(x => x.Trim()).ToArray();
            if (parts.Length != 3 || !parts.All(x => float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                throw new ArgumentException($"offset: '{text}' is not 'x,y,z' (e.g. '0,0,6').");
            return new Vector3(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture),
                               float.Parse(parts[2], CultureInfo.InvariantCulture));
        }

        // ── invoke ───────────────────────────────────────────────────────────

        [CliCommand("simulean_invoke", "Run an action of an object's components: one marked for agents ([SimuLeanAction]: fill a " +
                                       "travel matrix, rebuild a floor zone, bake a walk grid…) or a context-menu action. Without " +
                                       "action, lists them. Undoable.", Tags = new[] { Tag })]
        public static object Invoke(
            [CliArg("object", "The object (name or path).", Required = true)] string @object,
            [CliArg("action", "The action's method name or menu label (empty = list them).")] string action = "")
            => AgentReply.Of(() => InvokeOf(@object, action));

        private static object InvokeOf(string objectName, string action)
        {
            var go = FindObject(objectName);
            var actions = Actions(go);
            if (string.IsNullOrWhiteSpace(action))
                return new { @object = go.name, actions = actions.Select(a => new { component = a.component.GetType().Name, action = a.name, a.description }).ToList() };
            var match = actions.Where(a => string.Equals(a.name, action.Trim(), StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(a.label, action.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count == 0)
                throw new ArgumentException($"'{go.name}' has no action '{action}'" + (actions.Count == 0 ? "." : $" (it has {string.Join(", ", actions.Select(a => a.name))})."));
            if (match.Count > 1) throw new ArgumentException($"'{action}' is an action of several components of '{go.name}'.");
            var (component, method, name, _, _) = match[0];
            Undo.RegisterFullObjectHierarchyUndo(go, "Invoke " + name);
            method.Invoke(component, null);
            EditorUtility.SetDirty(component);
            if (go.scene.IsValid()) EditorSceneManager.MarkSceneDirty(go.scene);
            return new { @object = go.name, component = component.GetType().Name, action = name, ran = true };
        }

        private static List<(Component component, MethodInfo method, string name, string label, string description)> Actions(GameObject go)
        {
            var list = new List<(Component, MethodInfo, string, string, string)>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                foreach (var m in c.GetType().GetMethods(flags))
                {
                    if (m.GetParameters().Length != 0 || m.ContainsGenericParameters) continue;
                    var mark = m.GetCustomAttribute<SimuLeanActionAttribute>();
                    var menu = m.GetCustomAttribute<ContextMenu>();
                    if (mark == null && menu == null) continue;
                    list.Add((c, m, m.Name, menu?.menuItem ?? m.Name, mark?.Description ?? menu?.menuItem ?? ""));
                }
            }
            return list;
        }
    }
}
