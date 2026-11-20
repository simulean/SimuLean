using System;

namespace SimuLean.Serialization
{
    /// <summary>
    /// The one implementation behind every <see cref="IConfigValues"/> of a config DTO (element, resource team, reaction,
    /// downtime): the setting at a path of its settings object, read and written through <see cref="SettingsPaths"/>. Each
    /// DTO names its settings object (null = none given yet), the type whose paths are declared and how it is named in an
    /// error.
    /// </summary>
    internal static class ConfigValues
    {
        /// <summary>The setting at <paramref name="path"/> as <typeparamref name="T"/>; no settings, or a path the type
        /// does not declare = <paramref name="defaultValue"/>.</summary>
        public static T Get<T>(object settings, Type declared, string path, T defaultValue, string owner)
            => settings != null && SettingsPaths.Declares(declared, path)
                ? SettingsPaths.As(SettingsPaths.GetValue(settings, path, owner), path, defaultValue)
                : defaultValue;

        /// <summary>The setting's value as stored (false when there are no settings, it is not given or not declared).</summary>
        public static bool TryGet(object settings, Type declared, string path, string owner, out object value)
        {
            value = settings != null && SettingsPaths.Declares(declared, path) ? SettingsPaths.GetValue(settings, path, owner) : null;
            return value != null;
        }

        /// <summary>Clears a setting (its default applies again); nothing when it has no value.</summary>
        public static void Remove(object settings, Type declared, string path, string owner)
        {
            if (TryGet(settings, declared, path, owner, out _)) SettingsPaths.SetValue(settings, path, null, owner);
        }
    }
}
