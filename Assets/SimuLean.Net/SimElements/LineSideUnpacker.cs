using System;

namespace SimuLean
{
    /// <summary>
    /// <b>Line-side unpacker</b> (Manufacturing pack, line supply; a specialisation of the Core <see cref="Separator"/>): the point at an assembly line where
    /// carriers (wagons, totes) are emptied. It unpacks only, and the line pulls: one piece at a time, each after
    /// <see cref="PieceTime"/> (none = as soon as the line takes the previous one), FIFO / LIFO; <see cref="ChangeTime"/>
    /// before each carrier after the first (SETUP; before the first too with <see cref="ChangeTimeFirst"/>); the empty carrier leaves through port <c>empties</c> (to a milk run's
    /// pick-up point) and, with <see cref="EmptiesUseCapacity"/> (default), keeps its place until it has been collected;
    /// <see cref="CopyCarrierLabels"/> copies the carrier's labels onto its pieces. Besides the separator's counters it
    /// reports the line-side stock: the pieces held over time (<c>pieces_avg</c> / <c>pieces_min</c> / <c>pieces_max</c>,
    /// carriers not counted).
    /// </summary>
    public class LineSideUnpacker : Separator
    {
        /// <param name="capacity">Carriers held at once (≥ 1).</param>
        public LineSideUnpacker(string name, SimClock clock, int capacity = 2, IVElement vElement = null)
            : base(name, clock, capacity, SeparatorMode.Unpack, vElement)
        {
            ReleaseOneByOne = true;
            KeepEmptiesInPlace = true;
        }

        /// <summary>Time to take each piece out (null = none). An expression gets the piece.</summary>
        public IRandomSampler PieceTime { get => PieceTimeSampler; set => PieceTimeSampler = value; }

        /// <summary>Time to change to the next carrier before its first piece (null = none). Gets the new carrier.</summary>
        public IRandomSampler ChangeTime { get => ChangeTimeSampler; set => ChangeTimeSampler = value; }

        /// <summary><see cref="ChangeTime"/> before the run's first carrier too, i.e. a handling time for every carrier
        /// (default false: the line starts with the first carrier in place).</summary>
        public bool ChangeTimeFirst { get => ChangeBeforeFirst; set => ChangeBeforeFirst = value; }

        /// <summary>Copy the carrier's labels the piece does not have onto each piece (default false).</summary>
        public bool CopyCarrierLabels { get => CopyContainerLabels; set => CopyContainerLabels = value; }

        /// <summary>An empty carrier keeps its place until it has left the empties port (default true).</summary>
        public bool EmptiesUseCapacity { get => KeepEmptiesInPlace; set => KeepEmptiesInPlace = value; }

        public override void ReportMetrics(Action<string, double> set)
        {
            base.ReportMetrics(set);
            set(Results.ResultMetrics.PiecesAvg, PiecesHeld.GetTimeWeightedAverage(simClock.GetSimulationTime()));
            set(Results.ResultMetrics.PiecesMin, PiecesHeld.Min);
            set(Results.ResultMetrics.PiecesMax, PiecesHeld.Max);
        }
    }
}
