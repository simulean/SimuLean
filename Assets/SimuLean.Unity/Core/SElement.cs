using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    public abstract class SElement : MonoBehaviour
    {
        [Tooltip("Output elements this element connects to.")]
        public List<SElement> nextElements = new List<SElement>();

        [Tooltip("Optional shared routing-strategy asset (Assets → Create → SimuLean → Output Strategy). " +
                 "Applied in ConnectSim; empty = element/inline default (FirstAvailable).")]
        public OutputStrategyAsset outputStrategyAsset;

        [Tooltip("Which next element gets each item (first available, round robin, shortest queue, an expression…).")]
        public OutputStrategyConfig outputStrategy = new OutputStrategyConfig();

        [Tooltip("Which items this element admits (all, by label, by a condition over the item and the element…).")]
        public InputStrategyConfig inputStrategy = new InputStrategyConfig();

        [Tooltip("Labels set on every item that enters, 'name = expression' (e.g. reps = reps + 1, t_in = now(), " +
                 "valid = rand() < 0.9). Set before this element takes the item, so its times and routing read them.")]
        public List<string> entryLabels = new List<string>();

        [Tooltip("Labels set on every item that leaves, 'name = expression' (e.g. dx = pick(0.6, 'diagnosis', 0.4, 'control')). " +
                 "Set before the next element takes the item.")]
        public List<string> exitLabels = new List<string>();

        [Tooltip("Values recorded for every item that enters, 'name = expression' (e.g. time_in_system = age, " +
                 "to_decision = t_decision - created, within_30d = age <= 720): mean, std, min, max, P50, P90 in the results.")]
        public List<string> tallies = new List<string>();

        [Tooltip("Split the item statistics by this label (e.g. priority): one set per value as well as the total. Empty = no split.")]
        public string tallySplit = "";

        /// <summary>The element receives items through its input (false: sources; the Combiner filters per input).</summary>
        public virtual bool HasInput => true;

        /// <summary>Item flow can reach the element through an upstream's Next Elements: every element with an input, and the
        /// Combiner (no input strategy, but its inputs are its incoming connections). The one rule of the Connect tool and
        /// <c>simulean_connect</c>.</summary>
        public virtual bool TakesConnections => HasInput;

        /// <summary>The element sends items to next elements (false: sinks).</summary>
        public virtual bool HasOutput => true;

        /// <summary>Where this element sends its items.</summary>
        public enum SendMode { Elements, List }

        /// <summary>Where this element gets its items from.</summary>
        public enum ReceiveMode { Connections, List }

        [Tooltip("Elements: send to Next Elements. List: push the items to a list — they stay here until an element pulls them.")]
        public SendMode sendTo = SendMode.Elements;

        [Tooltip("Connections: receive from the elements that list this one in their Next Elements. List: pull items from a list.")]
        public ReceiveMode receiveFrom = ReceiveMode.Connections;

        [Tooltip("The list this element pushes its items to (Send To = List).")]
        public UnitySimList pushToList;

        [Tooltip("The list this element pulls its items from (Receive From = List).")]
        public UnitySimList pullFromList;

        /// <summary>The list it pushes to, or null when it sends to elements.</summary>
        public UnitySimList PushList => sendTo == SendMode.List ? pushToList : null;

        /// <summary>The list it pulls from, or null when it receives from connections.</summary>
        public UnitySimList PullList => receiveFrom == ReceiveMode.List ? pullFromList : null;

        [Tooltip("Which items to pull and in which order (empty = everything, in the list's default order). " +
                 "Fields: item labels, age, pushTime, itemPriority, owner, type.")]
        public SimuLean.Collections.ListQuerySpec pullQuery = new SimuLean.Collections.ListQuerySpec();

        // ── resources: what a resource team does for this element ──
        [Tooltip("Resources for the process (stations): team, requirements, when they are released.")]
        public ProcessResourceUse processBy = new ProcessResourceUse();

        [Tooltip("Network node where the element stands (optional): vehicles and walking operators carry its items from / to it.")]
        public UnityTransportNode location;

        [Tooltip("Where the resources that process here stand (network nodes around the element), apart from Location where " +
                 "items are picked up and dropped — e.g. the welders' places beside a block. Each takes a free one; empty = Location.")]
        public List<UnityTransportNode> workNodes = new List<UnityTransportNode>();

        [Tooltip("Resources that carry this element's items to its Next Elements (load + travel + unload).")]
        public TransportResourceUse transportBy = new TransportResourceUse();

        [Tooltip("Its items walk by themselves to its Next Elements (patients), from its Location to theirs, in model time.")]
        public ItemWalkUse walkBy = new ItemWalkUse();

        // ── auto-attach stats display and connection gizmo ────────────────
        /// <summary>
        /// Called by Unity when the component is first added or Reset is pressed.
        /// Ensures every SElement always has a SElementConnectionGizmo sibling (its live statistics are a section of the
        /// element's inspector).
        /// </summary>
        protected virtual void Reset()
        {
            if (GetComponent<SElementConnectionGizmo>() == null)
                gameObject.AddComponent<SElementConnectionGizmo>();
        }

        // ── run (driven by UnitySimClock) ─────────────────────────────────
        // A scene run builds its model like a headless run: the scene is extracted (SceneExtractor) and the engine
        // handlers build every element, with this component as its view (VElement). The component is then bound to
        // its engine element — one build path for the scene, headless runs, experiments and the optimizer.

        private Element element;

        /// <summary>The engine element of the current run (null outside a run).</summary>
        public Element GetElement() => element;

        /// <summary>Binds the engine element the model factory built for this component (before the run starts).</summary>
        public void Bind(Element built)
        {
            element = built;
            OnBound(built);
        }

        /// <summary>The engine element of this run was built: keep a typed reference, prepare the views.</summary>
        protected virtual void OnBound(Element built) { }

        /// <summary>The model started (every element, resource and list): views that need the started state.</summary>
        public virtual void OnRunStarted() { }

        /// <summary>Clears the views of the finished run (items shown, animations). The next run builds a new model.</summary>
        public virtual void ClearViews() { }
    }
}

