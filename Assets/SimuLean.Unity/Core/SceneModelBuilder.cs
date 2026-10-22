using System.Collections.Generic;
using SimuLean.Headless;
using SimuLean.Serialization;

namespace SimuLean.Unity
{
    /// <summary>
    /// Builds the scene's model for a run in the scene: the scene is extracted (<see cref="SceneExtractor"/>) and the
    /// engine factory builds it — exactly as in a headless run — with each element component as the view of its engine
    /// element. The components (elements, resource teams, lists, downtimes, the transport network) are then bound to the
    /// engine objects built for them. One build path: what runs in the scene is what runs headless.
    /// </summary>
    public static class SceneModelBuilder
    {
        /// <summary>Builds the model on <paramref name="clock"/> and binds the scene; <paramref name="bound"/> receives
        /// the element components of the run. Start it with <see cref="HeadlessModelFactory.StartModel"/>.</summary>
        /// <param name="root">Model root (null = the whole scene).</param>
        /// <param name="configure">Changes the extracted config before the build (e.g. an experiment's scenario).</param>
        public static HeadlessModelFactory Build(SimClock clock, List<SElement> bound, UnityEngine.GameObject root = null,
                                                 System.Action<SimulationConfig> configure = null)
        {
            SceneExtraction x = SceneExtractor.Extract(root, sceneRun: true);
            configure?.Invoke(x.Config);
            var factory = new HeadlessModelFactory(clock, false, ElementHandlerRegistry.CreateDefault(),
                                                   ec => x.ElementOf(ec.Id) as IVElement);
            Dictionary<string, Element> built = factory.BuildModel(x.Config);

            bound.Clear();
            foreach (var kv in x.ElementIds)
                if (built.TryGetValue(kv.Value, out Element e))
                {
                    kv.Key.Bind(e);
                    bound.Add(kv.Key);
                }
            foreach (var kv in x.ListIds)
                kv.Key.Bind(factory.ListLinks.TryGetValue(kv.Value, out ListLink link) ? link : null);
            foreach (var kv in x.TeamIds)
                kv.Key.Bind(factory.Teams.TryGetValue(kv.Value, out var team) ? team : null, factory.Network);
            foreach (var kv in x.DowntimeIds)
                kv.Key.Bind(factory.DowntimesById.TryGetValue(kv.Value, out var gens) ? gens : null);
            foreach (var kv in x.ReactionIds)
                kv.Key.Bind(factory.ReactionsById.TryGetValue(kv.Value, out var reaction) ? reaction : null);
            foreach (var net in root != null ? root.GetComponentsInChildren<UnityTransportNetwork>() : UnityCompat.FindAll<UnityTransportNetwork>())
                net.Traffic = factory.Space?.Traffic;
            return factory;
        }
    }
}
