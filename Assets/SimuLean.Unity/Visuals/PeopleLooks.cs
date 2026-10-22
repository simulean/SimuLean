using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The catalog of the library's people looks (<c>Prefabs/ResourceMembers/PeopleLooks.asset</c>): per role — operator,
    /// doctor, nurse, patient, fitter, welder, painter, outfitter… — its male and female models (the same height and bone
    /// contract, <c>Tools/Blender/assets/operator.py</c>) and whether the hard hat is worn by default. An
    /// <see cref="OperatorAppearance"/> shows the look its <see cref="OperatorAppearance.look"/> names, so a person in the
    /// library is one element (the operator) and a role is a field — the Doctor, Fitter… prefabs are variants of the
    /// operator that only set it. Add your own looks here (any model on the operator's bones).
    /// </summary>
    [CreateAssetMenu(menuName = "SimuLean/People Looks", fileName = "PeopleLooks")]
    public class PeopleLooks : ScriptableObject
    {
        [Serializable]
        public class Look
        {
            public string name = "";
            public GameObject male;
            public GameObject female;
            [Tooltip("Wear the hard hat by default (the person's own setting decides).")]
            public bool helmet = true;
        }

        public List<Look> looks = new List<Look>();

        /// <summary>The look named <paramref name="name"/> (case-insensitive), else null.</summary>
        public Look Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var l in looks)
                if (l != null && string.Equals(l.name, name, StringComparison.OrdinalIgnoreCase)) return l;
            return null;
        }

        public IEnumerable<string> Names()
        {
            foreach (var l in looks) if (l != null && !string.IsNullOrEmpty(l.name)) yield return l.name;
        }
    }
}
