using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using SimuLean.Results.Charts;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>The Recorder's shots and fly-path: where the camera is at a time of the video.</summary>
    public sealed partial class SimRecorder
    {
        // ── shots and the fly-path ───────────────────────────────────────────

        /// <summary>Video length: the shots' total (or 10 s without shots).</summary>
        public float TotalSeconds
        {
            get
            {
                float s = 0f;
                foreach (var sh in shots) if (sh != null) s += Mathf.Max(0.1f, sh.seconds);
                return s > 0f ? s : 10f;
            }
        }

        /// <summary>The shot at <paramref name="time"/> seconds of video and how far into it (0–1); -1 without shots.</summary>
        public int ShotAt(float time, out float progress)
        {
            float t = 0f;
            for (int i = 0; i < shots.Count; i++)
            {
                float len = Mathf.Max(0.1f, shots[i].seconds);
                if (time < t + len || i == shots.Count - 1) { progress = Mathf.Clamp01((time - t) / len); return i; }
                t += len;
            }
            progress = 0f;
            return -1;
        }

        /// <summary>When shot <paramref name="index"/> starts, in seconds of video.</summary>
        public float ShotStart(int index)
        {
            float t = 0f;
            for (int i = 0; i < index && i < shots.Count; i++) t += Mathf.Max(0.1f, shots[i].seconds);
            return t;
        }

        /// <summary>Stretches (or shrinks) every shot so that the video lasts <paramref name="total"/> seconds.</summary>
        public void ScaleTo(float total)
        {
            float now = 0f;
            foreach (var sh in shots) if (sh != null) now += Mathf.Max(0.1f, sh.seconds);
            if (now <= 0f || total <= 0f) return;
            float f = total / now;
            foreach (var sh in shots)
                if (sh != null)
                {
                    sh.seconds = Mathf.Max(0.1f, sh.seconds * f);
                    sh.glideSeconds *= f;
                }
        }

        /// <summary>Shot <paramref name="index"/>'s own camera <paramref name="k"/> (0–1) of the way through its motion
        /// (only an orbit moves), without the glide that leads into it.</summary>
        public CameraPose Framing(int index, float k)
        {
            var sh = index >= 0 && index < shots.Count ? shots[index] : null;
            float fov = sh != null && sh.fieldOfView > 0f ? sh.fieldOfView : fieldOfView;
            if (sh == null) return new CameraPose(transform.position, transform.rotation, fov);
            switch (sh.framing)
            {
                case ShotFraming.Fixed:
                    return new CameraPose(sh.position, Quaternion.Euler(sh.rotation), fov);
                case ShotFraming.Pose:
                    var p = sh.pose != null ? sh.pose : transform;
                    return new CameraPose(p.position, p.rotation, fov);
                default:
                    Vector3 look = (sh.target != null ? sh.target.position : transform.position) + Vector3.up * sh.lookHeight;
                    float az = sh.azimuth + (sh.framing == ShotFraming.Orbit ? sh.orbitDegrees * Mathf.Clamp01(k) : 0f);
                    return Around(look, sh.distance, sh.elevation, az, fov);
            }
        }

        /// <summary>A camera <paramref name="distance"/> m from <paramref name="look"/>, <paramref name="elevation"/>° above
        /// the horizon, looking along the compass direction <paramref name="azimuth"/> (0 = +Z) at it.</summary>
        public static CameraPose Around(Vector3 look, float distance, float elevation, float azimuth, float fieldOfView)
        {
            var rot = Quaternion.Euler(elevation, azimuth, 0f);
            return new CameraPose(look - rot * Vector3.forward * Mathf.Max(0.01f, distance), rot, fieldOfView);
        }

        /// <summary>The camera at <paramref name="time"/> seconds of video: each shot's framing, glided along a smooth curve
        /// (Catmull–Rom through the shots; easing in and out where the camera stops) where a shot glides.</summary>
        public CameraPose PoseAt(float time)
        {
            if (shots.Count == 0) return new CameraPose(transform.position, transform.rotation, fieldOfView);
            int i = ShotAt(time, out _);
            float len = Mathf.Max(0.1f, shots[i].seconds);
            float local = Mathf.Clamp(time - ShotStart(i), 0f, len);
            float g = GlideLength(i);
            if (local < g)
            {
                float u = local / g;
                bool flowIn = FlowsThrough(i - 1), flowOut = FlowsThrough(i);
                float s = Ease(u, !flowIn, !flowOut);
                CameraPose a = Framing(i - 1, 1f), b = Framing(i, 0f);
                Vector3 p0 = flowIn ? Framing(i - 2, 1f).position : 2f * a.position - b.position;
                Vector3 p3 = flowOut ? Framing(i + 1, 0f).position : 2f * b.position - a.position;
                return new CameraPose(CatmullRom(p0, a.position, b.position, p3, s),
                                      Quaternion.Slerp(a.rotation, b.rotation, s), Mathf.Lerp(a.fieldOfView, b.fieldOfView, s));
            }
            float rest = len - g;
            return Framing(i, rest > 1e-4f ? (local - g) / rest : 1f);
        }

        // seconds of shot i spent gliding into it (0 = a cut)
        private float GlideLength(int i)
        {
            if (i <= 0 || i >= shots.Count || !shots[i].glide) return 0f;
            float len = Mathf.Max(0.1f, shots[i].seconds);
            return shots[i].glideSeconds > 0f ? Mathf.Min(shots[i].glideSeconds, len) : len;
        }

        // true when the camera is still moving as shot i ends into shot i+1: shot i is all glide, does not orbit, and
        // shot i+1 glides on — the curve goes through without stopping
        private bool FlowsThrough(int i)
        {
            if (i <= 0 || i + 1 >= shots.Count) return false;
            float len = Mathf.Max(0.1f, shots[i].seconds);
            return shots[i].framing != ShotFraming.Orbit && GlideLength(i) >= len - 1e-4f && shots[i + 1].glide;
        }

        /// <summary>The glide's progress: eased where the camera starts / stops, linear where it flows on (the speed is
        /// continuous across the shots).</summary>
        public static float Ease(float u, bool easeIn, bool easeOut)
        {
            u = Mathf.Clamp01(u);
            if (easeIn && easeOut) return u * u * (3f - 2f * u);
            if (easeIn) return u * u * (2f - u);
            if (easeOut) return u + u * u - u * u * u;
            return u;
        }

        /// <summary>Uniform Catmull–Rom from <paramref name="p1"/> (t = 0) to <paramref name="p2"/> (t = 1).</summary>
        public static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
        }


        private void OnDrawGizmosSelected()
        {
            if (shots.Count == 0) return;
            // the fly-path, and a marker where each shot starts
            Gizmos.color = new Color(0.25f, 0.65f, 0.85f);
            float total = TotalSeconds;
            Vector3 last = PoseAt(0f).position;
            for (float t = 0.1f; t <= total; t += 0.1f)
            {
                Vector3 p = PoseAt(t).position;
                Gizmos.DrawLine(last, p);
                last = p;
            }
            for (int i = 0; i < shots.Count; i++)
            {
                var pose = PoseAt(ShotStart(i) + GlideLength(i));
                Gizmos.DrawWireSphere(pose.position, 0.3f);
                Gizmos.DrawRay(pose.position, pose.rotation * Vector3.forward * 2f);
            }
        }
    }
}
