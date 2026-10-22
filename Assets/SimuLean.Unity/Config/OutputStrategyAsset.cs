using UnityEngine;
using SimuLean;

namespace SimuLean.Unity
{
    /// <summary>
    /// Shareable, drag-and-drop routing-strategy asset (backlog Task 1 Fase 1).
    ///
    /// DESIGN DECISION: this deliberately WRAPS the existing
    /// <see cref="OutputStrategyConfig"/> factory instead of introducing a
    /// second strategy mechanism — one source of truth for how strategy types
    /// map to <see cref="IOutputStrategy"/> instances. The asset adds what a
    /// plain serializable config cannot: a project-level object users create
    /// once (Assets → Create → SimuLean → Output Strategy), configure in its
    /// own Inspector, and assign to any number of elements.
    ///
    /// Assign it to <see cref="SElement.outputStrategyAsset"/>; the base
    /// <c>ConnectSim()</c> applies it to the wrapped engine element.
    /// PRECEDENCE: lifecycle order is InitializeSim (inline config) →
    /// ConnectSim (this asset), so an assigned asset OVERRIDES the element's
    /// inline OutputStrategyConfig — assigning an asset is the stronger,
    /// deliberate act. Leave the asset field empty to keep the inline config;
    /// if neither is set, the engine default (FirstAvailableStrategy) applies.
    /// Note: wrappers that override ConnectSim without calling base skip the
    /// asset (currently only UnityCombiner-style port wiring).
    /// </summary>
    [CreateAssetMenu(fileName = "OutputStrategy",
                     menuName = "SimuLean/Output Strategy",
                     order = 10)]
    public class OutputStrategyAsset : ScriptableObject
    {
        [Tooltip("Strategy configuration — same options as the inline element config.")]
        public OutputStrategyConfig config = new OutputStrategyConfig();

    }
}
