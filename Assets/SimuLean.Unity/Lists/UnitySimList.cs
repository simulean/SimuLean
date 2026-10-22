using System.Collections.Generic;
using SimuLean.Collections;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A list in the scene: elements push their items to it (their
    /// <see cref="SElement.pushToList"/>) and pull from it with a query (<see cref="SElement.pullFromList"/>,
    /// <see cref="SElement.pullQuery"/>) instead of being connected to each other — a model-wide
    /// priority queue. Built by the model factory with the rest of the model (one build path) and bound
    /// here (<see cref="Bind"/>) for the views; its inspector shows the live content.
    /// </summary>
    [SimuLeanTool(SimuLeanToolCategories.Lists, "List",
        Description = "Model-wide list: elements push their items to it and pull from it with a query (priority queues, dispatching)")]
    [AddComponentMenu("SimuLean/List")]
    public class UnitySimList : MonoBehaviour
    {
        [Tooltip("Name of the list in the model (empty = the GameObject name).")]
        public string listName = "";

        [Tooltip("Fields of the list: item labels (under their own or another name), built-ins (age, pushTime, " +
                 "itemPriority, owner, type) or formulas over other fields (e.g. prio * 10 + age). Queries and orders " +
                 "use them by name; monitored fields record their values when entries are pushed and pulled.")]
        public List<ListFieldSpec> fields = new List<ListFieldSpec> { new ListFieldSpec("prio", ListFieldKind.Label, "prio") };

        [Tooltip("Field the pulls without their own order use (one of the list's fields, a built-in or any item label). " +
                 "Empty = first pushed, first pulled. Entries without the field rank as 0.")]
        public string defaultOrderBy = "prio";

        [Tooltip("Highest value first for the default order.")]
        public bool defaultDescending = true;

        /// <summary>Engine list (null before the run initialises).</summary>
        public SimList List { get; private set; }

        /// <summary>Link joining the pushing / pulling elements to the list.</summary>
        public ListLink Link { get; private set; }

        public string ListName => string.IsNullOrEmpty(listName) ? gameObject.name : listName;

        /// <summary>Binds the list the model factory built for this run (null outside a run).</summary>
        public void Bind(ListLink link)
        {
            Link = link;
            List = link?.List;
        }

        /// <summary>Field names usable in queries and orders: the list's fields, then the built-ins.</summary>
        public List<string> AvailableFields()
        {
            var names = new List<string>();
            if (fields != null)
                foreach (var f in fields)
                    if (f != null && !string.IsNullOrWhiteSpace(f.name) && !names.Contains(f.name.Trim())) names.Add(f.name.Trim());
            foreach (string b in SimList.BuiltInFields) if (!names.Contains(b)) names.Add(b);
            return names;
        }

    }
}
