using System;

namespace SimuLean
{
    /// <summary>
    /// Discriminates the payload actually stored in a <see cref="ParameterValue"/>.
    /// </summary>
    public enum ParameterKind : byte
    {
        /// <summary>No value (default of an uninitialised <see cref="ParameterValue"/>).</summary>
        None   = 0,
        /// <summary>32-bit signed integer, stored in the numeric slot.</summary>
        Int    = 1,
        /// <summary>Double-precision float, stored in the numeric slot.</summary>
        Double = 2,
        /// <summary>Boolean, stored in the numeric slot as 0/1.</summary>
        Bool   = 3,
        /// <summary>String, stored in the reference slot.</summary>
        String = 4
    }

    /// <summary>
    /// A typed, allocation-free parameter value: exactly one of int / double / bool
    /// / string, tagged by <see cref="Kind"/>. Replaces the former boxed
    /// <c>object</c> in the per-model parameter bag (<see cref="SimContext.ModelParameters"/>).
    ///
    /// Efficiency: this is a <c>readonly struct</c>. Numeric kinds (int/double/bool)
    /// share a single <see cref="double"/> field and never allocate; only the string
    /// kind carries a reference. Storing it in a <c>Dictionary&lt;string,ParameterValue&gt;</c>
    /// therefore boxes nothing, unlike the previous <c>Dictionary&lt;string,object&gt;</c>
    /// which boxed every numeric/boolean value.
    ///
    /// Use the implicit conversions to build values (<c>ParameterValue p = 3;</c>,
    /// <c>= 2.5;</c>, <c>= true;</c>, <c>= "round_robin";</c>) and the strongly typed
    /// <c>As*</c> accessors (throw on kind mismatch) or the non-throwing
    /// <c>TryGet*</c> methods to read them back.
    /// </summary>
    public readonly struct ParameterValue : IEquatable<ParameterValue>
    {
        // Numeric slot reused by Int/Double/Bool (bool = 0/1); string slot for String.
        private readonly double _number;
        private readonly string _string;

        /// <summary>Which of the payload slots is meaningful for this value.</summary>
        public ParameterKind Kind { get; }

        private ParameterValue(ParameterKind kind, double number, string str)
        {
            Kind    = kind;
            _number = number;
            _string = str;
        }

        /// <summary>The empty value (<see cref="Kind"/> == <see cref="ParameterKind.None"/>).</summary>
        public static readonly ParameterValue None = default;

        /// <summary>True when this value carries no payload.</summary>
        public bool IsNone => Kind == ParameterKind.None;

        // ── factory methods ──────────────────────────────────────────────────
        public static ParameterValue FromInt(int value)       => new ParameterValue(ParameterKind.Int,    value,           null);
        public static ParameterValue FromDouble(double value) => new ParameterValue(ParameterKind.Double, value,           null);
        public static ParameterValue FromBool(bool value)     => new ParameterValue(ParameterKind.Bool,   value ? 1.0 : 0, null);
        public static ParameterValue FromString(string value) => new ParameterValue(ParameterKind.String, 0.0,             value);

        // ── implicit conversions (build a value) ─────────────────────────────
        public static implicit operator ParameterValue(int value)    => FromInt(value);
        public static implicit operator ParameterValue(double value) => FromDouble(value);
        public static implicit operator ParameterValue(bool value)   => FromBool(value);
        public static implicit operator ParameterValue(string value) => FromString(value);

        // ── strongly typed accessors (throw on mismatch) ─────────────────────
        /// <summary>Reads the value as an int. Int/Double/Bool are accepted
        /// (Double is truncated); throws for String/None.</summary>
        public int AsInt()
        {
            switch (Kind)
            {
                case ParameterKind.Int:
                case ParameterKind.Double:
                case ParameterKind.Bool:
                    return (int)_number;
                default:
                    throw new InvalidOperationException($"ParameterValue of kind {Kind} is not numeric.");
            }
        }

        /// <summary>Reads the value as a double. Int/Double/Bool are accepted;
        /// throws for String/None.</summary>
        public double AsDouble()
        {
            switch (Kind)
            {
                case ParameterKind.Int:
                case ParameterKind.Double:
                case ParameterKind.Bool:
                    return _number;
                default:
                    throw new InvalidOperationException($"ParameterValue of kind {Kind} is not numeric.");
            }
        }

        /// <summary>Reads the value as a bool. Bool returns its stored flag;
        /// Int/Double return (value != 0); throws for String/None.</summary>
        public bool AsBool()
        {
            switch (Kind)
            {
                case ParameterKind.Bool:
                case ParameterKind.Int:
                case ParameterKind.Double:
                    return _number != 0.0;
                default:
                    throw new InvalidOperationException($"ParameterValue of kind {Kind} is not boolean.");
            }
        }

        /// <summary>Reads the value as a string. Only the String kind is accepted;
        /// throws otherwise (use <see cref="ToString"/> for a display form of any kind).</summary>
        public string AsString()
        {
            if (Kind == ParameterKind.String)
                return _string;
            throw new InvalidOperationException($"ParameterValue of kind {Kind} is not a string.");
        }

        // ── non-throwing accessors ───────────────────────────────────────────
        public bool TryGetInt(out int value)
        {
            if (Kind == ParameterKind.Int || Kind == ParameterKind.Double || Kind == ParameterKind.Bool)
            {
                value = (int)_number;
                return true;
            }
            value = 0;
            return false;
        }

        public bool TryGetDouble(out double value)
        {
            if (Kind == ParameterKind.Int || Kind == ParameterKind.Double || Kind == ParameterKind.Bool)
            {
                value = _number;
                return true;
            }
            value = 0.0;
            return false;
        }

        public bool TryGetBool(out bool value)
        {
            if (Kind == ParameterKind.Bool || Kind == ParameterKind.Int || Kind == ParameterKind.Double)
            {
                value = _number != 0.0;
                return true;
            }
            value = false;
            return false;
        }

        public bool TryGetString(out string value)
        {
            if (Kind == ParameterKind.String)
            {
                value = _string;
                return true;
            }
            value = null;
            return false;
        }

        // ── equality / display ───────────────────────────────────────────────
        public bool Equals(ParameterValue other)
        {
            if (Kind != other.Kind) return false;
            if (Kind == ParameterKind.String)
                return string.Equals(_string, other._string, StringComparison.Ordinal);
            return _number.Equals(other._number);
        }

        public override bool Equals(object obj) => obj is ParameterValue other && Equals(other);

        public override int GetHashCode()
        {
            int h = (int)Kind;
            if (Kind == ParameterKind.String)
                return (h * 397) ^ (_string != null ? _string.GetHashCode() : 0);
            return (h * 397) ^ _number.GetHashCode();
        }

        public static bool operator ==(ParameterValue a, ParameterValue b) =>  a.Equals(b);
        public static bool operator !=(ParameterValue a, ParameterValue b) => !a.Equals(b);

        /// <summary>Culture-invariant textual form (for logs / reports / debug).</summary>
        public override string ToString()
        {
            switch (Kind)
            {
                case ParameterKind.Int:    return ((int)_number).ToString(System.Globalization.CultureInfo.InvariantCulture);
                case ParameterKind.Double: return _number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case ParameterKind.Bool:   return (_number != 0.0) ? "true" : "false";
                case ParameterKind.String: return _string ?? string.Empty;
                default:                   return string.Empty;
            }
        }
    }
}
