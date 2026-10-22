using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The 3D model of a station element, chosen from a list (as the shape of a FlexSim object): e.g. a MultiServer
    /// shown as the library's CNC machine or as the classic workstation, or your own model in <see cref="custom"/>.
    /// The chosen model is a child named <c>Model</c>, placed in edit mode (the element is visible as soon as it is
    /// dragged into the scene). When the model has a work socket (<c>WorkSocket</c>, <c>PalletSocket</c>,
    /// <c>PatientSocket</c>…) the element's item position (<c>inItemPosition</c> / <c>itemPosition</c>) is bound to
    /// it, unless you set your own. View only: it never changes the model's parameters.
    /// <para>The <see cref="look"/> can also be <see cref="Look.Virtual"/> — a step done
    /// elsewhere or not face to face (a test at home, a report written later, a phone review): no model, a translucent
    /// marker on the floor with an optional caption — or <see cref="Look.None"/> (nothing drawn). Both hide the element's
    /// own model (its renderers); the marker is rebuilt when enabled and never saved.</para>
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("SimuLean/Visuals/Station Model")]
    public class StationModel : MonoBehaviour
    {
        /// <summary>What the station shows.</summary>
        public enum Look
        {
            /// <summary>The chosen model (an option or <see cref="custom"/>).</summary>
            Model,
            /// <summary>A virtual step: a translucent marker on the floor and a caption, no model.</summary>
            Virtual,
            /// <summary>Nothing (the items, labels and counters only).</summary>
            None
        }

        [Serializable]
        public class Option
        {
            public string name;
            public GameObject model;
        }

        [Tooltip("The models this station can show.")]
        public List<Option> options = new List<Option>();
        [Tooltip("The option shown.")]
        [Min(0)] public int selected;
        [Tooltip("Your own model (a prefab or an FBX): shown instead of the options when set.")]
        public GameObject custom;

        [Tooltip("Model: the chosen model. Virtual: a step done elsewhere or not face to face — a translucent marker on the " +
                 "floor with a caption. None: nothing drawn. Virtual and None also hide the element's own model.")]
        public Look look = Look.Model;
        [Tooltip("Virtual look: size of the marker on the floor (X, Z in m).")]
        public Vector2 virtualSize = new Vector2(1.6f, 1.2f);
        [Tooltip("Virtual look: the caption on the marker (e.g. 'At home', 'Not face to face'); empty = none.")]
        public string virtualCaption = "";
        [Tooltip("Virtual look: the marker's colour (its alpha is the fill's).")]
        public Color virtualColor = new Color(0.20f, 0.55f, 0.75f, 0.22f);

        public const string VirtualChild = "VirtualMarker";

        /// <summary>Socket names bound to the element's item position, in order of preference.</summary>
        public static readonly string[] SocketNames = { "WorkSocket", "PalletSocket", "PatientSocket", "ServiceSocket_1", "ItemSocket" };
        /// <summary>The element fields an item socket is bound to.</summary>
        public static readonly string[] ItemPositionFields = { "inItemPosition", "itemPosition" };
        public const string ModelChild = "Model";

        [SerializeField, HideInInspector] private GameObject shownSource;

        /// <summary>The model to show: <see cref="custom"/>, else the selected option (none unless the look is Model).</summary>
        public GameObject Chosen => look != Look.Model ? null : custom != null ? custom
            : options != null && selected >= 0 && selected < options.Count ? options[selected].model : null;

        /// <summary>The model instance (the <c>Model</c> child), or null.</summary>
        public Transform Instance => transform.Find(ModelChild);

        /// <summary>The first work socket of the shown model, or null.</summary>
        public Transform Socket()
        {
            var inst = Instance;
            if (inst == null) return null;
            foreach (var name in SocketNames)
                foreach (var t in inst.GetComponentsInChildren<Transform>(true))
                    if (t.name == name) return t;
            return null;
        }

        /// <summary>Where items come in (a box's door): the model's <c>EntrySocket</c>.</summary>
        public const string EntrySocket = "EntrySocket";
        /// <summary>Where the staff works (a doctor in a box, an operator at a machine): <c>StaffSocket</c> /
        /// <c>StaffSocket_1</c> / <c>OperatorSocket</c>.</summary>
        public static readonly string[] StaffSockets = { "StaffSocket", "StaffSocket_1", "OperatorSocket" };

        /// <summary>A named position of <paramref name="element"/>: a child of that name (the user's own, first), else one
        /// of its shown model's; null when there is none.</summary>
        public static Transform Position(Component element, params string[] names)
        {
            if (element == null) return null;
            foreach (var n in names)
                foreach (Transform c in element.transform)
                    if (c.name == n) return c;
            var sm = element.GetComponent<StationModel>();
            var inst = sm != null ? sm.Instance : null;
            if (inst == null) return null;
            foreach (var n in names)
                foreach (var t in inst.GetComponentsInChildren<Transform>(true))
                    if (t.name == n) return t;
            return null;
        }

        /// <summary>
        /// Where a person works at <paramref name="element"/> (a view): its staff position, else in front of it —
        /// the side its +Z faces (the library's models face +Z), half its depth plus 0.6 m from its centre.
        /// <paramref name="face"/> is the point to look at (the element).
        /// </summary>
        public static Vector3 StaffPosition(Component element, out Vector3 face)
        {
            face = element.transform.position;
            var staff = Position(element, StaffSockets);
            if (staff != null) return staff.position;
            ItemVisuals.Footprint(element, out Vector3 size, out _);
            float depth = Mathf.Max(size.z, 0.5f);
            Vector3 forward = element.transform.forward; forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            return element.transform.position + forward.normalized * (depth * 0.5f + 0.6f);
        }

        /// <summary>True when the shown model is not the chosen one (the editor rebuilds it).</summary>
        public bool Stale => Chosen != shownSource || (Chosen != null) != (Instance != null);

        private void OnEnable() => ApplyLook();
        private void OnDisable() => RemoveMarker();

        /// <summary>Shows or hides the element's own model and the virtual marker per <see cref="look"/>. View only.</summary>
        public void ApplyLook()
        {
            // the own renderers are switched back on only if this look switched them off (a pad hidden by hand stays
            // hidden); that flag is saved (review2 #14: lost at a reload, the element stayed invisible) and a queue's
            // pad setting is kept (#34)
            bool hide = look != Look.Model;
            if (hide || hidOwn)
            {
                bool show = OwnModelShown(look, gameObject);
                foreach (var r in GetComponents<Renderer>()) if (r.enabled != show) r.enabled = show;
                hidOwn = hide;
            }
            RemoveMarker();
            if (look == Look.Virtual) BuildMarker();
        }

        /// <summary>The virtual marker (the <c>VirtualMarker</c> child), or null.</summary>
        public Transform Marker => transform.Find(VirtualChild);

        [SerializeField, HideInInspector] private bool hidOwn;

        /// <summary>Whether an element's own renderers show: its station look (if any) is Model and, for a queue, its pad is on.</summary>
        public static bool OwnModelShown(Look look, GameObject element)
        {
            var queue = element.GetComponent<UnityQueue>();
            return look == Look.Model && (queue == null || queue.showFootprint);
        }

        private void BuildMarker()
            => FloorMarker.Build(transform, VirtualChild, virtualSize, virtualColor, virtualCaption, 0.22f, FontStyle.Italic);

        private void RemoveMarker() => FloorMarker.Remove(transform, VirtualChild);

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying) return;
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                if (Stale) Rebuild();
                ApplyLook();
            };
        }

        /// <summary>Replaces the <c>Model</c> child by the chosen model and binds the element's item position to its
        /// socket (when the field is empty or still points into the former model). Editor only.</summary>
        [SimuLeanAction("Rebuild the station model with the chosen look")]
        public void Rebuild()
        {
            if (Application.isPlaying) return;
            var old = Instance;
            var element = GetComponent<SElement>();
            var fields = new List<System.Reflection.FieldInfo>();
            if (element != null)
                foreach (var f in ItemPositionFields)
                {
                    var fi = element.GetType().GetField(f);
                    if (fi != null && fi.FieldType == typeof(Transform)) fields.Add(fi);
                }
            // fields pointing into the old model are re-bound; others (the user's own points) are kept
            var rebind = new List<System.Reflection.FieldInfo>();
            foreach (var fi in fields)
            {
                var v = fi.GetValue(element) as Transform;
                if (v == null || (old != null && v.IsChildOf(old))) rebind.Add(fi);
            }
            if (old != null) UnityEditor.Undo.DestroyObjectImmediate(old.gameObject);

            var chosen = Chosen;
            shownSource = chosen;
            if (chosen != null)
            {
                var inst = UnityEditor.PrefabUtility.IsPartOfPrefabAsset(chosen)
                    ? (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(chosen, transform)
                    : Instantiate(chosen, transform);
                inst.name = ModelChild;
                inst.transform.localPosition = Vector3.zero;
                // the model's own root rotation / scale (e.g. a Z-up import turned 270° about X) are kept
                inst.transform.localRotation = chosen.transform.localRotation;
                inst.transform.localScale = chosen.transform.localScale;
                UnityEditor.Undo.RegisterCreatedObjectUndo(inst, "Station model");
            }
            var socket = Socket();
            foreach (var fi in rebind)
            {
                UnityEditor.Undo.RecordObject(element, "Station model socket");
                fi.SetValue(element, socket);
                UnityEditor.EditorUtility.SetDirty(element);
            }
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
