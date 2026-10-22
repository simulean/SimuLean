using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Opens and closes a door of the IndustrialBuilding kit (Door_Sectional or
    /// Door_Personnel) by moving its <c>Leaf</c> node: the sectional leaf rises 3.9 m,
    /// the personnel leaf swings 90° outwards about its hinge. Added automatically by
    /// <see cref="UnityModularWall"/>; call <see cref="Open"/> / <see cref="Close"/> from
    /// simulation code (e.g. when a forklift approaches) or scrub
    /// <see cref="openAmount"/> in the inspector.
    /// </summary>
    public class UnityIndustrialDoor : MonoBehaviour
    {
        public enum DoorKind { Sectional, Personnel }

        public const float SectionalTravel = 3.9f;
        public const float PersonnelAngle = 90f;

        public DoorKind kind = DoorKind.Sectional;

        [Tooltip("0 = closed, 1 = fully open.")]
        [Range(0f, 1f)] public float openAmount;

        [Tooltip("Seconds for a full open or close.")]
        [Min(0.05f)] public float travelTime = 4f;

        [SerializeField, HideInInspector] Transform leaf;
        [SerializeField, HideInInspector] Vector3 restPosition;
        [SerializeField, HideInInspector] Quaternion restRotation = Quaternion.identity;
        [SerializeField, HideInInspector] bool hasRest;
        float target;

        public bool IsOpen => openAmount >= 0.999f;
        public bool IsClosed => openAmount <= 0.001f;
        public bool IsMoving => !Mathf.Approximately(openAmount, target);

        public void Open() => target = 1f;
        public void Close() => target = 0f;
        public void Toggle() => target = target > 0.5f ? 0f : 1f;

        /// <summary>Jumps to <paramref name="amount"/> (0..1) without animating.</summary>
        public void SetOpen(float amount)
        {
            openAmount = target = Mathf.Clamp01(amount);
            Apply();
        }

        void Awake()
        {
            target = openAmount;
            Apply();
        }

        void Update()
        {
            if (!IsMoving) return;
            openAmount = Mathf.MoveTowards(openAmount, target, Time.deltaTime / travelTime);
            Apply();
        }

        void OnValidate()
        {
            target = openAmount;
            Apply();
        }

        void Apply()
        {
            if (leaf == null)
            {
                leaf = transform.Find("Leaf");
                if (leaf == null) return;
                hasRest = false;
            }
            if (!hasRest)
            {
                restPosition = leaf.localPosition;
                restRotation = leaf.localRotation;
                hasRest = true;
            }
            if (kind == DoorKind.Sectional)
            {
                leaf.localPosition = restPosition + Vector3.up * (SectionalTravel * openAmount);
                leaf.localRotation = restRotation;
            }
            else
            {
                leaf.localPosition = restPosition;
                leaf.localRotation = restRotation * Quaternion.Euler(0f, PersonnelAngle * openAmount, 0f);
            }
        }
    }
}
