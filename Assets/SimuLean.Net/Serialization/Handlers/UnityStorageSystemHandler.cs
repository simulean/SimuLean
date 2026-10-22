using System.Collections.Generic;
using SimuLean;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Handler for <see cref="UnityStorageSystem"/> → <see cref="SimuLean.Storage.StorageSystem"/>.
    ///
    /// Lanes are extracted through the wrapper's <c>ResolveLanes()</c> (the
    /// same resolution InitializeSim uses), so scene and headless models see
    /// identical lanes. The optional transport binding travels as a
    /// "TransportSystem" connection carrying the home node id.
    /// </summary>
    internal sealed class UnityStorageSystemHandler : ElementHandlerBase
    {
        public override string TypeName => "UnityStorageSystem";

        /// <summary>Parameterisation of this type's keys (numbers / bools are found by type; see ParameterSchema).</summary>
        public override SimuLean.Optimization.ParameterSchema Schema { get; } = new SimuLean.Optimization.ParameterSchema()
            .Choice("locationPolicyType", "FirstAvailable", "ClosestSpot", "Random", "DedicatedBySku")
            .Choice("relocationPolicyType", "FirstAvailable", "ClosestSpot", "Random", "DedicatedBySku")
            .Structural("lanes", "locationPolicy_skuMap", "relocationPolicy_skuMap", "refX", "refY", "refZ");



        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => TransportStorageBuilders.BuildStorageSystem(config, clock, vElement);
    }
}
