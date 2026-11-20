using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// An element with <b>named port elements</b>: child elements with their own links (an input buffer for a component,
    /// an outbound port for empties or orders), created by the element itself.
    /// <list type="bullet">
    /// <item>Headless wiring: the connection types <c>PortInput</c> (upstream → the target's port named by the
    ///   connection parameter <c>port</c>) and <c>PortOutput</c> (the source's port → downstream) of
    ///   <see cref="Headless.HeadlessModelFactory"/> resolve the port with <see cref="GetPort"/>.</item>
    /// <item>Results: <see cref="Results.RunResults.Capture"/> reports every port of <see cref="Ports"/> as an element
    ///   object (id <c>owner/port</c>, group = the owner), with its link statistics and states.</item>
    /// </list>
    /// The owner starts its ports in its own <c>Start</c>.
    /// </summary>
    public interface IPortedElement
    {
        /// <summary>The port elements by port name (stable order).</summary>
        IReadOnlyList<KeyValuePair<string, Element>> Ports { get; }

        /// <summary>The port with this name, or null.</summary>
        Element GetPort(string port);
    }

    /// <summary>
    /// An element (or port) with statistics of its own beyond the link / state ones (counters such as a line's indexes
    /// or a station's line stops). <see cref="Results.RunResults.Capture"/> adds them to the element's result object;
    /// the names must be <see cref="Results.ResultMetrics"/> constants.
    /// </summary>
    public interface IElementMetrics
    {
        /// <summary>Reports each statistic once: <c>set(name, value)</c>.</summary>
        void ReportMetrics(Action<string, double> set);
    }
}
