using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Supplies the pick list (SKU → quantity) for a container arriving at a
    /// <see cref="PickingStation"/>. Implementations return null when they have
    /// no list for the given container, allowing sources to be chained
    /// (<see cref="CompositePickListSource"/>): e.g. SQLite table → container
    /// labels → static Inspector table.
    /// </summary>
    public interface IPickListSource
    {
        /// <returns>SKU → qty to pick, or null if this source has no list for the container.</returns>
        Dictionary<string, int> GetPickList(Item container);
    }

    /// <summary>Fixed pick list configured once (e.g. from the Inspector).</summary>
    public class StaticPickListSource : IPickListSource
    {
        private readonly Dictionary<string, int> pickList;

        public StaticPickListSource(Dictionary<string, int> pickList)
        {
            this.pickList = pickList;
        }

        public Dictionary<string, int> GetPickList(Item container)
        {
            if (pickList == null || pickList.Count == 0)
                return null;
            return new Dictionary<string, int>(pickList);
        }
    }

    /// <summary>
    /// Reads the pick list from the container item's own labels. Every label
    /// named "&lt;prefix&gt;&lt;SKU&gt;" (default prefix "pick_") contributes
    /// SKU → (int)value. Lets each order carry its own kitting list, e.g. set
    /// by a ScheduleSource data table.
    /// </summary>
    public class LabelPickListSource : IPickListSource
    {
        private readonly string prefix;

        public LabelPickListSource(string prefix = "pick_")
        {
            this.prefix = prefix;
        }

        public Dictionary<string, int> GetPickList(Item container)
        {
            if (!container.HasProperties)
                return null;

            Dictionary<string, int> result = null;
            var props = container.Properties;
            foreach (var name in props.Names)
            {
                if (!name.StartsWith(prefix) || name.Length <= prefix.Length)
                    continue;

                int qty = (int)(props.TryGetDouble(name, out double d) ? d : System.Convert.ToDouble(props.Get(name)));
                if (qty <= 0)
                    continue;

                if (result == null)
                    result = new Dictionary<string, int>();
                result[name.Substring(prefix.Length)] = qty;
            }
            return result;
        }
    }

    /// <summary>
    /// Chains several sources; the first one returning a non-null list wins.
    /// </summary>
    public class CompositePickListSource : IPickListSource
    {
        private readonly IPickListSource[] sources;

        public CompositePickListSource(params IPickListSource[] sources)
        {
            this.sources = sources ?? new IPickListSource[0];
        }

        public Dictionary<string, int> GetPickList(Item container)
        {
            foreach (var s in sources)
            {
                if (s == null) continue;
                var list = s.GetPickList(container);
                if (list != null && list.Count > 0)
                    return list;
            }
            return null;
        }
    }
}
