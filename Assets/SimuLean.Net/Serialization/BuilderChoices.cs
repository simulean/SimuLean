using System;

namespace SimuLean.Serialization
{
    /// <summary>
    /// How the model builder reads the choice texts it parses itself (a routing, an admission; a timeout action:
    /// <see cref="WaitLimit.TryActionOf"/>), in one place:
    /// the builder, the logic view (<c>ModelLogic</c>, <c>LogicSentences</c>) and the rule list of an element read them through
    /// it, so what a model is said to do is what it does — a number, spaces around a name or another case included.
    /// </summary>
    internal static class BuilderChoices
    {
        /// <summary>The routing <paramref name="text"/> builds: a name (exact case) or a number (<c>Enum.TryParse</c>);
        /// anything else, and a number that names no routing, builds FirstAvailable.</summary>
        public static HeadlessOutputStrategyType Routing(string text)
            => Enum.TryParse(text ?? "", out HeadlessOutputStrategyType t) && Enum.IsDefined(typeof(HeadlessOutputStrategyType), t)
                ? t : HeadlessOutputStrategyType.FirstAvailable;

        /// <summary>The admission <paramref name="text"/> builds: a name (exact case) or a number; anything else builds Default.</summary>
        public static HeadlessInputStrategyType Admission(string text)
            => Enum.TryParse(text ?? "", out HeadlessInputStrategyType t) && Enum.IsDefined(typeof(HeadlessInputStrategyType), t)
                ? t : HeadlessInputStrategyType.Default;

        /// <summary>
        /// The option name a setting's text stands for once built, for the settings the builder reads through this class
        /// (<see cref="OutputStrategySettings.StrategyType"/>, <see cref="InputStrategySettings.StrategyType"/>, every
        /// <c>onTimeout</c>); <paramref name="text"/> as it is for any other setting, and for a timeout text that fails the build.
        /// </summary>
        public static string Built(SettingsPaths.Member setting, string text)
        {
            var field = setting?.Field;
            if (field == null || field.FieldType != typeof(string)) return text;
            if (field.DeclaringType == typeof(OutputStrategySettings) && field.Name == nameof(OutputStrategySettings.StrategyType))
                return Routing(text).ToString();
            if (field.DeclaringType == typeof(InputStrategySettings) && field.Name == nameof(InputStrategySettings.StrategyType))
                return Admission(text).ToString();
            if (field.Name == "OnTimeout") return WaitLimit.TryActionOf(text, out var action) ? action.ToString() : text;
            return text;
        }
    }
}
