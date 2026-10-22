using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>Shots taken from the Scene view (the inspector's buttons and <c>simulean_record_shot fromView</c>).</summary>
    public static class RecorderAuthoring
    {
        /// <summary>The Scene view's camera as a fixed shot.</summary>
        public static SimRecorder.Shot FromView(Camera view, string name)
        {
            var t = view.transform;
            return new SimRecorder.Shot
            {
                name = name, framing = SimRecorder.ShotFraming.Fixed, position = t.position, rotation = t.rotation.eulerAngles,
                fieldOfView = 0f,
            };
        }

        /// <summary>Frames <paramref name="target"/> as the camera sees it now: its angle and distance, kept when the
        /// element moves.</summary>
        public static void FrameFromView(SimRecorder.Shot shot, Camera view, Transform target)
        {
            var t = view.transform;
            Vector3 look = target.position + Vector3.up * shot.lookHeight;
            Vector3 e = t.rotation.eulerAngles;
            shot.framing = shot.framing == SimRecorder.ShotFraming.Orbit ? SimRecorder.ShotFraming.Orbit : SimRecorder.ShotFraming.Element;
            shot.target = target;
            shot.elevation = Mathf.Clamp(Mathf.DeltaAngle(0f, e.x), -10f, 89f);
            shot.azimuth = Mathf.Repeat(e.y, 360f);
            shot.distance = Mathf.Max(0.5f, Vector3.Distance(t.position, look));
        }

        /// <summary>Moves the Scene view to a camera pose (to look through a shot).</summary>
        public static void LookThrough(SceneView view, SimRecorder.CameraPose pose)
        {
            if (view == null) return;
            var go = new GameObject("RecorderPreview") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                go.transform.SetPositionAndRotation(pose.position, pose.rotation);
                view.AlignViewToObject(go.transform);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }

    /// <summary>
    /// The Recorder's inspector: its settings, then the shots with buttons to add one from the Scene view (a fixed pose, or
    /// the selected element framed as seen now), to look through a shot and to preview the fly-path at any time of the
    /// video; in Play, the take's progress.
    /// </summary>
    [CustomEditor(typeof(SimRecorder))]
    public sealed class SimRecorderEditor : UnityEditor.Editor
    {
        private float previewTime;

        public override void OnInspectorGUI()
        {
            var r = (SimRecorder)target;
            DrawDefaultInspector();
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Shots from the Scene view", EditorStyles.boldLabel);
            var view = SceneView.lastActiveSceneView;
            using (new EditorGUI.DisabledScope(view == null || view.camera == null))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Add shot from view", "A fixed shot where the Scene view's camera is now.")))
                        AddShot(r, RecorderAuthoring.FromView(view.camera, $"Shot {r.shots.Count + 1}"));
                    var sel = Selection.activeTransform;
                    using (new EditorGUI.DisabledScope(sel == null || sel == r.transform))
                        if (GUILayout.Button(new GUIContent("Frame selection", "A shot of the selected element from the Scene view's angle and distance: it follows the element when the layout changes.")))
                        {
                            var shot = new SimRecorder.Shot { name = sel.name };
                            RecorderAuthoring.FrameFromView(shot, view.camera, sel);
                            AddShot(r, shot);
                        }
                }
                for (int i = 0; i < r.shots.Count; i++)
                {
                    var sh = r.shots[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"{i + 1}. {sh.name}", $"{sh.framing}, {sh.seconds:0.#} s{(sh.glide ? ", glide" : "")}");
                        if (GUILayout.Button("View", GUILayout.Width(48)))
                            RecorderAuthoring.LookThrough(view, r.Framing(i, 0f));
                        if (GUILayout.Button(new GUIContent("Set", "This shot from the Scene view (a fixed pose; an Element / Orbit shot keeps its element and takes the view's angle and distance)."), GUILayout.Width(40)))
                        {
                            Undo.RecordObject(r, "Set shot from view");
                            if ((sh.framing == SimRecorder.ShotFraming.Element || sh.framing == SimRecorder.ShotFraming.Orbit) && sh.target != null)
                                RecorderAuthoring.FrameFromView(sh, view.camera, sh.target);
                            else
                            {
                                var v = RecorderAuthoring.FromView(view.camera, sh.name);
                                sh.framing = SimRecorder.ShotFraming.Fixed;
                                sh.position = v.position;
                                sh.rotation = v.rotation;
                            }
                            EditorUtility.SetDirty(r);
                        }
                    }
                }
                if (r.shots.Count > 0)
                {
                    float total = r.TotalSeconds;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        previewTime = EditorGUILayout.Slider(new GUIContent("Preview at (s)", "Look through the fly-path at this time of the video."), previewTime, 0f, total);
                        if (GUILayout.Button("Look", GUILayout.Width(48))) RecorderAuthoring.LookThrough(view, r.PoseAt(previewTime));
                    }
                }
            }
            var size = SimRecorder.EvenSize(r.width, r.height);
            string rate = r.output == SimRecorder.VideoOutput.Mp4
                ? $" {SimRecorder.BitRate(size.x, size.y, r.fps, r.quality, r.bitrateMbps) / 1e6:0.#} Mbps (≈ {SimRecorder.BitRate(size.x, size.y, r.fps, r.quality, r.bitrateMbps) / 8e6 * r.TotalSeconds:0} MB)"
                : r.output == SimRecorder.VideoOutput.Ffmpeg ? $" {r.quality}" : "";
            EditorGUILayout.HelpBox($"{r.TotalSeconds:0.#} s · {r.TotalFrames} frames · {size.x}×{size.y} · {r.output}{rate}" +
                                    (string.IsNullOrWhiteSpace(r.startAt) ? "" : $" · from {r.startAt}"), MessageType.None);
            if (Application.isPlaying && SimRecorder.Current == r)
            {
                string state = r.Done ? (r.Error ?? "done: " + (r.VideoPath ?? r.TakeFolder)) : r.Recording ? $"recording {r.FramesWritten}/{r.TotalFrames}" : "driving the clock to the start";
                EditorGUILayout.HelpBox(state, MessageType.Info);
                Repaint();
            }
        }

        private static void AddShot(SimRecorder r, SimRecorder.Shot shot)
        {
            Undo.RecordObject(r, "Add shot");
            shot.glide = r.shots.Count > 0;
            r.shots.Add(shot);
            EditorUtility.SetDirty(r);
        }
    }

    /// <summary>A shot in the inspector: only the fields of its framing.</summary>
    [CustomPropertyDrawer(typeof(SimRecorder.Shot))]
    public sealed class RecorderShotDrawer : PropertyDrawer
    {
        private static string[] FieldsOf(SimRecorder.ShotFraming f)
        {
            switch (f)
            {
                case SimRecorder.ShotFraming.Fixed: return new[] { "position", "rotation" };
                case SimRecorder.ShotFraming.Pose: return new[] { "pose" };
                case SimRecorder.ShotFraming.Element: return new[] { "target", "distance", "elevation", "azimuth", "lookHeight" };
                default: return new[] { "target", "distance", "elevation", "azimuth", "orbitDegrees", "lookHeight" };
            }
        }

        private static readonly string[] Common = { "seconds", "glide", "glideSeconds", "fieldOfView", "title", "subtitle" };

        private static string[] Visible(SerializedProperty p)
        {
            var framing = (SimRecorder.ShotFraming)p.FindPropertyRelative("framing").enumValueIndex;
            var list = new System.Collections.Generic.List<string> { "name", "framing" };
            list.AddRange(FieldsOf(framing));
            foreach (var c in Common)
                if (c != "glideSeconds" || p.FindPropertyRelative("glide").boolValue) list.Add(c);
            return list.ToArray();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float h = EditorGUIUtility.singleLineHeight;
            if (!property.isExpanded) return h;
            foreach (var f in Visible(property))
                h += EditorGUI.GetPropertyHeight(property.FindPropertyRelative(f), true) + EditorGUIUtility.standardVerticalSpacing;
            return h;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            string title = property.FindPropertyRelative("name").stringValue;
            string summary = $"{title}  ({(SimRecorder.ShotFraming)property.FindPropertyRelative("framing").enumValueIndex}, " +
                             $"{property.FindPropertyRelative("seconds").floatValue:0.#} s)";
            property.isExpanded = EditorGUI.Foldout(line, property.isExpanded, summary, true);
            if (!property.isExpanded) return;
            EditorGUI.indentLevel++;
            float y = line.yMax + EditorGUIUtility.standardVerticalSpacing;
            foreach (var f in Visible(property))
            {
                var p = property.FindPropertyRelative(f);
                float h = EditorGUI.GetPropertyHeight(p, true);
                EditorGUI.PropertyField(new Rect(position.x, y, position.width, h), p, true);
                y += h + EditorGUIUtility.standardVerticalSpacing;
            }
            EditorGUI.indentLevel--;
        }
    }
}
