using System;
using System.Collections.Generic;
using SimuLean.Stochastics.Distributions;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Headless builds of the line-supply elements (typed settings): <see cref="CallOffPoint"/>
    /// (<see cref="CallOffPointSettings"/>), <see cref="Separator"/> (<see cref="SeparatorSettings"/>),
    /// <see cref="LineSideUnpacker"/> (<see cref="LineSideUnpackerSettings"/>) and <see cref="PacedLine"/>
    /// (<see cref="PacedLineSettings"/>). The Unity extractors fill the settings; the handlers call <c>Build</c>, so the
    /// scene and headless runs build the same model.
    /// </summary>
    public static class FlowBuilders
    {
        /// <summary>Builds an engine family; its reference tables get the stream owner / <c>&lt;family&gt;.ref</c>.</summary>
        public static CallOffFamily BuildFamily(CallOffFamilySettings f, SimClock clock, string owner, string defaultName = null)
        {
            f = f ?? new CallOffFamilySettings();
            string name = string.IsNullOrWhiteSpace(f.Name) ? defaultName : f.Name.Trim();
            var family = new CallOffFamily(name, Math.Max(1, f.Quantity))
            {
                OrderType = string.IsNullOrEmpty(f.OrderType) ? "CallOff" : f.OrderType,
                ReferenceLabel = string.IsNullOrEmpty(f.ReferenceLabel) ? "reference" : f.ReferenceLabel,
            };
            try { family.Condition = f.Condition ?? ""; }
            catch (Exception e) { throw new FormatException($"'{owner}', family '{name}': bad condition '{f.Condition}': {e.Message}", e); }
            int k = 0;
            foreach (var t in f.ReferenceTables ?? new List<ReferenceTableSettings>())
            {
                k++;
                if (t?.Choices == null || t.Choices.Count == 0) continue;
                var values = new List<string>(); var weights = new List<double>();
                foreach (var c in t.Choices) { values.Add((c?.Reference ?? "").Trim()); weights.Add(c?.Weight ?? 0); }
                try { family.ReferenceTables.Add(new DiscreteChoice(values, weights)); }
                catch (ArgumentException e) { throw new FormatException($"'{owner}', family '{name}': reference table {k}: {e.Message}", e); }
            }
            family.AssignStreams(clock, owner);
            return family;
        }

        // ── call-off point ───────────────────────────────────────────────────

        public static CallOffPoint BuildCallOffPoint(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<CallOffPointSettings>();
            var point = new CallOffPoint(config.Name, clock, null, vElement)
            {
                SequenceLabel = s.SequenceLabel ?? "",
                OverwriteSequence = s.OverwriteSequence,
            };
            int n = 0;
            foreach (var f in s.Families ?? new List<CallOffFamilySettings>())
            {
                n++;
                point.AddFamily(BuildFamily(f, clock, config.Name, "Family" + n));
            }
            return point;
        }

        // ── separator (Core) ─────────────────────────────────────────────────

        public static Separator BuildSeparator(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<SeparatorSettings>();
            return new Separator(config.Name, clock, Math.Max(1, s.Capacity), s.Mode, vElement)
            {
                Order = s.UnpackOrder,
                ProcessTime = Sampler(s.ProcessTime, clock, config.Name, "processTime"),
                SplitQuantity = Sampler(s.SplitQuantity, clock, config.Name, "splitQuantity"),
            };
        }

        // ── line-side unpacker (Manufacturing) ───────────────────────────────

        public static LineSideUnpacker BuildLineSideUnpacker(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<LineSideUnpackerSettings>();
            return new LineSideUnpacker(config.Name, clock, Math.Max(1, s.Capacity), vElement)
            {
                Order = s.UnpackOrder,
                PieceTime = Sampler(s.PieceTime, clock, config.Name, "pieceTime"),
                ChangeTime = Sampler(s.ChangeTime, clock, config.Name, "changeTime"),
                ChangeTimeFirst = s.ChangeTimeFirst,
                CopyCarrierLabels = s.CopyCarrierLabels,
                EmptiesUseCapacity = s.EmptiesUseCapacity,
            };
        }

        // ── paced line ───────────────────────────────────────────────────────

        public static PacedLine BuildPacedLine(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<PacedLineSettings>();
            IRandomSampler takt = Sampler(s.Takt, clock, config.Name, "takt")
                ?? throw new FormatException($"Paced line '{config.Name}': no takt.");
            var line = new PacedLine(config.Name, clock, Math.Max(1, s.Positions), takt, vElement)
            {
                SequenceLabel = s.SequenceLabel ?? "",
                ComponentsToVehicle = s.ComponentsToVehicle,
            };
            int i = 0;
            foreach (var st in s.Stations ?? new List<LineStationSettings>())
            {
                string name = string.IsNullOrWhiteSpace(st?.Name) ? "Station" + (i + 1) : st.Name.Trim();
                var family = BuildFamily(st?.Family, clock, config.Name, name);
                line.AddStation(new LineStation(name, st?.Position ?? 1, family)
                {
                    MatchLabel = st?.MatchLabel ?? "",
                    CallOffPosition = st?.CallOffPosition ?? -1,
                    PortCapacity = Math.Max(1, st?.PortCapacity ?? 2),
                });
                i++;
            }
            return line;
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private static IRandomSampler Sampler(SamplerSettings s, SimClock clock, string owner, string purpose)
            => ElementBuildHelpers.SamplerOf(s, clock, owner, purpose, purpose);
    }
}
