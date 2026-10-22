using NUnit.Framework;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>The Speed Profile and the Recorder: their rules, without Play mode.</summary>
    public class RecorderTests
    {
        /// <summary>The labels that face the viewer face the take's camera while a recorder records (they
        /// faced <c>Camera.main</c>, so a take saw their backs), and the main camera otherwise.</summary>
        [Test]
        public void LabelsFaceTheTakesCameraWhileRecording()
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                                                     | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var recGo = new GameObject("Recorder");
            var camGo = new GameObject("TakeCamera");
            var labelGo = new GameObject("Labelled");
            var current = typeof(SimRecorder).GetProperty("Current", any);
            try
            {
                var rec = recGo.AddComponent<SimRecorder>();
                var take = camGo.AddComponent<Camera>();
                take.enabled = false;
                camGo.transform.position = new Vector3(10, 20, -8);
                camGo.transform.rotation = Quaternion.Euler(60f, -40f, 0f);   // looking down, off to one side
                Assert.AreSame(Camera.main, SimRecorder.ViewCamera, "no take: the main camera");

                typeof(SimRecorder).GetField("cam", any).SetValue(rec, take);
                typeof(SimRecorder).GetProperty("Recording", any).SetValue(rec, true);
                current.SetValue(null, rec);
                Assert.AreSame(take, SimRecorder.ViewCamera, "recording: the take's camera");

                var label = labelGo.AddComponent<UnityElementLabel>();
                label.height = 0;
                typeof(UnityElementLabel).GetMethod("OnEnable", any).Invoke(label, null);   // EditMode: not called by itself
                var text = labelGo.GetComponentInChildren<TextMesh>().transform;
                // parallel to the take's view (turned to the camera's position, labels below a high
                // camera spun round): the take's forward and up
                Assert.Greater(Vector3.Dot(text.forward, camGo.transform.forward), 0.999f, "the label faces the take");
                Assert.Greater(Vector3.Dot(text.up, camGo.transform.up), 0.999f, "upright in the take");
                typeof(UnityElementLabel).GetMethod("OnDisable", any).Invoke(label, null);
            }
            finally
            {
                current.SetValue(null, null);
                var r = recGo.GetComponent<SimRecorder>();
                if (r != null) typeof(SimRecorder).GetProperty("Recording", any).SetValue(r, false);   // no take to end
                Object.DestroyImmediate(labelGo);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(recGo);
            }
        }

        [Test]
        public void TheSpeedFollowsTheCalendarAndSlowsWhileSomeoneWalks()
        {
            var go = new GameObject("Speed");
            try
            {
                var p = go.AddComponent<SimSpeedProfile>();
                p.openHours = "Mon-Fri 08:00-15:00";
                var tuesdayMorning = new System.DateTime(2026, 3, 10, 9, 0, 0);
                var tuesdayNight = new System.DateTime(2026, 3, 10, 23, 0, 0);
                Assert.AreEqual(p.openSpeed, p.SpeedAt(tuesdayMorning, false, 0));
                Assert.AreEqual(p.closedSpeed, p.SpeedAt(tuesdayNight, false, 0));
                Assert.AreEqual(p.walkingSpeed, p.SpeedAt(tuesdayMorning, true, 1f), "slow motion while someone walks");
                Assert.AreEqual(p.openSpeed, p.SpeedAt(tuesdayMorning, true, p.maxSlowSeconds + 0.1f), "for a few seconds at most");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void TheUsersSpeedWinsUntilAutoIsResumed()
        {
            var go = new GameObject("Speed");
            try
            {
                var p = go.AddComponent<SimSpeedProfile>();
                p.openHours = "Mon-Fri 08:00-15:00";
                var morning = new System.DateTime(2026, 3, 10, 9, 0, 0);
                double? s = p.Next(1.0, morning, false, 0.02f);
                Assert.AreEqual(p.openSpeed, s.Value, 1e-6, "the profile sets the clinic speed");
                Assert.IsNull(p.Next(s.Value, morning, false, 0.02f), "unchanged: nothing to set");
                // the user doubles it (HUD ×2 / the + key): the profile steps aside, also when someone walks
                Assert.IsNull(p.Next(s.Value * 2, morning, true, 0.02f), "the user's speed stays");
                Assert.IsTrue(p.Manual);
                Assert.IsNull(p.Next(s.Value * 2, morning, true, 0.02f), "still manual");
                p.ResumeAuto();
                Assert.AreEqual(p.walkingSpeed, p.Next(s.Value * 2, morning, true, 0.02f).Value, 1e-6, "Auto: the profile again");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void TheShotsSplitTheVideo()
        {
            var go = new GameObject("Recorder");
            try
            {
                var r = go.AddComponent<SimRecorder>();
                r.shots.Add(new SimRecorder.Shot { name = "Overview", seconds = 4 });
                r.shots.Add(new SimRecorder.Shot { name = "Waiting room", seconds = 6, glide = true });
                Assert.AreEqual(10f, r.TotalSeconds);
                Assert.AreEqual(0, r.ShotAt(1f, out float k0));
                Assert.AreEqual(0.25f, k0, 1e-6f);
                Assert.AreEqual(1, r.ShotAt(7f, out float k1));
                Assert.AreEqual(0.5f, k1, 1e-6f);
                Assert.AreEqual(1, r.ShotAt(99f, out _), "past the end: the last shot");
                StringAssert.Contains("-framerate 30", SimRecorder.FfmpegArguments(30, "take", "take/video.mp4"));
                StringAssert.Contains("f_%05d.jpg", SimRecorder.FfmpegArguments(30, "take", "take/video.mp4"));
                r.ScaleTo(20f);
                Assert.AreEqual(20f, r.TotalSeconds, 1e-4f, "a take's length stretches the shots");
                Assert.AreEqual(8f, r.ShotStart(1), 1e-4f);
                Assert.AreEqual(new Vector2Int(1280, 720), SimRecorder.EvenSize(1281, 721), "H.264 needs an even size");
                Assert.AreEqual(8294400u, SimRecorder.BitRate(1280, 720, 30, SimRecorder.VideoQuality.High), "0.3 bit per pixel per frame");
                Assert.Greater(SimRecorder.BitRate(1280, 720, 30, SimRecorder.VideoQuality.Maximum), SimRecorder.BitRate(1280, 720, 30, SimRecorder.VideoQuality.High));
                Assert.Greater(SimRecorder.BitRate(1280, 720, 30, SimRecorder.VideoQuality.High), SimRecorder.BitRate(1280, 720, 30, SimRecorder.VideoQuality.Standard));
                Assert.AreEqual(12000000u, SimRecorder.BitRate(1280, 720, 30, SimRecorder.VideoQuality.Standard, 12f), "an explicit bitrate wins");
                Assert.AreEqual(1000000u, SimRecorder.BitRate(64, 64, 1, SimRecorder.VideoQuality.Standard), "at least 1 Mbps");
                StringAssert.Contains("-crf 14", SimRecorder.FfmpegArguments(30, "take", "take/video.mp4", SimRecorder.VideoQuality.Maximum));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void AnElementShotLooksAtTheElementFromItsAngleAndFollowsIt()
        {
            var go = new GameObject("Recorder");
            var element = new GameObject("Station");
            try
            {
                element.transform.position = new Vector3(10, 0, 5);
                var r = go.AddComponent<SimRecorder>();
                r.shots.Add(new SimRecorder.Shot
                {
                    framing = SimRecorder.ShotFraming.Element, target = element.transform, distance = 10, elevation = 30, azimuth = 90, lookHeight = 1,
                });
                var pose = r.Framing(0, 0f);
                var look = new Vector3(10, 1, 5);
                Assert.AreEqual(10f, Vector3.Distance(pose.position, look), 1e-3f, "at its distance");
                Assert.AreEqual(1f + 10f * Mathf.Sin(30f * Mathf.Deg2Rad), pose.position.y, 1e-3f, "at its elevation");
                Assert.Less(pose.position.x, 10f, "azimuth 90: looking along +X, so standing on the -X side");
                Assert.AreEqual(1f, Vector3.Dot(pose.rotation * Vector3.forward, (look - pose.position).normalized), 1e-4f, "looking at it");
                Assert.AreEqual(r.fieldOfView, pose.fieldOfView);

                element.transform.position += new Vector3(3, 0, 0);
                Assert.AreEqual(pose.position.x + 3f, r.Framing(0, 0f).position.x, 1e-3f, "the layout changes: the shot follows");

                r.shots[0].framing = SimRecorder.ShotFraming.Orbit;
                r.shots[0].orbitDegrees = 180;
                r.shots[0].fieldOfView = 30;
                var half = r.Framing(0, 0.5f);
                Assert.AreEqual(180f, Mathf.Repeat(half.rotation.eulerAngles.y, 360f), 0.01f, "half-way round the orbit");
                Assert.AreEqual(30f, half.fieldOfView, "a shot's own field of view");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(element);
            }
        }

        [Test]
        public void GlidesFollowASmoothCurveThroughTheShots()
        {
            var go = new GameObject("Recorder");
            try
            {
                var r = go.AddComponent<SimRecorder>();
                SimRecorder.Shot Fixed(string n, Vector3 p, bool glide) => new SimRecorder.Shot { name = n, position = p, seconds = 4, glide = glide };
                r.shots.Add(Fixed("A", new Vector3(0, 10, 0), false));
                r.shots.Add(Fixed("B", new Vector3(20, 10, 0), true));
                r.shots.Add(Fixed("C", new Vector3(20, 10, 20), true));
                r.shots.Add(Fixed("D", new Vector3(0, 10, 20), false));

                Assert.AreEqual(r.shots[0].position, r.PoseAt(2f).position, "a fixed shot holds");
                Assert.AreEqual(0f, Vector3.Distance(r.shots[0].position, r.PoseAt(4.001f).position), 0.01f, "the glide starts where A stands");
                Assert.AreEqual(0f, Vector3.Distance(r.shots[1].position, r.PoseAt(8f - 1e-4f).position), 0.05f, "and reaches B at the end of B");
                Assert.AreEqual(0f, Vector3.Distance(r.shots[3].position, r.PoseAt(12.001f).position), 1e-3f, "D cuts");

                // the camera eases out of A, flows through B without stopping, eases into C
                float speedStart = Vector3.Distance(r.PoseAt(4.0f).position, r.PoseAt(4.05f).position);
                float speedAtB = Vector3.Distance(r.PoseAt(7.975f).position, r.PoseAt(8.025f).position);
                float speedEnd = Vector3.Distance(r.PoseAt(11.94f).position, r.PoseAt(11.99f).position);
                Assert.Greater(speedAtB, 10 * speedStart, "through B at speed");
                Assert.Greater(speedAtB, 10 * speedEnd, "stops at C");
                Assert.Greater(r.PoseAt(8f).position.z, -0.01f, "a curve, not a corner: past B it already turns towards C");

                // a partial glide: 1 s of glide, then the shot holds
                r.shots[3].glide = true;
                r.shots[3].glideSeconds = 1f;
                Assert.AreEqual(0f, Vector3.Distance(r.shots[3].position, r.PoseAt(14f).position), 1e-3f);
                Assert.Greater(Vector3.Distance(r.shots[3].position, r.PoseAt(12.5f).position), 1f);

                Assert.AreEqual(0.5f, SimRecorder.Ease(0.5f, true, true), 1e-6f);
                Assert.AreEqual(1f, SimRecorder.Ease(1f, true, false), 1e-6f);
                Assert.AreEqual(0.25f, SimRecorder.Ease(0.25f, false, false), 1e-6f);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void CaptionsFillTheirPlaceholdersAndAreBurntIntoTheFrame()
        {
            string s = SimRecorder.FillTemplate("{content:Agenda NML} waiting · {time} · {unknown}",
                (kind, arg) => kind == "content" ? (arg == "Agenda NML" ? "7" : "?") : kind == "time" ? "Tue 08:50" : null);
            Assert.AreEqual("7 waiting · Tue 08:50 · {unknown}", s);

            const int w = 320, h = 180;
            int Changed(byte[] px, int x0, int y0, int x1, int y1)   // top-down rectangle
            {
                int n = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                        if (px[((h - 1 - y) * w + x) * 4] != 0) n++;
                return n;
            }
            var frame = new byte[w * h * 4];
            SimRecorder.BurnCaptions(frame, w, h, new SimRecorder.CaptionText { title = "Consulta", subtitle = "Neumología", titleAlpha = 1f });
            Assert.Greater(Changed(frame, 0, h / 2, w / 2, h), 100, "the title bottom left");
            Assert.AreEqual(0, Changed(frame, w / 2, 0, w, h / 3), "no clock");

            frame = new byte[w * h * 4];
            SimRecorder.BurnCaptions(frame, w, h, new SimRecorder.CaptionText
            {
                clock = "Tue 11 Mar 2025 08:50", lines = new System.Collections.Generic.List<string> { "7 waiting" },
            });
            Assert.Greater(Changed(frame, w / 2, 0, w, h / 3), 100, "the clock top right");
            Assert.Greater(Changed(frame, 0, 0, w / 2, h / 3), 100, "the live values top left");
            Assert.AreEqual(0, Changed(frame, 0, h * 2 / 3, w / 2, h), "no title");
        }

        [Test]
        public void ShotsAreAddedAndChangedThroughTheCommands()
        {
            // in a scene of its own: the commands mark their scene modified
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);   // an untitled scene cannot have another added
            var scene = temporary ? UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                                                                                            UnityEditor.SceneManagement.NewSceneMode.Additive) : active;
            if (temporary) UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);
            var go = new GameObject("RecorderUnderTest");
            var element = new GameObject("RecorderTestStation");
            try
            {
                var r = go.AddComponent<SimRecorder>();
                element.transform.position = new Vector3(4, 0, 4);
                SimuLeanRecordCommands.RecordShot(shot: "Overview", framing: "fixed", position: "0,30,-20", rotation: "45,0,0", seconds: "5",
                                                  title: "Unidad del sueño", recorder: "RecorderUnderTest");
                SimuLeanRecordCommands.RecordShot(shot: "Station", target: "RecorderTestStation", distance: "8", elevation: "25", azimuth: "30",
                                                  seconds: "6", recorder: "RecorderUnderTest");
                Assert.AreEqual(2, r.shots.Count);
                Assert.AreEqual(new Vector3(0, 30, -20), r.shots[0].position);
                Assert.AreEqual("Unidad del sueño", r.shots[0].title);
                Assert.AreEqual(SimRecorder.ShotFraming.Element, r.shots[1].framing, "a target makes an element shot");
                Assert.IsTrue(r.shots[1].glide, "a new shot after the first glides");
                Assert.AreEqual(11f, r.TotalSeconds);

                SimuLeanRecordCommands.RecordShot(shot: "Station", framing: "orbit", orbit: "120", glide: "false", index: "0", recorder: "RecorderUnderTest");
                Assert.AreEqual("Station", r.shots[0].name, "moved first");
                Assert.AreEqual(SimRecorder.ShotFraming.Orbit, r.shots[0].framing);
                Assert.AreEqual(8f, r.shots[0].distance, "the other values kept");
                Assert.IsFalse(r.shots[0].glide);
                SimuLeanRecordCommands.RecordShot(shot: "Overview", title: " ", recorder: "RecorderUnderTest");
                Assert.AreEqual("", r.shots[1].title, "a space clears a text");

                Assert.Throws<System.ArgumentException>(() => SimuLeanRecordCommands.RecordShot(shot: "Nothing", framing: "element", recorder: "RecorderUnderTest"),
                                                        "an element shot needs a target");
                // review2 #12: a failing call changes nothing (it edited the shot in place, then failed)
                float before = r.shots[0].distance;
                Assert.Throws<System.ArgumentException>(() => SimuLeanRecordCommands.RecordShot(shot: "Station", distance: "20", framing: "pose",
                                                                                                 recorder: "RecorderUnderTest"));
                Assert.AreEqual(before, r.shots[0].distance, "the failed call left the shot as it was");
                Assert.AreEqual(SimRecorder.ShotFraming.Orbit, r.shots[0].framing);
                SimuLeanRecordCommands.RecordShot(shot: "Overview", remove: true, recorder: "RecorderUnderTest");
                Assert.AreEqual(1, r.shots.Count);
            }
            finally
            {
                if (temporary)
                {
                    if (active.IsValid()) UnityEngine.SceneManagement.SceneManager.SetActiveScene(active);
                    UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
                }
                else
                {
                    Object.DestroyImmediate(go);
                    Object.DestroyImmediate(element);
                }
            }
        }
    }
}
