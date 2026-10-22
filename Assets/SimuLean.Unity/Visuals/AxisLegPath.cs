using UnityEngine;
using SimuLean.Resources;

namespace SimuLean.Unity
{
    /// <summary>
    /// Where a member is along its travel leg at a simulation time, for the views of axis-travel resources
    /// (<see cref="CraneResourceView"/>, <see cref="RobotArmView"/>). With <see cref="AxisTravel"/> each axis x / y / z
    /// follows its own speed / acceleration profile (<see cref="AxisTravel.AxisPosition"/>, the same profile the engine
    /// timed the leg with): together, or one after the other (x, then y, then z) when the team's axes are sequential.
    /// Any other travel model moves in a straight line at constant speed. View only.
    /// </summary>
    public static class AxisLegPath
    {
        /// <summary>The point between <paramref name="from"/> and <paramref name="to"/> (the leg's end points) at
        /// <paramref name="now"/>.</summary>
        public static Vector3 Along(ITravelModel model, TravelLeg leg, Vector3 from, Vector3 to, double now)
        {
            double total = leg.EndTime - leg.StartTime;
            float f = (float)leg.Fraction(now);
            if (!(model is AxisTravel ax) || double.IsNaN(total) || total <= 0) return Vector3.Lerp(from, to, f);
            double t = f * total;
            if (ax.Sequential)
            {
                double tx = AxisTravel.AxisTime(to.x - from.x, ax.Speed(0), ax.Accel(0));
                double ty = AxisTravel.AxisTime(to.y - from.y, ax.Speed(1), ax.Accel(1));
                return new Vector3((float)Axis(from.x, to.x, ax, 0, t), (float)Axis(from.y, to.y, ax, 1, t - tx),
                                   (float)Axis(from.z, to.z, ax, 2, t - tx - ty));
            }
            return new Vector3((float)Axis(from.x, to.x, ax, 0, t), (float)Axis(from.y, to.y, ax, 1, t),
                               (float)Axis(from.z, to.z, ax, 2, t));
        }

        private static double Axis(float from, float to, AxisTravel ax, int i, double t)
            => from + System.Math.Sign(to - from) * AxisTravel.AxisPosition(to - from, ax.Speed(i), ax.Accel(i), t);
    }
}
