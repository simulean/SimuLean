using SimuLean.Resources;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Plays the people clips on the look a
    /// person shows (<see cref="OperatorAppearance"/>): one clip set for every look — they share the operator's rig — in
    /// <c>People.controller</c>, on an Animator it adds to the shown look's armature (where the clips' paths
    /// <c>Root/Torso/…</c> start). Read only: it picks the clip from the model each frame and nothing feeds back.
    /// <list type="bullet">
    /// <item>A resource member (its <see cref="UnityVehicleAnimator"/>): walking → <c>Walk</c> / <c>CarryWalk</c> (scaled to
    /// the speed, 1.4 m a cycle), working at a station (a Process / Hold step, not moving) → <c>Examine</c> at a
    /// consultation model, <c>WorkDesk</c> elsewhere, carrying → <c>CarryIdle</c>, else <c>Idle</c> / <c>Idle2</c>.</item>
    /// <item>An item (a patient): <see cref="pose"/>, set by the layouts — sitting on a seat (<see cref="ItemLayout"/>),
    /// lying at a station's <c>PatientSocket</c> — else standing.</item>
    /// </list>
    /// Without a controller, or outside Play mode, it does nothing (the procedural limbs of the vehicle animator stay).
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]   // after the layouts that place the items this frame (a station's LateUpdate): no walk shown
                                   // with the person already at its place
    [AddComponentMenu("SimuLean/Visuals/Person Animator")]
    public sealed class PersonAnimator : MonoBehaviour
    {
        public enum Pose { Stand, Sit, Lie }

        /// <summary>What a member does at a station, from the station's model.</summary>
        public enum Work { Desk, Examine, Machine, Weld, Paint }

        [Tooltip("The people clips (Art/Models/Operator/People.controller, built by Tools ▸ SimuLean ▸ Build People Animations).")]
        public RuntimeAnimatorController controller;
        [Tooltip("An item's pose (set by the layouts: seats → Sit, a patient socket → Lie).")]
        public Pose pose = Pose.Stand;
        [Tooltip("Distance of one walk cycle of the Walk clip (m), to match the feet to the speed.")]
        [Min(0.1f)] public float walkCycleMetres = 1.4f;
        [Tooltip("Cross-fade between clips (s).")]
        [Min(0f)] public float fade = 0.25f;
        [Tooltip("An item (a patient) moved to another place by the model walks there instead of jumping (a view: the model's " +
                 "times are unchanged).")]
        public bool walkBetweenPlaces = true;
        [Tooltip("The longest such walk (real seconds): farther places are reached faster.")]
        [Min(0.1f)] public float maxWalkSeconds = 1.5f;
        [Tooltip("Places farther than this (m) are reached at once (e.g. appearing at the entrance).")]
        [Min(0f)] public float maxWalkDistance = 40f;
        [Tooltip("A porter: walks pushing (hands on the bed / stretcher / wheelchair handles) instead of carrying.")]
        public bool pushes;

        public const string Idle = "Idle", Idle2 = "Idle2", Walk = "Walk", CarryIdle = "CarryIdle", CarryWalk = "CarryWalk",
                            WorkDesk = "WorkDesk", Examine = "Examine", SitIdle = "SitIdle", LieOnBed = "LieOnBed",
                            PushWalk = "PushWalk", HandOver = "HandOver", MachineTend = "MachineTend", Weld = "Weld", Paint = "Paint";

        private Animator animator;
        private Renderer[] renderers = new Renderer[0];
        private Transform armature;
        private UnityVehicleAnimator mover;
        private Vector3 lastPosition;
        private float speed;
        private bool hasLast;
        // an item's walk between two places: where it is shown, where the model put it
        private Vector3 shown, target;
        private Quaternion targetRotation;
        private bool hasShown, walking;
        private float walkSpeed;

        /// <summary>True while an item walks to the place the model gave it.</summary>
        public bool Walking => walking;

        // the way to the next place: through these points first (a box's door), then the place
        private readonly System.Collections.Generic.List<Vector3> via = new System.Collections.Generic.List<Vector3>();
        private readonly System.Collections.Generic.List<Vector3> path = new System.Collections.Generic.List<Vector3>();
        private readonly System.Collections.Generic.List<Vector3> rest = new System.Collections.Generic.List<Vector3>();
        private Vector3? exitVia;

        /// <summary>The next walk to a place goes through <paramref name="point"/> (a station's entry: the door of a box, not
        /// through its walls). A view: the model's times are unchanged.</summary>
        public void WalkVia(Vector3 point) { via.Clear(); via.Add(point); }

        /// <summary>The rest of a model walk its drawing had not reached when it ended (<see cref="ItemWalkView"/>): the next
        /// walk to a place goes along it first — the route's nodes, then the station's entry — not straight through the
        /// walls, whatever its length.</summary>
        public void FinishWalk(System.Collections.Generic.IEnumerable<Vector3> points) { rest.Clear(); rest.AddRange(points); }

        /// <summary>The next walk the model starts (leaving a station) goes out through <paramref name="point"/> first.</summary>
        public void LeaveVia(Vector3 point) => exitVia = point;

        /// <summary>The way out set by <see cref="LeaveVia"/>, once (null when none).</summary>
        public Vector3? TakeExitVia() { var v = exitVia; exitVia = null; return v; }

        // set by ItemWalkView each frame the item is on foot in the model (a WalkLink): its position is the walk's
        private int drivenFrame = -1;
        private bool drivenMoving;

        /// <summary>The item is on foot in the model this frame (<see cref="ItemWalkView"/> places it): walking, or standing
        /// at a door (<paramref name="moving"/> false).</summary>
        public void DriveWalk(bool moving) { drivenFrame = Time.frameCount; drivenMoving = moving; }

        /// <summary>The clip playing now (null before the first frame in Play).</summary>
        public string State { get; private set; }

        /// <summary>Sets the pose of an item's person, if it has one (the layouts call it).</summary>
        public static void SetPose(GameObject item, Pose p)
        {
            if (item == null) return;
            var a = item.GetComponent<PersonAnimator>();
            if (a != null) a.pose = p;
        }

        /// <summary>The clip for a person's situation (see the class summary); a pure rule, tested in EditMode.</summary>
        public static string ClipFor(Pose pose, bool moving, bool carrying, bool pushing, Work? work, bool handing, bool altIdle)
        {
            if (pose == Pose.Sit) return SitIdle;
            if (pose == Pose.Lie) return LieOnBed;
            if (moving) return pushing ? PushWalk : carrying ? CarryWalk : Walk;
            if (handing) return HandOver;
            if (work.HasValue)
                switch (work.Value)
                {
                    case Work.Examine: return Examine;
                    case Work.Machine: return MachineTend;
                    case Work.Weld: return Weld;
                    case Work.Paint: return Paint;
                    default: return WorkDesk;
                }
            if (carrying && !pushing) return CarryIdle;
            return altIdle ? Idle2 : Idle;
        }

        /// <summary>The work at a station, from its model's name (a consultation / bed: Examine; a CNC / inspection /
        /// palletizer / robot cell: Machine; a welding positioner or gantry: Weld; a paint booth: Paint; else Desk).</summary>
        public static Work WorkAt(string modelName)
        {
            string n = modelName ?? "";
            bool Has(string k) => n.IndexOf(k, System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (Has("Consultation") || Has("Bed") || Has("Exam")) return Work.Examine;
            if (Has("Weld")) return Work.Weld;
            if (Has("Paint")) return Work.Paint;
            if (Has("Cnc") || Has("Machine") || Has("Inspection") || Has("Palletizer") || Has("Robot")) return Work.Machine;
            return Work.Desk;
        }

        private void Start()
        {
            mover = GetComponentInParent<UnityVehicleAnimator>();
        }

        // the people in the scene (a door opening for whoever comes near)
        private static readonly System.Collections.Generic.List<PersonAnimator> all = new System.Collections.Generic.List<PersonAnimator>();
        private void OnEnable() => all.Add(this);
        private void OnDisable() => all.Remove(this);

        /// <summary>True when a shown person stands within <paramref name="radius"/> (on the floor) of <paramref name="point"/>.</summary>
        public static bool AnyVisibleNear(Vector3 point, float radius)
        {
            float r2 = radius * radius;
            foreach (var a in all)
            {
                if (a == null) continue;
                Vector3 d = a.transform.position - point; d.y = 0f;
                if (d.sqrMagnitude > r2) continue;
                // its look's renderers, found once when the look was bound (no search per door per frame; review2 #22)
                var shown = a.renderers.Length > 0 ? a.renderers : a.GetComponentsInChildren<Renderer>(false);
                foreach (var r in shown) if (r != null && r.enabled) return true;
            }
            return false;
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying || controller == null) return;
            if (!BindShownLook()) return;
            // hidden (e.g. a patient at home drawn as a count): no animation work
            bool visible = false;
            foreach (var r in renderers) if (r != null && r.enabled) { visible = true; break; }
            if (animator.enabled != visible) animator.enabled = visible;
            if (!visible) { hasShown = walking = false; rest.Clear(); return; }   // shown again: where the model puts it

            // speed from the motion of this object (members move along their legs; items are placed by the layouts)
            Vector3 p = transform.position;
            float dt = Time.deltaTime;
            if (hasLast && dt > 0)
            {
                Vector3 d = p - lastPosition; d.y = 0;
                speed = Mathf.Lerp(speed, d.magnitude / dt, 0.3f);
            }
            lastPosition = p; hasLast = true;

            var ex = mover != null ? mover.Executor : null;
            var sim = UnitySimClock.Instance;
            bool paused = sim != null && sim.SimPaused;
            bool driven = drivenFrame == Time.frameCount;
            if (driven) { shown = target = p; targetRotation = transform.rotation; hasShown = true; walking = false; rest.Clear(); }   // a model walk
            else if (mover == null) WalkToPlace(paused ? 0f : dt);
            bool moving = ex != null ? (ex.CurrentLeg != null || mover.Stepping) && speed > 0.05f : driven ? drivenMoving && speed > 0.05f : walking;
            var step = ex?.CurrentStep;
            bool working = ex != null && ex.CurrentLeg == null && step != null && (step.Kind == StepKind.Process || step.Kind == StepKind.Hold);
            bool carrying = ex != null && ex.Carried.Count > 0 && !working;   // a station's item held while serving is not carried (B7)
            bool handing = ex != null && ex.CurrentLeg == null && step != null && (step.Kind == StepKind.Load || step.Kind == StepKind.Unload);
            Work? work = working ? WorkAt(ModelName(ex.CurrentTask?.Requester)) : (Work?)null;
            string clip = ClipFor(walking ? Pose.Stand : pose, moving, carrying, pushes, work, handing && !pushes, (Hash & 1u) == 1u);

            // (re)issue the clip until the Animator is in it: a request in the frame the Animator was added is lost
            var now = animator.GetCurrentAnimatorStateInfo(0);
            if (clip != State || (!animator.IsInTransition(0) && !now.IsName(clip)))
            {
                if (State == null || !now.IsName(State)) animator.Play(clip, 0, Random01());
                else animator.CrossFadeInFixedTime(clip, fade);
                State = clip;
            }
            float pace = mover == null && !driven ? walkSpeed : speed;
            animator.speed = paused ? 0f : moving ? Mathf.Clamp(pace / walkCycleMetres, 0.3f, 3f) : 1f;
            if (mover != null) mover.ProceduralLimbs = false;
        }

        // An item placed somewhere else by the model (a layout, a station) walks there from where it is shown, straight,
        // in at most maxWalkSeconds; small moves (a queue shuffling) and far ones (appearing) are taken at once.
        private void WalkToPlace(float dt)
        {
            Vector3 p = transform.position;
            if (!hasShown) { shown = target = p; targetRotation = transform.rotation; hasShown = true; return; }
            if ((p - shown).sqrMagnitude > 1e-8f && (p - target).sqrMagnitude > 1e-4f)
            {
                target = p;
                targetRotation = transform.rotation;
                // the rest of the model walk, then through the station's entry (a box's door), on the floor
                path.Clear();
                bool alongRoute = rest.Count > 0;
                foreach (var v in rest) path.Add(new Vector3(v.x, Mathf.Min(shown.y, target.y), v.z));
                rest.Clear();
                foreach (var v in via) path.Add(new Vector3(v.x, Mathf.Min(shown.y, target.y), v.z));
                via.Clear();
                path.Add(target);
                float dist = 0f;
                Vector3 from = shown;
                foreach (var q in path) { Vector3 d = q - from; d.y = 0; dist += d.magnitude; from = q; }
                walking = walkBetweenPlaces && dist > 0.6f && (alongRoute || dist < maxWalkDistance);
                if (walking) walkSpeed = Mathf.Max(1.4f, dist / maxWalkSeconds);
                else { shown = target; path.Clear(); }
            }
            if (walking)
            {
                Vector3 goal = path.Count > 0 ? path[0] : target;
                Vector3 next = Vector3.MoveTowards(shown, goal, walkSpeed * dt);
                Vector3 dir = goal - shown; dir.y = 0;
                if (dir.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
                shown = next;
                if ((shown - goal).sqrMagnitude < 1e-6f)
                {
                    if (path.Count > 0) path.RemoveAt(0);
                    if (path.Count == 0) { walking = false; transform.rotation = targetRotation; }
                }
            }
            else shown = target;
            transform.position = shown;
        }

        // the Animator on the armature of the look shown (its child "Root" is where the clips' paths start)
        private bool BindShownLook()
        {
            if (armature != null && armature.gameObject.activeInHierarchy) return animator != null;
            armature = null;
            // no rig found last time: look again only when the hierarchy changed (a look applied), not every frame
            if (unboundHierarchy == transform.hierarchyCount) return false;
            foreach (var t in GetComponentsInChildren<Transform>(false))
                if (t.name == "Root" && t.parent != null && t.parent != transform) { armature = t.parent; break; }
            if (armature == null) { unboundHierarchy = transform.hierarchyCount; return false; }
            unboundHierarchy = -1;
            animator = armature.GetComponent<Animator>();
            if (animator == null) animator = armature.gameObject.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            // the look's meshes are siblings of the armature, not below it: culling by renderers would never update it
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            renderers = GetComponentsInChildren<Renderer>(false);
            animator.Rebind();
            State = null;
            return true;
        }

        // per instance: the idle variant and the start of the loop (a waiting line does not move in unison)
        private uint Hash => hash ?? (hash = (uint)UnityCompat.ObjectId(gameObject).GetHashCode()).Value;   // a string per frame before
        private uint? hash;
        private int unboundHierarchy = -1;
        private float Random01() => ((Hash * 2654435761u) % 1000u) / 1000f;

        // the name of the model a station shows (its StationModel's choice, else its GameObject)
        private static string ModelName(Element station)
        {
            if (!(station?.vElement is Component c) || c == null) return null;
            var sm = c.GetComponent<StationModel>();
            var chosen = sm != null ? sm.Chosen : null;
            return chosen != null ? chosen.name : c.name;
        }
    }
}
