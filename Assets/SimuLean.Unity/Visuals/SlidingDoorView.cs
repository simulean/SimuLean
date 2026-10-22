using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Opens a model's sliding doors (nodes <c>Door_L</c> / <c>Door_R</c>, e.g. the hospital door) when an item passes
    /// through its element (healthcare): an item leaving a source (a patient coming in) or entering a sink
    /// (a patient going home) opens them for <see cref="holdOpen"/> seconds. Each leaf slides away from the centre by
    /// <see cref="openDistance"/>. A view: it listens to the element's item events and moves nothing else.
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Sliding Door View")]
    public sealed class SlidingDoorView : MonoBehaviour
    {
        [Tooltip("How far each leaf slides open (m).")]
        [Min(0f)] public float openDistance = 0.9f;
        [Tooltip("How long the doors stay open after an item passes (real seconds).")]
        [Min(0f)] public float holdOpen = 1.5f;
        [Tooltip("Opening / closing speed (m/s).")]
        [Min(0.01f)] public float speed = 1.2f;
        [Tooltip("Also opens when a person (a walking patient, a member) comes this close (m, on the floor); 0 = only when an " +
                 "item passes through its element. A door with no element (a building's entrance) opens this way.")]
        [Min(0f)] public float openWhenNear = 2.5f;

        private Transform left, right;
        private Vector3 leftRest, rightRest;
        private Element bound;
        private SElement owner;   // looked up once, not every frame
        private float openUntil = -1f, amount;

        /// <summary>How open the doors are (0 closed – 1 open).</summary>
        public float Amount => amount;

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;
            if (left == null && right == null && !FindLeaves()) return;
            if (owner == null) owner = GetComponent<SElement>();
            var element = owner != null ? owner.GetElement() : null;
            if (element != bound)
            {
                if (bound != null) { bound.ItemEntered -= Passed; bound.ItemExited -= Passed; }
                bound = element;
                if (bound != null) { bound.ItemEntered += Passed; bound.ItemExited += Passed; }
            }
            if (openWhenNear > 0f && PersonAnimator.AnyVisibleNear(transform.position, openWhenNear)) openUntil = Time.time + holdOpen;
            float target = Time.time < openUntil ? 1f : 0f;
            amount = Mathf.MoveTowards(amount, target, Time.deltaTime * speed / Mathf.Max(0.01f, openDistance));
            float d = Mathf.SmoothStep(0f, 1f, amount) * openDistance;
            if (left != null) left.localPosition = leftRest + Away(leftRest) * d;
            if (right != null) right.localPosition = rightRest + Away(rightRest) * d;
        }

        private void Passed(Element e, Item item) => openUntil = Time.time + holdOpen;

        private int leaflessHierarchy = -1;

        /// <summary>Opens the doors now (as if an item passed).</summary>
        public void Open() => openUntil = Time.time + holdOpen;

        private bool FindLeaves()
        {
            // no leaves last time: search again only when the hierarchy changed (a model swapped), not every frame (review2 #22)
            if (leaflessHierarchy == transform.hierarchyCount) return false;
            leaflessHierarchy = transform.hierarchyCount;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Door_L") { left = t; leftRest = t.localPosition; }
                else if (t.name == "Door_R") { right = t; rightRest = t.localPosition; }
            }
            return left != null || right != null;
        }

        // a leaf slides away from the opening's centre along its parent's X
        private static Vector3 Away(Vector3 rest) => new Vector3(rest.x >= 0f ? 1f : -1f, 0f, 0f);

        private void OnDisable()
        {
            if (bound != null) { bound.ItemEntered -= Passed; bound.ItemExited -= Passed; bound = null; }
            if (left != null) left.localPosition = leftRest;
            if (right != null) right.localPosition = rightRest;
        }
    }
}
