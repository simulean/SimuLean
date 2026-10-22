using System.Collections.Generic;
using System.Linq;
using SimuLean.Expressions;
using SimuLean.Signals;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The reaction inspector: it reads as its sentence — <b>When</b> [elements] [signal] <b>if</b> [condition]
    /// <b>do</b> [actions] — with only the fields each action uses, the problems found (no element, an expression that does
    /// not parse, a label without an item…) and, in Play, how many times it fired.
    /// </summary>
    [CustomEditor(typeof(UnityReaction))]
    public class UnityReactionEditor : UnityEditor.Editor
    {
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var r = (UnityReaction)target;
            var bound = Application.isPlaying ? r.Bound : null;
            SimuLeanGUI.ToolHeader(r, !r.reactionEnabled ? "OFF" : bound != null ? $"FIRED {bound.Fired}" : null);
            EditorGUILayout.LabelField(Sentence(r), EditorStyles.wordWrappedLabel);
            var issues = Issues(r);
            SimuLeanGUI.Issues(issues);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("reactionEnabled"), new GUIContent("Enabled"));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("When", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            var signal = serializedObject.FindProperty("signal");
            var kind = (SignalKind)signal.enumValueIndex;
            if (!SignalKinds.IsTime(kind)) DrawSources(serializedObject.FindProperty("sources"));
            EditorGUILayout.PropertyField(signal, new GUIContent("Signal", "Of the elements: an item enters / leaves, a process ends, the " +
                                                                           "state changes, a stop starts / ends. Of the clock: the run starts, every period, at a time."));
            kind = (SignalKind)signal.enumValueIndex;
            if (SignalKinds.HasState(kind))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("state"),
                    new GUIContent(kind == SignalKind.StateChanged ? "To state" : "Stop state", "Empty = any (e.g. PROCESSING, BLOCKED, STOPPED, FAILED)"));
            string unit = SimuLeanGUI.TimeUnit;
            if (kind == SignalKind.Every)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("period"), new GUIContent($"Period ({unit})"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("firstTime"), new GUIContent($"First at ({unit})", "Negative = one period after the start."));
            }
            if (kind == SignalKind.AtTime)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("atTime"), new GUIContent($"At ({unit})"));
            if (SignalKinds.IsTime(kind))
                EditorGUILayout.LabelField("A clock signal: no element of its own — the actions name theirs, and the condition reads " +
                                           "elements by name (content('Buffer')).", EditorStyles.wordWrappedMiniLabel);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("If", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(serializedObject.FindProperty("condition"),
                new GUIContent("Condition", "Empty = always. An expression over the item and the element: element.content >= 5, item.type == 'A', " +
                                            "rand() < 0.1, content('Buffer') > 10"));
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Do", EditorStyles.boldLabel);
            DrawActions(serializedObject.FindProperty("actions"), kind);

            bool open = SessionState.GetBool("SimuLean.Reaction.Advanced", false);
            bool now = EditorGUILayout.Foldout(open, "Advanced", true);
            if (now != open) SessionState.SetBool("SimuLean.Reaction.Advanced", now);
            if (now)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("timing"),
                    new GUIContent("Timing", "Next Event (default): after the step that signalled — the safe choice. Immediate: inside it, " +
                                             "seeing the state before anything else moves."));
                EditorGUI.indentLevel--;
            }

            if (bound != null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("This run", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"Fired {bound.Fired}" + (bound.Skipped > 0 ? $" · condition not met {bound.Skipped}" : ""));
                if (!double.IsNaN(bound.LastFired))
                    EditorGUILayout.LabelField($"Last at t = {bound.LastFired:0.##}" + (bound.LastItem != null ? $" · {bound.LastItem}" : "")
                                               + (bound.LastSource != null ? $" · {bound.LastSource.GetName()}" : ""), EditorStyles.miniLabel);
            }
            serializedObject.ApplyModifiedProperties();
        }

        // the elements it listens to, always in sight: one row each, + Element
        private static void DrawSources(SerializedProperty sources)
        {
            for (int i = 0; i < sources.arraySize; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PropertyField(sources.GetArrayElementAtIndex(i), new GUIContent(i == 0 ? "Elements" : " "));
                    if (GUILayout.Button("✕", GUILayout.Width(22)))
                    {
                        sources.GetArrayElementAtIndex(i).objectReferenceValue = null;
                        sources.DeleteArrayElementAtIndex(i);
                        break;
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(sources.arraySize == 0 ? "Elements" : " ");
                if (GUILayout.Button(new GUIContent("+ Element", "Another element it listens to"), GUILayout.Width(90)))
                    sources.InsertArrayElementAtIndex(sources.arraySize);
            }
        }

        private static void DrawActions(SerializedProperty actions, SignalKind signal)
        {
            for (int i = 0; i < actions.arraySize; i++)
            {
                var a = actions.GetArrayElementAtIndex(i);
                var kindProp = a.FindPropertyRelative("kind");
                var kind = (ReactionActionKind)kindProp.enumValueIndex;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    bool remove;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PropertyField(kindProp, new GUIContent($"{i + 1}."));
                        remove = GUILayout.Button("✕", GUILayout.Width(22));
                    }
                    switch (kind)
                    {
                        case ReactionActionKind.SetItemLabel:
                            Field(a, "name", "Label");
                            Field(a, "value", "Value");
                            break;
                        case ReactionActionKind.SetElementProperty:
                            Field(a, "target", "Element", "Empty = the element that signalled.");
                            Field(a, "name", "Property");
                            Field(a, "value", "Value");
                            break;
                        case ReactionActionKind.Stop:
                            Who(a, "Empty = the element that signalled.");
                            Field(a, "state", "State", a.FindPropertyRelative("onTeam").boolValue ? "e.g. OFF_SHIFT for members sent off" : null);
                            Field(a, "mode", "Mode");
                            Field(a, "duration", $"Duration ({SimuLeanGUI.TimeUnit})", "0 = until a Resume action (of any reaction, same group).");
                            Field(a, "group", "Group");
                            break;
                        case ReactionActionKind.Resume:
                            Who(a, "Empty = the element that signalled. Ends the stops reactions started on it in this group.");
                            Field(a, "group", "Group");
                            break;
                        case ReactionActionKind.PushToList:
                            Field(a, "list", "List");
                            Field(a, "value", "Value");
                            break;
                    }
                    Field(a, "onlyIf", "Only if");
                    if (remove) { actions.DeleteArrayElementAtIndex(i); break; }
                }
            }
            if (GUILayout.Button("+ Action"))
            {
                actions.InsertArrayElementAtIndex(actions.arraySize);
                if (actions.arraySize == 1)
                    actions.GetArrayElementAtIndex(0).FindPropertyRelative("kind").enumValueIndex = (int)ReactionActionKind.SetElementProperty;
            }
        }

        // Stop / Resume: an element, or members of a team
        private static void Who(SerializedProperty action, string elementTip)
        {
            var onTeam = action.FindPropertyRelative("onTeam");
            int choice = EditorGUILayout.Popup(new GUIContent("Of", "An element, or members of a resource team (operators, vehicles…)"),
                                               onTeam.boolValue ? 1 : 0, new[] { new GUIContent("Element"), new GUIContent("Team members") });
            onTeam.boolValue = choice == 1;
            if (onTeam.boolValue)
            {
                Field(action, "team", "Team");
                Field(action, "count", "Members", "How many (0 = all).");
            }
            else Field(action, "target", "Element", elementTip);
        }

        private static void Field(SerializedProperty action, string name, string label, string tooltip = null)
        {
            var p = action.FindPropertyRelative(name);
            EditorGUILayout.PropertyField(p, new GUIContent(label, tooltip ?? p.tooltip));
        }

        // ── the sentence and the checks ─────────────────────────────────────

        /// <summary>"When Queue A: an item enters, if element.content >= 5 → stop Source for 10; set Router.lane = 1".</summary>
        public static string Sentence(UnityReaction r)
        {
            var names = r.sources.Where(s => s != null).Select(s => s.name).ToList();
            string who = names.Count == 0 ? "(no element)" : names.Count <= 3 ? string.Join(" or ", names) : $"{names.Count} elements";
            string when = r.IsTimeSignal ? TimeText(r) : $"When {who}: {SignalText(r.signal, r.state)}";
            if (!string.IsNullOrWhiteSpace(r.condition)) when += $", if {r.condition.Trim()}";
            var acts = r.actions.Where(a => a != null).Select(ActionText).ToList();
            return when + " → " + (acts.Count == 0 ? "(nothing yet)" : string.Join("; ", acts));
        }

        private static string TimeText(UnityReaction r)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            switch (r.signal)
            {
                case SignalKind.RunStarted: return "When the run starts";
                case SignalKind.Every:
                    return "Every " + r.period.ToString("0.##", c) + (r.firstTime >= 0 ? " from " + r.firstTime.ToString("0.##", c) : "");
                default: return "At " + r.atTime.ToString("0.##", c);
            }
        }

        private static string SignalText(SignalKind kind, string state)
        {
            bool st = !string.IsNullOrWhiteSpace(state);
            switch (kind)
            {
                case SignalKind.ItemEntered: return "an item enters";
                case SignalKind.ItemExited: return "an item leaves";
                case SignalKind.ProcessEnded: return "a process ends";
                case SignalKind.StateChanged: return st ? $"the state becomes {state.Trim()}" : "the state changes";
                case SignalKind.Stopped: return st ? $"a {state.Trim()} stop starts" : "a stop starts";
                default: return st ? $"a {state.Trim()} stop ends" : "a stop ends";
            }
        }

        private static string ActionText(UnityReaction.ActionSpec a)
        {
            string target = a.onTeam ? (a.count > 0 ? a.count + " of " : "all of ") + (a.team != null ? a.team.name : "?")
                          : a.target != null ? a.target.name : "it";
            string value = string.IsNullOrWhiteSpace(a.value) ? "?" : a.value.Trim();
            string text = Text(a, target, value);
            if ((a.kind == ReactionActionKind.Stop || a.kind == ReactionActionKind.Resume) && !string.IsNullOrWhiteSpace(a.group))
                text += $" [{a.group.Trim()}]";
            return string.IsNullOrWhiteSpace(a.onlyIf) ? text : $"if {a.onlyIf.Trim()}: {text}";
        }

        private static string Text(UnityReaction.ActionSpec a, string target, string value)
        {
            switch (a.kind)
            {
                case ReactionActionKind.SetItemLabel: return $"item.{Or(a.name)} = {value}";
                case ReactionActionKind.SetElementProperty: return $"{(a.target != null ? a.target.name : "element")}.{Or(a.name)} = {value}";
                case ReactionActionKind.Stop: return $"stop {target}" + (a.duration > 0 ? " for " + a.duration.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : " until resumed");
                case ReactionActionKind.Resume: return $"resume {target}";
                default: return $"push {value} to {(a.list != null ? a.list.ListName : "?")}";
            }
        }

        private static string Or(string name) => string.IsNullOrWhiteSpace(name) ? "?" : name.Trim();

        private static List<InspectorIssue> Issues(UnityReaction r)
        {
            var issues = new List<InspectorIssue>();
            if (!r.IsTimeSignal && r.sources.All(s => s == null))
                issues.Add(new InspectorIssue(MessageType.Warning, "Choose the elements it listens to (When ▸ Elements)."));
            if (r.signal == SignalKind.Every && !(r.period > 0))
                issues.Add(new InspectorIssue(MessageType.Error, "Every: the period must be > 0."));
            if (r.actions.Count == 0)
                issues.Add(new InspectorIssue(MessageType.Info, "It does nothing yet: add an action (Do ▸ + Action)."));
            Check(issues, r.condition, "The condition");
            bool itemSignal = SignalKinds.HasItem(r.signal);
            for (int i = 0; i < r.actions.Count; i++)
            {
                var a = r.actions[i];
                if (a == null) continue;
                string at = $"Action {i + 1}";
                bool needsName = a.kind == ReactionActionKind.SetItemLabel || a.kind == ReactionActionKind.SetElementProperty;
                bool needsValue = needsName || a.kind == ReactionActionKind.PushToList;
                if (a.kind == ReactionActionKind.SetItemLabel && !itemSignal)
                    issues.Add(new InspectorIssue(MessageType.Error, $"{at}: an item label needs an item signal (an item enters / leaves, a process ends)."));
                bool stopResume = a.kind == ReactionActionKind.Stop || a.kind == ReactionActionKind.Resume;
                if (stopResume && a.onTeam && a.team == null)
                    issues.Add(new InspectorIssue(MessageType.Error, $"{at}: choose the team."));
                if (stopResume && !a.onTeam && a.target == null && r.IsTimeSignal)
                    issues.Add(new InspectorIssue(MessageType.Error, $"{at}: choose the element (a clock signal has none of its own)."));
                if (needsName && string.IsNullOrWhiteSpace(a.name))
                    issues.Add(new InspectorIssue(MessageType.Error, $"{at}: give the name to set."));
                if (needsValue && string.IsNullOrWhiteSpace(a.value))
                    issues.Add(new InspectorIssue(MessageType.Error, $"{at}: give the value."));
                if (a.kind == ReactionActionKind.PushToList && a.list == null)
                    issues.Add(new InspectorIssue(MessageType.Error, $"{at}: choose the list."));
                if (needsValue && UnityReaction.Value(a.value) is string) Check(issues, a.value, $"{at}'s value");
            }
            return issues;
        }

        private static void Check(List<InspectorIssue> issues, string expression, string what)
        {
            if (string.IsNullOrWhiteSpace(expression)) return;
            string error = Expression.Check(expression.Trim());
            if (error != null) issues.Add(new InspectorIssue(MessageType.Error, $"{what}: {error}"));
        }
    }
}
