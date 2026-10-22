using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The look of the library's people: one person element (<c>Prefabs/ResourceMembers/Operator</c>) whose role is a
    /// field — <see cref="look"/> names an entry of the <see cref="catalog"/> (<see cref="PeopleLooks"/>: operator, doctor,
    /// nurse, patient, fitter, welder, painter, outfitter…) — so the Doctor, Nurse, Fitter… prefabs are variants of the
    /// operator that only set it (the patient item and the porters' and pallet truck's drivers use the same component).
    /// Each look comes male and female — the same height and joint names, so <see cref="UnityVehicleAnimator"/> walks and
    /// carries both alike — and with the hard hat on or off, as the operator gender in FlexSim.
    /// <para>The look's models are built as children when the object is enabled (a member spawned by its team, an item,
    /// a scene object) and rebuilt when <see cref="look"/> changes; in the editor they are not saved (nothing is dirtied
    /// by opening a scene or a prefab). The look not chosen is deactivated. <see cref="Gender.Mixed"/> alternates by
    /// member: <see cref="UnityResourceTeam"/> calls <see cref="ForMember"/> when it spawns a member; an item look
    /// alternates by instance. Without a catalog the children are the looks (the male parts, and
    /// <see cref="femaleModel"/>) — a hand-made person. View only.</para>
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("SimuLean/Visuals/Operator Appearance")]
    public class OperatorAppearance : MonoBehaviour
    {
        public enum Gender { Male, Female, Mixed }

        [Tooltip("The catalog of looks (Prefabs/ResourceMembers/PeopleLooks); none = this object's children are the looks.")]
        public PeopleLooks catalog;
        [Tooltip("The role's look, from the catalog (Operator, Doctor, Nurse, Patient, Fitter, Welder, Painter, Outfitter…).")]
        public string look = "Operator";
        [Tooltip("Male, female, or mixed (members alternate: even = male, odd = female).")]
        public Gender gender = Gender.Male;
        [Tooltip("Wear the hard hat.")]
        public bool helmet = true;
        [Tooltip("Without a catalog: the female look (a child); every other child is part of the male look.")]
        public GameObject femaleModel;

        private int memberIndex;
        private bool indexed;
        private string builtLook;
        private GameObject builtMale;
        private static int itemCounter;

        /// <summary>The look shown: <see cref="gender"/>, or for Mixed the member's (even = male).</summary>
        public Gender Shown => gender != Gender.Mixed ? gender : (memberIndex % 2 == 0 ? Gender.Male : Gender.Female);

        /// <summary>The catalog entry shown (null without a catalog or for an unknown name).</summary>
        public PeopleLooks.Look CurrentLook => catalog != null ? catalog.Find(look) : null;

        /// <summary>Called by the team for its <paramref name="index"/>-th member (Mixed alternates).</summary>
        public void ForMember(int index)
        {
            memberIndex = index;
            indexed = true;
            Apply();
        }

        /// <summary>Shows another look (rebuilds the models).</summary>
        public void SetLook(string name)
        {
            look = name;
            Build();
            Apply();
        }

        private void OnEnable()
        {
            Build();
            Apply();
        }

        // an item look (e.g. the hospital's patient) is not a team member: Mixed alternates by instance
        private void Start()
        {
            if (!Application.isPlaying || indexed || gender != Gender.Mixed) return;
            ForMember(itemCounter++);
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnEnterPlayMode]
        private static void ResetCounter() => itemCounter = 0;
#endif

        private void OnValidate()
        {
#if UNITY_EDITOR
            // SetActive / hierarchy changes are not allowed inside OnValidate
            UnityEditor.EditorApplication.delayCall += () => { if (this != null && isActiveAndEnabled) { Build(); Apply(); } };
#endif
        }

        /// <summary>Builds the catalog look's male and female models as children (once per look).</summary>
        public void Build()
        {
            var l = CurrentLook;
            if (l == null || l.male == null) return;
            if (builtLook == look && builtMale != null) return;
#if UNITY_EDITOR
            // a prefab asset (not an instance in a scene or a prefab stage) is never edited
            if (!Application.isPlaying && UnityEditor.EditorUtility.IsPersistent(this)) return;
#endif
            Clear();
            builtMale = Spawn(l.male, look + "_Male");
            femaleModel = l.female != null ? Spawn(l.female, look + "_Female") : null;
            builtLook = look;
        }

        private GameObject Spawn(GameObject model, string name)
        {
            var go = Instantiate(model, transform, false);
            go.name = name;
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            if (!Application.isPlaying)
                foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.DontSave;
            return go;
        }

        // the models built before (in the editor they are not saved: they come back on enable)
        private void Clear()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var c = transform.GetChild(i).gameObject;
                bool built = c == builtMale || c == femaleModel || (c.hideFlags & HideFlags.DontSave) != 0;
                if (!built) continue;
                if (Application.isPlaying) { c.SetActive(false); Destroy(c); }
                else DestroyImmediate(c);
            }
            builtMale = null;
        }

        /// <summary>Shows the chosen look and hat.</summary>
        public void Apply()
        {
            bool female = Shown == Gender.Female && femaleModel != null;
            foreach (Transform c in transform)
            {
                if (catalog != null && c.gameObject != builtMale && c.gameObject != femaleModel) continue;   // sockets etc.
                bool isFemale = femaleModel != null && c.gameObject == femaleModel;
                bool on = female ? isFemale : !isFemale;
                if (c.gameObject.activeSelf != on) c.gameObject.SetActive(on);
            }
            SetHelmet(transform);
        }

        private void SetHelmet(Transform t)
        {
            foreach (Transform c in t)
            {
                if (c.name == "Helmet")
                {
                    if (c.gameObject.activeSelf != helmet) c.gameObject.SetActive(helmet);
                    continue;
                }
                SetHelmet(c);
            }
        }
    }
}
