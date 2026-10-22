using System;

namespace SimuLean.Transport
{
    /// <summary>
    /// A node of the transport network. Carries an id and an abstract
    /// (x, y, z) coordinate in meters. The engine never reads Unity
    /// Transforms — the Unity layer (or a config file) populates the
    /// coordinates at build time.
    /// </summary>
    public class TransportNode
    {
        /// <summary>Unique id within the network.</summary>
        public string Id { get; }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public TransportNode(string id, double x, double y, double z)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("TransportNode id must be non-empty.", nameof(id));
            Id = id;
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>Euclidean distance to another node (used as the default track length).</summary>
        public double DistanceTo(TransportNode other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            double dz = Z - other.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public override string ToString() => $"Node({Id})";
    }
}
