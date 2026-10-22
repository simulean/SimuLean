using System;

namespace SimuLean.Unity
{
    /// <summary>
    /// Registers a model-element wrapper (<see cref="SElement"/>) in the SimuLean window
    /// (Tools ▸ SimuLean ▸ Open SimuLean, Library tab), grouped by <see cref="Module"/>. Every concrete
    /// <see cref="SElement"/> must carry it (checked by an EditMode test), so the library always lists
    /// the whole element set. Pure metadata: nothing happens at run time.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class SimuLeanElementAttribute : Attribute
    {
        public SimuLeanElementAttribute(string module, string displayName)
        {
            Module = module; DisplayName = displayName;
        }

        /// <summary>Library group (use the <see cref="SimuLeanModules"/> constants).</summary>
        public string Module { get; }
        public string DisplayName { get; }
        public string Description { get; set; } = "";

        /// <summary>Prefab file under <c>Assets/SimuLean.Unity/Prefabs/Elements</c> (empty = a bare
        /// GameObject with the component).</summary>
        public string Prefab { get; set; } = "";

        /// <summary>The tile icon when there is no prefab: a model / prefab (its preview) or a texture asset path.</summary>
        public string Icon { get; set; } = "";

        /// <summary>Sort order inside the module.</summary>
        public int Order { get; set; }

        /// <summary>The library pack it belongs to (<see cref="SimuLeanPacks"/>; empty = by its module:
        /// <see cref="SimuLeanPacks.OfModule"/>).</summary>
        public string Pack { get; set; } = "";
    }

    /// <summary>
    /// The library pack of a preset (a <c>GameObject/SimuLean/…</c> menu method) when its menu folder does not say it
    /// (<see cref="SimuLeanPacks.OfPresetFolder"/>), e.g. the hospital porters among the resource teams. Pure metadata.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class SimuLeanPresetPackAttribute : Attribute
    {
        public SimuLeanPresetPackAttribute(string pack) { Pack = pack; }

        /// <summary>A <see cref="SimuLeanPacks"/> constant.</summary>
        public string Pack { get; }
    }

    /// <summary>
    /// A preset that is decoration only — a building, furniture, fences: it adds models, no element or resource, and a
    /// run is the same without it. The Library lists such presets under <i>Visuals</i>, hidden unless its
    /// <i>Visuals</i> switch is on. A preset under a <c>Building/</c> or <c>…/Decor/</c> menu folder is visual without it.
    /// Pure metadata.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class SimuLeanVisualAttribute : Attribute { }

    /// <summary>
    /// The SimuLean window's tile icon of a preset (a <c>GameObject/SimuLean/…</c> menu method): an asset path — a model
    /// or prefab (its asset preview is drawn) or a texture (drawn as it is). Presets without it show their initials.
    /// Pure metadata.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class SimuLeanPresetIconAttribute : Attribute
    {
        public SimuLeanPresetIconAttribute(string assetPath) { AssetPath = assetPath; }

        /// <summary>Project path of the icon asset (e.g. <c>Assets/SimuLean.Unity/Art/Models/CncMachine/CncMachine.fbx</c>).</summary>
        public string AssetPath { get; }
    }

    /// <summary>
    /// A parameterless action of a scene component that an agent may run through <c>simulean_invoke</c> (fill a
    /// travel matrix, rebuild a floor zone…, which agents ran by reflection through <c>eval</c>). Editor-time actions on
    /// the scene; never run by a simulation. Pure metadata.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = true)]
    public sealed class SimuLeanActionAttribute : Attribute
    {
        public SimuLeanActionAttribute(string description) { Description = description ?? ""; }

        /// <summary>What the action does (listed by <c>simulean_invoke</c>).</summary>
        public string Description { get; }
    }

