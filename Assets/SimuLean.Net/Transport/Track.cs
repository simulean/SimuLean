using System;

namespace SimuLean.Transport
{
    /// <summary>
    /// Kind of a network edge. Determines which transport implementation may
    /// traverse it and how travel time is computed.
    /// </summary>
    public enum TrackType
    {
        /// <summary>Free-travel segment for vehicles (AGVs, forklifts, operators, tuggers).
        /// The vehicle is the resource; the track supplies distance.</summary>
        TravelPath = 0,

        /// <summary>The track itself is the transporter (distributed capacity along its
        /// length). Reserved for the conveyor implementation — drawable/definable now,
        /// simulated in a later phase.</summary>
        Conveyor = 1,

        /// <summary>Single-vehicle guided track (crane / ASRS aisle). Reserved: travelled like a path today (the
        /// simultaneous-axis motion of cranes is the team's <c>Axes</c> travel, not this track type).</summary>
        Crane = 2
    }

    /// <summary>
    /// Shared per-track parameters, referenced by many tracks (edit in one
    /// place). Every field is reserved (drawn and saved, not simulated yet): travel
    /// times use the moving resource's own speed.
    /// </summary>
    public class TrackProfile
    {
        public string Name;

        /// <summary>Reserved: a speed cap in m/s for vehicles on this track (&lt;= 0 = uncapped); not applied yet.</summary>
        public double MaxSpeed;

        // ── reserved for the conveyor phase (see unified proposal §4.6) ──
        public double ItemSeparation;
        public bool Accumulating = true;
        public double Acceleration;   // reserved; not simulated

        public TrackProfile(string name = null, double maxSpeed = 0)
        {
            Name = name;
            MaxSpeed = maxSpeed;
        }
    }

    /// <summary>
    /// A directed (optionally bidirectional) edge of the transport network.
    /// Length defaults to the euclidean distance between its nodes; a manual
    /// length overrides it (e.g. a curved aisle longer than the straight line).
    /// </summary>
    public class Track
    {
        public string FromId { get; }
        public string ToId { get; }
        public bool Bidirectional { get; }
        public TrackType Type { get; }
        public TrackProfile Profile { get; }

        /// <summary>Length in meters (already resolved — euclidean or manual).</summary>
        public double Length { get; }

        public Track(string fromId, string toId, double length,
                     bool bidirectional = true,
                     TrackType type = TrackType.TravelPath,
                     TrackProfile profile = null)
        {
            if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(toId))
                throw new ArgumentException("Track endpoints must be non-empty node ids.");
            if (fromId == toId)
                throw new ArgumentException($"Track cannot connect node '{fromId}' to itself.");
            if (length < 0)
                throw new ArgumentException("Track length must be >= 0.", nameof(length));

            FromId = fromId;
            ToId = toId;
            Length = length;
            Bidirectional = bidirectional;
            Type = type;
            Profile = profile;
        }

        public override string ToString()
            => $"Track({FromId}{(Bidirectional ? "<->" : "->")}{ToId}, {Length:F2}m, {Type})";
    }
}
