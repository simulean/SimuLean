using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A turning station's view: while a station (MultiServer) processes an item, the item rolls about its long
    /// horizontal axis by <see cref="angle"/> (a sub-block turned over for the welding of its other side), in step with
    /// the process time; it leaves turned. Put it on the station; the station keeps placing the item, this view only
    /// turns it. View only.
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Block Turn View")]
    public class BlockTurnView : MonoBehaviour
    {
        [Tooltip("Roll over the process (degrees).")]
        public float angle = 180f;

        private SElement element;
        private readonly Dictionary<Item, (Transform t, Quaternion start, Vector3 axis)> turning =
            new Dictionary<Item, (Transform, Quaternion, Vector3)>();
        private readonly List<Item> left = new List<Item>();

        private void Awake() => element = GetComponent<SElement>();

        private void LateUpdate()
        {
            if (element == null || UnitySimClock.Instance == null || !(element.GetElement() is MultiServer ms)) return;
            Turn(ms, UnitySimClock.Instance.SmoothSimTime);
        }

        /// <summary>Poses the station's items at sim time <paramref name="now"/> (the view's step; public for tests).</summary>
        public void Turn(MultiServer ms, float now)
        {
            left.Clear();
            left.AddRange(turning.Keys);
            foreach (var sp in ms.workInProgress)
            {
                var item = sp?.theItem;
                if (item == null || !(item.vItem is GameObject go) || go == null || sp.GetState() != ElementState.PROCESSING) continue;
                left.Remove(item);
                if (!turning.TryGetValue(item, out var s))
                {
                    var size = ItemVisuals.SizeOf(go);
                    Vector3 world = size.x >= size.z ? Vector3.right : Vector3.forward;   // the long horizontal axis
                    s = (go.transform, go.transform.rotation, go.transform.InverseTransformDirection(world));
                    turning[item] = s;
                }
                float p = sp.lastDelay > 0 ? Mathf.Clamp01((now - (float)sp.loadTime) / (float)sp.lastDelay) : 1f;
                go.transform.rotation = s.start * Quaternion.AngleAxis(angle * p, s.axis);
            }
            foreach (var item in left)                          // left the process: fully turned
            {
                var s = turning[item];
                if (s.t != null) s.t.rotation = s.start * Quaternion.AngleAxis(angle, s.axis);
                turning.Remove(item);
            }
        }
    }
}