    /// <summary>
    /// Registers a tool — a scene component that is not a model element (simulation clock, operator
    /// pools, downtime generators, transport networks, reports, runtime views, experimentation) — in
    /// the SimuLean window: by its category in the Library (Resources, Lists, Transport, Downtime), the
    /// Experiments tab (Experiments, Analysis) or the Settings tab (Simulation, Visualization; singletons as
    /// on / off switches). Pure metadata.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class SimuLeanToolAttribute : Attribute
    {
        public SimuLeanToolAttribute(string category, string displayName)
        {
            Category = category; DisplayName = displayName;
        }

        /// <summary>Library group (use the <see cref="SimuLeanToolCategories"/> constants).</summary>
        public string Category { get; }
        public string DisplayName { get; }
        public string Description { get; set; } = "";

        /// <summary>At most one instance per scene (Create selects the existing one).</summary>
        public bool Singleton { get; set; }

        /// <summary>Added to the main camera instead of a new GameObject (camera tools).</summary>
        public bool OnMainCamera { get; set; }

        /// <summary>The tile icon: a model / prefab (its preview) or a texture asset path (empty = the initials).</summary>
        public string Icon { get; set; } = "";

        /// <summary>Sort order inside the category.</summary>
        public int Order { get; set; }

        /// <summary>The library pack it belongs to (<see cref="SimuLeanPacks"/>; empty = <see cref="SimuLeanPacks.Core"/>).</summary>
        public string Pack { get; set; } = "";
    }

    /// <summary>
    /// The library's packs: <see cref="Core"/> — the general elements, resources, transport, storage,
    /// building kit and the model tools — is always shown; the others are sector libraries the user shows or hides in
    /// the Library tab (the project's choice in <c>ProjectSettings/SimuLeanSettings.json</c>; a pack an open scene uses
    /// is shown while it does). Hiding a pack only filters the window: its types still build and run, and the MCP
    /// commands see every entry.
    /// </summary>
    public static class SimuLeanPacks
    {
        public const string Core          = "Core";
        public const string Manufacturing = "Manufacturing";
        public const string Warehousing   = "Warehousing";
        public const string Healthcare    = "Healthcare";
        public const string Shipbuilding  = "Shipbuilding";

        /// <summary>The packs that can be hidden, in display order.</summary>
        public static readonly string[] Optional = { Manufacturing, Warehousing, Healthcare, Shipbuilding };

        /// <summary>The packs shown in a new project.</summary>
        public static readonly string[] DefaultShown = { Manufacturing, Warehousing };

        /// <summary>The pack of an element module (the sector modules are their packs; the rest is core).</summary>
        public static string OfModule(string module)
            => module == SimuLeanModules.Healthcare ? Healthcare : module == SimuLeanModules.Shipbuilding ? Shipbuilding : Core;

        /// <summary>The pack of a preset by the first folder of its menu path (<c>GameObject/SimuLean/Healthcare/…</c>).</summary>
        public static string OfPresetFolder(string folder)
        {
            foreach (string p in Optional) if (string.Equals(folder, p, StringComparison.OrdinalIgnoreCase)) return p;
            return Core;
        }
    }

    /// <summary>Element modules of the library (one tab group each).</summary>
    public static class SimuLeanModules
    {
        public const string Sources    = "Sources";
        public const string Buffers    = "Buffers";
        public const string Processors = "Processors";
        public const string Assembly   = "Assembly & Kitting";
        public const string Sinks      = "Sinks";
        public const string Transport  = "Transport";
        public const string Storage    = "Storage";
        /// <summary>The naval library (erection dock, …).</summary>
        public const string Shipbuilding = "Shipbuilding";
        /// <summary>The healthcare library (agenda, …).</summary>
        public const string Healthcare = "Healthcare";

        /// <summary>Display order of the modules.</summary>
        public static readonly string[] Order = { Sources, Buffers, Processors, Assembly, Sinks, Transport, Storage, Shipbuilding, Healthcare };
    }

    /// <summary>Tool categories of the library.</summary>
    public static class SimuLeanToolCategories
    {
        public const string Simulation    = "Simulation";
        public const string Resources     = "Resources";
        public const string Lists         = "Lists";
        public const string Downtime      = "Downtime";
        /// <summary>Reactions: model logic triggered by what elements do.</summary>
        public const string Logic         = "Logic";
        public const string Transport     = "Transport";
        public const string Data          = "Data";
        public const string Analysis      = "Analysis";
        public const string Experiments   = "Experiments";
        public const string Visualization = "Visualization";
        /// <summary>Model data tables (e.g. the naval library's block breakdown, read from the project database).</summary>

        /// <summary>Display order of the categories.</summary>
        public static readonly string[] Order = { Simulation, Data, Lists, Resources, Downtime, Logic, Transport, Analysis, Experiments, Visualization };
    }
}
