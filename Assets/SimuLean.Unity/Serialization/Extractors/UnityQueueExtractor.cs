using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityQueue</c>; the headless build side is
    /// <c>QueueHandler</c> in the engine.</summary>
    internal sealed class UnityQueueExtractor : ElementExtractorBase, IElementReferenceExtractor
    {
        public override string TypeName => "Queue";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityQueue queue)) return;

            config.Set("capacity", queue.capacity);
            // every option also at its default: a study can vary it from there; the fields' paths (QueueHandler)
            config.Set("offerOrder", queue.offerOrder.ToString());
            config.Set("offerScan", queue.offerScan);
            if (!string.IsNullOrWhiteSpace(queue.offerSort)) config.Set("offerSort", queue.offerSort.Trim());
            if (!string.IsNullOrWhiteSpace(queue.offerRule))
            {
                config.Set("offerRule", queue.offerRule.Trim());   // a release rule
                RuleParameterValue.Extract(config, queue.ruleParameters);
            }
            config.Set("gated", queue.gated);
            config.Set("groupBySetup", queue.groupBySetup);
            config.Set("groupLimit", queue.groupLimit);
            config.Set("maxWait", string.IsNullOrWhiteSpace(queue.maxWait) ? "0" : queue.maxWait.Trim());   // 0 = none
            config.Set("onTimeout", queue.onTimeout.ToString());
        }

        /// <summary>The timeout target by its model id (once every element has one).</summary>
        public void ExtractReferences(SElement element, ElementConfig config, IReadOnlyDictionary<SElement, string> ids)
        {
            if (!(element is UnityQueue queue) || config == null || string.IsNullOrWhiteSpace(queue.maxWait)
                || queue.onTimeout == SimuLean.TimeoutAction.KeepWaiting || queue.timeoutTarget == null) return;
            config.Set("timeoutTarget", ids.TryGetValue(queue.timeoutTarget, out string id) ? id : queue.timeoutTarget.name);
        }
    }
}
