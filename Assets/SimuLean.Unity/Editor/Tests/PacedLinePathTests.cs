using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Paced line view along a path: pitches along the polyline through the waypoints (sharp and rounded
    /// corners), vehicles facing along it; a straight line along local +X without a path; fitted parts shown at the vehicle's socket.
    /// </summary>
    public class PacedLinePathTests
    {
        private static void Near(Vector3 a, Vector3 b, float tol, string what) =>
            Assert.That(Vector3.Distance(a, b), Is.LessThan(tol), $"{what}: expected {b}, got {a}");

        private static UnityPacedLine Line(GameObject root, float radius, params Vector3[] waypoints)
        {
            var line = root.AddComponent<UnityPacedLine>();
            line.positions = 12;
            line.pitchLength = 2f;
            line.cornerRadius = radius;
            line.path = new List<Transform>();
            foreach (var w in waypoints)
            {
                var t = new GameObject("wp").transform;
                t.SetParent(root.transform, false);
                t.position = w;
                line.path.Add(t);
            }
            return line;
        }

        [Test]
        public void PitchesFollowAPolylineWithSharpCorners()
        {
            var root = new GameObject("Line");
            try
            {
                var line = Line(root, 0f, Vector3.zero, new Vector3(0, 0, 10), new Vector3(10, 0, 10));
                Near(line.PitchWorld(0), Vector3.zero, 1e-4f, "pitch 0 at the first waypoint");
                Near(line.PitchWorld(5), new Vector3(0, 0, 10), 1e-4f, "pitch 5 at the corner (10 m)");
                Near(line.PitchWorld(6), new Vector3(2, 0, 10), 1e-4f, "pitch 6 two metres past the corner");
                line.PitchPose(7, out Quaternion r);
                Near(r * Vector3.forward, Vector3.right, 1e-4f, "facing along the second leg");
                line.PitchPose(2, out r);
                Near(r * Vector3.forward, Vector3.forward, 1e-4f, "facing along the first leg");
                Near(line.PitchWorld(11), new Vector3(12, 0, 10), 1e-4f, "past the end it continues straight");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void RoundedCornersMakeAUTurn()
        {
            var root = new GameObject("Line");
            try
            {
                // +X 10 m, U-turn of radius 4 (two corners), back along −X
                var line = Line(root, 4f, Vector3.zero, new Vector3(14, 0, 0), new Vector3(14, 0, 8), new Vector3(0, 0, 8));
                var path = line.Path;
                Assert.IsNotNull(path);
                Assert.That(path.Length, Is.EqualTo(10f + Mathf.PI * 4f + 10f).Within(0.05f), "two straights and a half circle");
                Near(line.PoseAt(10f, out _), new Vector3(10, 0, 0), 1e-3f, "the turn starts 4 m before the corner");
                Vector3 mid = line.PoseAt(10f + Mathf.PI * 2f, out Quaternion r);
                Near(mid, new Vector3(14, 0, 4), 0.05f, "half way round: on the circle, at its far side");
                Near(r * Vector3.forward, Vector3.forward, 0.15f, "half way round the vehicle faces +Z (within one arc segment)");
                Near(line.PoseAt(path.Length, out r), new Vector3(0, 0, 8), 1e-3f, "the end");
                Near(r * Vector3.forward, Vector3.left, 1e-3f, "facing back along −X");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void WithoutAPathTheLineIsStraightAlongLocalX()
        {
            var root = new GameObject("Line");
            try
            {
                // the model frame: the line runs along its +X; turned −90 its +X is the world's +Z
                root.transform.SetPositionAndRotation(new Vector3(1, 0, 2), Quaternion.Euler(0, -90, 0));
                var line = root.AddComponent<UnityPacedLine>();
                line.pitchLength = 3f;
                Near(line.PitchWorld(2), new Vector3(1, 0, 8), 1e-4f, "two pitches along the rotated +X (= +Z)");
                line.PitchPose(2, out Quaternion r);
                Near(r * Vector3.forward, root.transform.right, 1e-4f, "vehicles face along the line (their +Z on its +X)");
                Near(r * Vector3.right, -root.transform.forward, 1e-4f, "the line side, right of the travel, is the line's −Z (its front)");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void FittedPartsAreShownAtTheVehicleSocket()
        {
            var vehicle = new GameObject("Vehicle");
            var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var socket = new GameObject("ConsoleSocket").transform;
                socket.SetParent(new GameObject("Body").transform, false);
                socket.parent.SetParent(vehicle.transform, false);
                socket.localPosition = new Vector3(0, 0.4f, 0);
                var part1 = new GameObject("p1");
                var part2 = new GameObject("p2");

                // own views at the socket
                var plain = new LineStationDef { name = "Plain", vehicleSocket = "ConsoleSocket" };
                Assert.IsNull(UnityPacedLine.ShowFitted(vehicle, plain, new List<GameObject> { part1 }));
                Assert.AreEqual(socket, part1.transform.parent);
                Near(part1.transform.position, socket.position, 1e-5f, "part at the socket");

                // a fitted model replaces the parts' views, once per vehicle and station
                var def = new LineStationDef { name = "Console", vehicleSocket = "ConsoleSocket", fittedPrefab = prefab };
                var shown = UnityPacedLine.ShowFitted(vehicle, def, new List<GameObject> { part2 });
                Assert.IsNotNull(shown);
                Assert.AreEqual(socket, shown.transform.parent);
                Assert.AreEqual("Fitted_Console", shown.name);
                Assert.IsTrue(part2 == null, "the part's own view is removed");
                var again = UnityPacedLine.ShowFitted(vehicle, def, new List<GameObject>());
                Assert.AreEqual(shown, again, "a second part of the same station shows no second model");
            }
            finally
            {
                Object.DestroyImmediate(vehicle);
                Object.DestroyImmediate(prefab);
            }
        }
    }
}
