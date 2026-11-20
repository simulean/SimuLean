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
    /// Building in bulk through the MCP, without <c>eval</c> loops or one <c>simulean_set</c> call per field.
    /// <c>simulean_apply</c> runs a list of edits in one call (all or nothing, one undo step), <c>simulean_repeat</c> copies
    /// a group of objects N times with its inner connections, <c>simulean_invoke</c> runs a component action marked for
    /// agents (<see cref="SimuLeanActionAttribute"/>) or a context-menu action.
    /// </summary>
    public static partial class SimuLeanCommands
    {
        [CliCommand("simulean_apply", "Run a list of edits in one call, all or nothing (one undo step). Every operation is tried: on " +
                                      "errors nothing is changed and the answer names every bad operation (a later one may fail because " +
                                      "of an earlier one). validateOnly=true checks the list the same way and changes nothing. JSON array " +
                                      "of {\"op\": …}: add (type name x y z parent), set (object key value), connect / disconnect (from to), " +
                                      "reorder (object next), remove (object), rename (object newName), move (object x z: as simulean_move), " +
                                      "add_team (name members travel kind x y z parent; without x / y / z it is placed once the whole list is " +
                                      "applied, in front of the model, so it does not stand on an element the list adds later), invoke (object " +
                                      "action; not run by validateOnly), repeat (group count offset names connectFrom connectTo).", Tags = new[] { Tag })]
        public static object Apply(
            [CliArg("operations", "JSON array, e.g. [{\"op\":\"add\",\"type\":\"UnityQueue\",\"name\":\"Q1\",\"x\":2}, " +
                                  "{\"op\":\"set\",\"object\":\"Q1\",\"key\":\"capacity\",\"value\":5}, {\"op\":\"connect\",\"from\":\"Src\",\"to\":\"Q1\"}].",
                    Required = true)] string operations,
            [CliArg("validateOnly", "Check every operation (each one tried, then all undone) and report all the bad ones; the scene is not changed.")] bool validateOnly = false,
            [CliArg("detail", "summary (default: the count per kind of operation, the objects created — their names as placed —, the racks rebuilt and every warning) or full (one result per operation).")] string detail = "summary")
            => AgentReply.Of(() => ApplyOf(operations, validateOnly, detail));

        private static object ApplyOf(string operations, bool validateOnly = false, string detail = "summary")
        {
            if (!string.IsNullOrWhiteSpace(detail) && !string.Equals(detail.Trim(), "full", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(detail.Trim(), "summary", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"detail: '{detail}' is not summary or full.");
            JArray ops;
            try { ops = JArray.Parse(operations ?? ""); }
            catch (Newtonsoft.Json.JsonException e)
            { throw new ArgumentException($"operations: not a JSON array ({e.Message}). Expected e.g. [{{\"op\": \"connect\", \"from\": \"A\", \"to\": \"B\"}}]."); }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            var done = new List<JObject>();
            var failures = new List<string>();
            var session = new EditSession();
            // the names this list creates, and where: a set naming one of them before it is created fails with "No object"
            var createdAt = new Dictionary<string, int>(StringComparer.Ordinal);
            // the teams added without a position: placed once the whole list stands (a team added first took a spot an
            // element of the list was given later)
            var teamsToPlace = new List<string>();
            for (int k = 0; k < ops.Count; k++)
                if (ops[k] is JObject c && ((string)c["op"] ?? "").ToLowerInvariant() is string ck
                    && (ck == "add" || ck == "add_team" || ck == "rename") && (string)(ck == "rename" ? c["newName"] : c["name"]) is string cn
                    && !createdAt.ContainsKey(cn))
                    createdAt[cn] = k;
            // every operation is tried (one bad operation does not make the caller resend the whole list)
            for (int i = 0; i < ops.Count; i++)
            {
                string kind = "?";
                try
                {
                    if (!(ops[i] is JObject op)) throw new ArgumentException("not an object.");
                    kind = Text(op, "op", i).ToLowerInvariant();
                    if (validateOnly && kind == "invoke")
                    {
                        FindObject(Text(op, "object", i)); Text(op, "action", i);
                        done.Add(new JObject { ["op"] = kind, ["note"] = "not run (validateOnly)" });
                        continue;
                    }
                    var result = RunOperation(kind, op, i, session);
                    done.Add(Brief(kind, result));
                    if (kind == "set") session.Edited(FindObject(Text(op, "object", i)).name);
                    if (kind == "add_team" && result["placedAt"] != null && result["placedAt"].Type == JTokenType.String && result["team"] != null)
                        teamsToPlace.Add((string)result["team"]);
                }
                catch (Exception e)
                {
                    var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                    failures.Add($"operation {i + 1} ({kind}{(ops[i] is JObject o ? Describe(o) : "")}): {inner.Message}" + LaterCreation(inner.Message, i, createdAt));
                    done.Add(new JObject { ["op"] = kind, ["error"] = inner.Message });
                }
            }
            // racks whose layout changed: their lanes rebuilt once, only when the list is kept
            var racks = session.RacksToRebuild;
            var rebuilt = new List<string>();
            if (failures.Count == 0 && !validateOnly)
                foreach (var rack in racks) rebuilt.Add($"{rack.name}: {RebuildRack(rack)}");
            var teamsPlaced = new SortedDictionary<string, float[]>(StringComparer.Ordinal);
            if (failures.Count == 0 && !validateOnly)
                foreach (string t in teamsToPlace)
                {
                    var team = GameObject.Find(t)?.GetComponent<UnityResourceTeam>()
                               ?? UnityCompat.FindAll<UnityResourceTeam>().FirstOrDefault(x => x != null && x.name == t);
                    if (team == null) continue;   // removed or renamed further on in the list
                    var spot = ModelLayout.FreeSpotForTeam(team);
                    Undo.RecordObject(team.transform, "Place " + t);
                    team.transform.position = spot;
                    teamsPlaced[t] = new[] { spot.x, spot.z };
                }
            // a label read that nothing sets, once the whole list is applied
            var labelWarnings = failures.Count == 0 ? LabelWarnings(session.EditedObjects) : new List<string>();
            // rule parameter lists that change nothing, once the whole list is applied
            var ruleWarnings = session.RuleWarnings();
            if (failures.Count == 0) labelWarnings.AddRange(ruleWarnings);
            if (failures.Count > 0 || validateOnly) TeamMemberPreviews.Revert(group);
            if (failures.Count > 0)
                throw new ArgumentException($"{failures.Count} of {ops.Count} operation(s) failed — nothing was changed: " + string.Join(" | ", failures) +
                                            (failures.Count > 1 ? " (a later operation may fail because an earlier one did)." : ""));
            // the answer: a summary (134 result lines for 134 operations), each result with detail=full
            bool full = string.Equals((detail ?? "").Trim(), "full", StringComparison.OrdinalIgnoreCase);
            var warnings = new List<string>(labelWarnings);
            var created = new List<string>();
            var moved = new List<string>();
            var placed = new SortedDictionary<string, JArray>(StringComparer.Ordinal);   // tools placed beside the model
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var j in done)
            {
                string kind = (string)j["op"] ?? "?";
                counts[kind] = counts.TryGetValue(kind, out int n) ? n + 1 : 1;
                // add answers its object's 'name', add_team its 'team'
                if ((kind == "add" || kind == "add_team") && (j["name"] ?? j["team"]) != null) created.Add((string)(j["name"] ?? j["team"]));
                if (kind == "move" && j["moved"] != null && !moved.Contains((string)j["moved"])) moved.Add((string)j["moved"]);
                if (kind == "add" && j["name"] != null && j["placedAt"] is JArray at) placed[(string)j["name"]] = at;
                if (j["warnings"] is JArray w)
                    foreach (var x in w) if (x.Type == JTokenType.String && !warnings.Contains((string)x)) warnings.Add((string)x);
            }
            if (validateOnly)
                return full ? (object)new { valid = true, @checked = ops.Count, results = done, warnings, note = "validateOnly: nothing was changed." }
                            : new { valid = true, @checked = ops.Count, operations = counts, warnings, note = "validateOnly: nothing was changed." };
            Undo.CollapseUndoOperations(group);
            Undo.SetCurrentGroupName($"SimuLean: {ops.Count} edits");
            // the footprints of the objects created and those that overlap one of them (neighbours placed 2 m apart
            // when a queue is 3 m wide)
            var sizes = new SortedDictionary<string, float[]>(StringComparer.Ordinal);
            foreach (string n in created)
            {
                var go = GameObject.Find(n) ?? ModelLayout.Elements().FirstOrDefault(e => e.name == n);
                var size = go != null ? ModelLayout.SizeOf(go) : null;
                if (size != null) sizes[n] = size;
            }
            // the objects created or moved: the footprints they overlap, once the whole list stands
            var involved = created.Concat(moved).Distinct().ToList();
            var overlaps = involved.Count > 0 ? ModelLayout.Overlaps(ModelLayout.Elements(), involved) : new List<string>();
            object placedNote = placed.Count == 0 ? null : new { at = placed, note = "no position given: beside the model, in a row behind it (+Z); their place does not change a run" };
            object teamsNote = teamsPlaced.Count == 0 ? null
                : new { at = teamsPlaced, note = "no position given: in front of the model (−Z), once the whole list stood; a Straight team without a " +
                                                 "home node starts its walks there (simulean_move moves it)" };
            if (full) return new { applied = ops.Count, results = done, rebuilt, sizes, placed = placedNote, teamsPlaced = teamsNote, overlaps, warnings };
            return new { applied = ops.Count, operations = counts, created, sizes, placed = placedNote, teamsPlaced = teamsNote, overlaps, rebuilt, warnings };
        }

        // each operation answers as its command does (AgentReply: its own errors / warnings logged); a set belongs to the session
        private static JObject RunOperation(string kind, JObject op, int i, EditSession session)
        {
            switch (kind)
            {
                case "add":
                    return AgentReply.Run(() => AddElementOf(Text(op, "type", i), Opt(op, "name"), Num(op, "x"), Num(op, "y"), Num(op, "z"), Opt(op, "parent")));
                case "set": return AgentReply.Run(() => SetOf(Text(op, "object", i), Text(op, "key", i), Text(op, "value", i), session));
                case "connect": return AgentReply.Run(() => ConnectOf(Text(op, "from", i), Text(op, "to", i)));
                case "disconnect": return AgentReply.Run(() => SimuLeanEditCommands.DisconnectOf(Text(op, "from", i), Text(op, "to", i)));
                case "reorder": return AgentReply.Run(() => SimuLeanEditCommands.ReorderOf(Text(op, "object", i), Text(op, "next", i)));
                case "remove": return AgentReply.Run(() => SimuLeanEditCommands.RemoveOf(Text(op, "object", i)));
                case "rename": return AgentReply.Run(() => SimuLeanEditCommands.RenameOf(Text(op, "object", i), Text(op, "newName", i)));
                case "add_team":
                    return AgentReply.Run(() => AddTeamOf(Text(op, "name", i), op["members"] != null ? (int)op["members"] : 1, Opt(op, "travel"),
                                                          Num(op, "x"), Num(op, "y"), Num(op, "z"), Opt(op, "parent"), Opt(op, "kind")));
                case "move": return AgentReply.Run(() => SimuLeanEditCommands.MoveOf(Text(op, "object", i), Coordinate(op, "x", i), Coordinate(op, "z", i)));
                case "invoke": return AgentReply.Run(() => InvokeOf(Text(op, "object", i), Text(op, "action", i)));
                case "repeat":
                    return AgentReply.Run(() => RepeatOf(Text(op, "group", i), op["count"] != null ? (int)op["count"] : 1, Opt(op, "offset"), Opt(op, "names"),
                                                         Opt(op, "connectFrom"), Opt(op, "connectTo")));
            }
            throw new ArgumentException($"'{kind}' is not add, set, connect, disconnect, reorder, remove, rename, move, add_team, invoke or repeat.");
        }

        // "No object 'T'" for a name the list creates further on: the operations run in list order
        private static string LaterCreation(string message, int at, Dictionary<string, int> createdAt)
        {
            var m = System.Text.RegularExpressions.Regex.Match(message ?? "", @"No object '([^']+)'");
            return m.Success && createdAt.TryGetValue(m.Groups[1].Value, out int k) && k > at
                ? $" — '{m.Groups[1].Value}' is created later in this list (operation {k + 1}): the operations run in order, put it before."
                : "";
        }

        // a short result per operation (the full answers would fill the agent's context)
        private static JObject Brief(string kind, JObject o)
        {
            var b = new JObject { ["op"] = kind };
            // a team's speed and where it came from (its kind): the brief asked speed is easy to override silently
            foreach (string k in new[] { "name", "team", "speed", "speedFrom", "object", "moved", "from", "to", "removed", "copies" })
                if (o[k] != null) b[k] = o[k];
            if (kind == "move" && o["at"] != null) b["at"] = o["at"];
            if (o["warnings"] is JArray w && w.Count > 0) b["warnings"] = w;
            // a tool placed beside the model: where, [x, z]
            if (o["placedAt"] != null && o["placedAt"].Type == JTokenType.String && o["position"] is JArray pos && pos.Count == 3)
                b["placedAt"] = new JArray(Math.Round((double)pos[0], 1), Math.Round((double)pos[2], 1));
            return b;
        }

        private static string Describe(JObject op)
        {
            foreach (string k in new[] { "name", "object", "from", "group" })
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

        // a coordinate an operation must give (a move without x or z would put the object at 0)
        private static float Coordinate(JObject op, string key, int i)
        {
            var v = op[key];
            if (v == null || v.Type == JTokenType.Null) throw new ArgumentException($"operation {i + 1}: no \"{key}\".");
            if (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) return (float)v;
            if (float.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return f;
            throw new ArgumentException($"operation {i + 1}: \"{key}\" = '{v}' is not a number.");
        }

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
                    // names first (unique), then the references inside the group remapped to this copy: the objects of the
                    // group itself always (decoration too: its copies kept Unity's "(Clone)" and were not listed),
                    // the model objects inside them (the decoration's parts keep their names)
                    var renamed = new Dictionary<string, string>();
                    foreach (var copy in made)
                        foreach (var t in copy.GetComponentsInChildren<Transform>(true))
                        {
                            if (t != copy.transform && !IsModelObject(t.gameObject)) continue;
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
                TeamMemberPreviews.Revert(undo);
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
