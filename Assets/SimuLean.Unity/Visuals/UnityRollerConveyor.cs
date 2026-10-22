using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Parametric roller-conveyor visual (geometry: <see cref="RollerConveyorMesh"/>). The component's own
    /// MeshFilter holds the full-detail mesh (LOD0): two side frames (C-channel by default) + N smooth-shaded
    /// rollers with tapered ends and axle stubs + optional legs with foot plates, cross ties and diagonal
    /// braces + optional drive (chain guard and gear motor), sized to <see cref="length"/>. The roller count
    /// adapts automatically: <c>rollers = round(length / rollerPitch)</c>, and the rollers are evenly
    /// distributed so the last one lands exactly at the end — so a conveyor can be any length with correctly
    /// spaced, never-deformed rollers. With <see cref="generateLod"/> a child <c>LOD1</c> holds a cheap
    /// version (rollers merged into one striped bed quad) under a LODGroup on this object.
    ///
    /// <para><b>Sizing rule — never scale this object.</b> Non-uniform scale
    /// would turn the round rollers into ovals. Instead set <see cref="length"/>
    /// (and the other pitch/size parameters) and run <see cref="Rebuild"/>
    /// (Inspector button, context menu, or the GameObject → SimuLean →
    /// Transport menu). To drive it from a "draw a conveyor" tool, set
    /// <see cref="length"/> at runtime and call <see cref="Rebuild"/>.</para>
    ///
    /// <para>Local axes: <b>+Z is the run/flow direction</b> (length), +X is the
    /// width, +Y is up. The conveying surface (roller tops) is at world/local
    /// Y = <see cref="bedHeight"/>. The generated meshes use two sub-meshes —
    /// 0 = frame + legs (<see cref="frameMaterial"/>), 1 = rollers
    /// (<see cref="rollerMaterial"/>; at LOD1 the striped bed, <see cref="bedMaterial"/>).</para>
    ///
    /// <para>The LODGroup uses a fixed reference size (<see cref="RollerConveyorMesh.LodReferenceSize"/>), so
    /// it switches when the rollers get small on screen, not when the whole run does; the distance is
    /// measured to the centre of the conveyor, so very long runs are best split into several conveyors
    /// (≈ 10 m) placed end to end (rollers continue the same lattice across the joints).</para>
    ///
    /// Purely visual: this component does not participate in the DES engine,
    /// serialization, or headless runs. Rollers are separate geometry in the
    /// mesh; to animate them spinning, drive a shader or replace this with a
    /// per-roller variant — the single-mesh form here is the efficient default.
    /// </summary>
    [SelectionBase]
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class UnityRollerConveyor : MonoBehaviour
    {
        [Header("Conveyor size (m)")]
        [Tooltip("Length along local +Z (the run/flow direction). Roller count adapts to this.")]
        [Min(0.1f)] public float length = 3.0f;

        [Tooltip("Overall width along local +X (outer frame to outer frame).")]
        [Min(0.1f)] public float width = 0.6f;

        [Tooltip("Height of the conveying surface (roller tops) above the floor.")]
        [Min(0.05f)] public float bedHeight = 0.5f;

        [Header("Rollers")]
        [Tooltip("Target spacing between roller centres. The actual pitch is adjusted " +
                 "slightly so a whole number of rollers fills the length exactly.")]
        [Min(0.02f)] public float rollerPitch = 0.15f;

        [Tooltip("Roller radius (industrial rollers: 25–45 mm, i.e. 50–90 mm diameter).")]
        [Min(0.005f)] public float rollerRadius = 0.03f;

        [Tooltip("Sides of each roller (smooth shaded; 10–12 look round).")]
        [Range(5, 32)] public int rollerSides = 12;

        [Tooltip("Axle stubs through the frame web, visible in the C-channel (C-channel frame only).")]
        public bool rollerStubs = true;

        [Header("Frame")]
        [Tooltip("Side-frame cross-section: folded C-channel (web towards the rollers) or the old solid bar.")]
        public RollerFrameProfile frameProfile = RollerFrameProfile.CChannel;

        [Tooltip("Depth of each of the two side frames across the width (C-channel flange width).")]
        [Min(0.01f)] public float railWidth = 0.04f;

        [Tooltip("Height of the side frames (the channel the rollers sit in).")]
        [Min(0.02f)] public float frameHeight = 0.12f;

        [Tooltip("Sheet thickness of the C-channel.")]
        [Min(0.001f)] public float frameThickness = 0.005f;

        [Tooltip("Frame top below the roller tops, so the load clears the frame.")]
        [Min(0f)] public float frameTopGap = 0.015f;

        [Header("Legs")]
        [Tooltip("Generate support legs at both ends and at intervals.")]
        public bool generateLegs = true;

        [Tooltip("Maximum spacing between leg stands. Legs are always placed at both ends.")]
        [Min(0.3f)] public float legSpacing = 2.0f;

        [Tooltip("Square-tube size of the legs.")]
        [Min(0.015f)] public float legSize = 0.05f;

        [Tooltip("Foot plates under the legs.")]
        public bool legFeet = true;

        [Tooltip("Low transverse tie between the two legs of every stand.")]
        public bool crossBraces = true;

        [Tooltip("Diagonal brace in both side planes every N bays (0 = none).")]
        [Min(0)] public int diagonalBraceEvery = 3;

        [Header("Drive")]
        [Tooltip("Side with the chain guard and the gear motor (None = no drive shown).")]
        public RollerDriveSide driveSide = RollerDriveSide.None;

        [Header("LOD")]
        [Tooltip("Add a cheap LOD1 (rollers merged into one striped bed quad) under a LODGroup.")]
        public bool generateLod = true;

        [Tooltip("Relative screen height of a 3 m reference below which LOD1 is drawn.")]
        [Range(0.01f, 0.5f)] public float lodTransition = 0.10f;

        [Header("Materials (optional — defaults created if empty)")]
        public Material frameMaterial;
        public Material rollerMaterial;
        [Tooltip("LOD1 roller bed (striped texture, see RollerConveyorMesh.CreateBedTexture).")]
        public Material bedMaterial;

        private static Material _defaultFrameMat;
        private static Material _defaultRollerMat;
        // one default bed per stripe ratio: the stripes follow the roller pitch (one shared material
        // gave every conveyor the stripes of the first one built)
        private static readonly System.Collections.Generic.Dictionary<int, Material> _defaultBedMats =
            new System.Collections.Generic.Dictionary<int, Material>();
        private static Texture2D _paintTex;

        private const string Lod0Name = "RollerConveyor", Lod1Name = "RollerConveyor_LOD1", Lod1Child = "LOD1";

        /// <summary>Number of rollers the current length/pitch produces.</summary>
        public int RollerCount => Mathf.Max(1, Mathf.RoundToInt(length / rollerPitch));

        /// <summary>One-line summary for the custom editor.</summary>
        public string GetSummary()
        {
            float actual = length / RollerCount;
            string tris = "";
            var mf = GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                int t0 = RollerConveyorMesh.TriangleCount(mf.sharedMesh);
                var l1 = transform.Find(Lod1Child);
                int t1 = l1 != null && l1.TryGetComponent(out MeshFilter f1) ? RollerConveyorMesh.TriangleCount(f1.sharedMesh) : -1;
                tris = $"\nTris LOD0 {t0}" + (t1 >= 0 ? $" / LOD1 {t1}" : "") + $"  ({t0 / Mathf.Max(0.1f, length):0} / m at LOD0)";
            }
            return $"{length:0.##} m × {width:0.##} m  |  {RollerCount} rollers Ø{rollerRadius * 2000f:0} mm @ {actual:0.###} m pitch" +
                   (generateLegs ? $"  |  legs every ≤{legSpacing:0.##} m" : "  |  no legs") + tris;
        }

        private void Reset()
        {
            Rebuild();
        }

        /// <summary>
        /// Fits the rollers under a lane of a length-limited queue (the Conveyor preset): <paramref name="laneLength"/>
        /// long, from the lane's exit (its origin) backwards, at <paramref name="x"/> across. Rebuilt only when something
        /// changed. Visual only.
        /// </summary>
        public void FitTo(float laneLength, float x)
        {
            laneLength = Mathf.Max(0.1f, laneLength);
            var at = new Vector3(x, 0f, -laneLength);
            if (Mathf.Approximately(length, laneLength) && transform.localPosition == at && transform.localRotation == Quaternion.identity
                && GetComponent<MeshFilter>() != null && GetComponent<MeshFilter>().sharedMesh != null) return;
            length = laneLength;
            transform.localPosition = at;
            transform.localRotation = Quaternion.identity;
            Rebuild();
        }

        /// <summary>The generation parameters of this component as a <see cref="RollerConveyorSpec"/>.</summary>
        public RollerConveyorSpec ToSpec() => new RollerConveyorSpec
        {
            length = length, width = width, bedHeight = bedHeight, centred = false,
            rollerPitch = rollerPitch, rollerRadius = rollerRadius, rollerSides = rollerSides, rollerStubs = rollerStubs,
            frameProfile = frameProfile, frameDepth = railWidth, frameHeight = frameHeight,
            frameThickness = frameThickness, frameTopGap = frameTopGap,
            legs = generateLegs, legSpacing = legSpacing, legSize = legSize, legFeet = legFeet,
            crossBraces = crossBraces, diagonalBraceEvery = diagonalBraceEvery, driveSide = driveSide,
        };

        // ── generation ───────────────────────────────────────────────────────

        [ContextMenu("Rebuild Conveyor")]
        public void Rebuild()
        {
            var spec = ToSpec();

            var mf = GetComponent<MeshFilter>();
            mf.sharedMesh = RollerConveyorMesh.Build(spec, 0, Reuse(mf.sharedMesh, Lod0Name), Lod0Name);
            var mr = GetComponent<MeshRenderer>();
            mr.sharedMaterials = new[] { ResolveFrameMat(), ResolveRollerMat() };

            Transform child = transform.Find(Lod1Child);
            if (generateLod)
            {
                if (child == null)
                {
                    child = new GameObject(Lod1Child).transform;
                    child.SetParent(transform, false);
                }
                child.gameObject.isStatic = gameObject.isStatic;
                if (!child.TryGetComponent(out MeshFilter f1)) f1 = child.gameObject.AddComponent<MeshFilter>();
                if (!child.TryGetComponent(out MeshRenderer r1)) r1 = child.gameObject.AddComponent<MeshRenderer>();
                f1.sharedMesh = RollerConveyorMesh.Build(spec, 1, Reuse(f1.sharedMesh, Lod1Name), Lod1Name);
                r1.sharedMaterials = new[] { ResolveFrameMat(), ResolveBedMat() };
                RollerConveyorMesh.SetupLodGroup(gameObject, mr, r1, lodTransition);
            }
            else
            {
                if (TryGetComponent(out LODGroup group)) DestroySafe(group);
                if (child != null) DestroySafe(child.gameObject);
            }
        }

        private static Mesh Reuse(Mesh m, string meshName) => m != null && m.name == meshName ? m : null;

        private static void DestroySafe(Object o)
        {
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        // ── default materials ────────────────────────────────────────────────

        private static Texture2D PaintTex => _paintTex != null ? _paintTex : (_paintTex = RollerConveyorMesh.CreatePaintTexture());

        private Material ResolveFrameMat()
        {
            if (frameMaterial != null) return frameMaterial;
            if (_defaultFrameMat == null)
                _defaultFrameMat = RollerConveyorMesh.CreateMaterial("ConveyorFrame(default)", new Color(0.13f, 0.16f, 0.20f), 0.15f, 0.45f, PaintTex);
            return _defaultFrameMat;
        }

        private Material ResolveRollerMat()
        {
            if (rollerMaterial != null) return rollerMaterial;
            if (_defaultRollerMat == null)
                _defaultRollerMat = RollerConveyorMesh.CreateMaterial("ConveyorRoller(default)", new Color(0.60f, 0.62f, 0.65f), 0.85f, 0.55f, PaintTex);
            return _defaultRollerMat;
        }

        private Material ResolveBedMat()
        {
            if (bedMaterial != null) return bedMaterial;
            float ratio = 2f * rollerRadius / Mathf.Max(0.02f, rollerPitch);
            int key = Mathf.RoundToInt(ratio * 1000f);
            if (!_defaultBedMats.TryGetValue(key, out var mat) || mat == null)
                _defaultBedMats[key] = mat = RollerConveyorMesh.CreateMaterial("ConveyorRollerBed(default)", new Color(0.62f, 0.64f, 0.67f), 0.2f, 0.3f,
                    RollerConveyorMesh.CreateBedTexture(ratio));
            return mat;
        }

        // ── configured-size preview (before/after rebuild) ───────────────────

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.5f);
            Vector3 size = new Vector3(width, frameHeight, length);
            Vector3 center = transform.position + transform.rotation *
                new Vector3(0f, bedHeight - frameHeight * 0.5f, length * 0.5f);
            Gizmos.matrix = Matrix4x4.TRS(center, transform.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, size);
        }
    }
}
