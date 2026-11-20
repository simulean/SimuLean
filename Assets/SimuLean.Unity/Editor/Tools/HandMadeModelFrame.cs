using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The library's model frame for the hand-made models a station or element model has its front
    /// (operator side, panel, stack light, screens) towards local −Z and its flow along local +X (in on −X, out on +X) —
    /// what the Blender scripts export (<c>Tools/Blender/simulean_kit.export_fbx</c>, <c>turn</c>). The few models under
    /// <c>Art/Models/</c> that come from no script were authored in another frame; they are turned into it on import,
    /// about the vertical through their origin: the root's own mesh (its vertices) and the root's children (their poses).
    /// The root keeps its own rotation (a Z-up import turned about X). Every other model is untouched.
    /// </summary>
    public class HandMadeModelFrame : AssetPostprocessor
    {
        /// <summary>The hand-made models turned on import: asset path → turn about the vertical (degrees, Unity yaw).</summary>
        public static readonly IReadOnlyDictionary<string, float> Turns = new Dictionary<string, float>
        {
            // a gate: items appear under it and leave along its −Z → flow +X
            { LibraryPresets.Models + "SourceStation/SourceStation.fbx", -90f },
            { LibraryPresets.Models + "SinkStation/SinkStation.fbx", -90f },
            // work tables / the classic workstation: the operator's side (panel, screen, andon) was +Z
            { LibraryPresets.Models + "AssemblyWorkstation/AssemblyWorkstation.fbx", 180f },
            { LibraryPresets.Models + "WorkstationProcessor/WorkstationProcessor.fbx", 180f },
        };

        public override uint GetVersion() => 1;

        private void OnPostprocessModel(GameObject root)
        {
            if (!Turns.TryGetValue(assetPath.Replace('\\', '/'), out float yaw)) return;
            Turn(root.transform, yaw);
        }

        /// <summary>Turns the content of <paramref name="root"/> by <paramref name="yaw"/> about the vertical, its own
        /// transform unchanged.</summary>
        internal static void Turn(Transform root, float yaw)
        {
            // the turn in the root's own axes (its rotation may lay a Z-up model down)
            Quaternion q = Quaternion.Inverse(root.localRotation) * Quaternion.Euler(0f, yaw, 0f) * root.localRotation;
            var filter = root.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null) TurnMesh(filter.sharedMesh, q);
            foreach (Transform child in root)
            {
                child.localPosition = q * child.localPosition;
                child.localRotation = q * child.localRotation;
            }
        }

        private static void TurnMesh(Mesh mesh, Quaternion q)
        {
            var v = mesh.vertices;
            for (int i = 0; i < v.Length; i++) v[i] = q * v[i];
            mesh.vertices = v;
            var n = mesh.normals;
            if (n != null && n.Length == v.Length)
            {
                for (int i = 0; i < n.Length; i++) n[i] = q * n[i];
                mesh.normals = n;
            }
            var t = mesh.tangents;
            if (t != null && t.Length == v.Length)
            {
                for (int i = 0; i < t.Length; i++)
                {
                    Vector3 d = q * new Vector3(t[i].x, t[i].y, t[i].z);
                    t[i] = new Vector4(d.x, d.y, d.z, t[i].w);
                }
                mesh.tangents = t;
            }
            mesh.RecalculateBounds();
        }
    }
}
