using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Base class for simulation items: a <see cref="SimEntity"/> whose labels are its <see cref="SimEntity.Properties"/>.
    /// </summary>
    public class Item : SimEntity
    {
        double creationTime;

        private string type;
        private int myId;

        public int priority;

        private object visual;

        /// <summary>The item's view (a Unity GameObject in a scene run, null headless). Assigning one raises
        /// <see cref="VisualAttached"/>, so views that show the item's labels (block number, size) get the item.</summary>
        public object vItem
        {
            get => visual;
            set
            {
                visual = value;
                if (value != null) VisualAttached?.Invoke(this);
            }
        }

        /// <summary>Raised when an item gets a visual (a source creating it, a station or combiner replacing its look,
        /// an erection dock gathering the ship). The Unity layer binds the visual's item views to it; the engine never
        /// subscribes.</summary>
        public static event System.Action<Item> VisualAttached;

        public List<Item> subItems;



        public Item(double creationTime)
        {
            this.creationTime = creationTime;
        }

        public void SetId(string type, int myId, int priority)
        {

            this.type = type;
            this.myId = myId;
            this.priority = priority;
        }

        public int GetId()
        {
            return myId;
        }

        public double GetCreationTime()
        {
            return creationTime;
        }

        public void AddItem(Item theItem)
        {
            if (subItems == null)
            {
                subItems = new List<Item>();
            }
            subItems.Add(theItem);
        }

        public List<Item> GetSubItems()
        {
            return subItems;
        }
        public void SetType(string newType)
        {
            this.type = newType;
        }

        /// <summary>
        /// Returns the textual type previously assigned with <see cref="SetType"/>.
        /// This is useful for debugging when items are identified by a string
        /// rather than the numeric id.
        /// </summary>
        public string GetTypeName()
        {
            return type;
        }

        /// <summary>Name of the item: its type (see <see cref="SetType"/>).</summary>
        public override string Name => type;

        // ── labels = the item's properties ────────────────

        /// <summary>
        /// Sets a label from text. If the text parses as a number (dot or comma
        /// decimal separator) it is stored as a numeric label; otherwise it is kept as
        /// a text label (see <see cref="GetLabelText"/>). Null/empty values are ignored.
        /// </summary>
        public void SetLabelValue(string label, string value) => Properties.SetParsed(label, value);

        /// <summary>Sets a numeric label (replaces a label of another kind with the same name).</summary>
        public void SetLabelValue(string label, double value) => Properties.SetDouble(label, value);

        /// <summary>Sets a text label, never parsed as a number (replaces a label of another kind with the same name).</summary>
        public void SetLabelText(string label, string text) => Properties.SetString(label, text);

        /// <summary>The label as text (a number in the invariant culture), or null.</summary>
        public string GetLabelText(string label) => HasProperties ? Properties.GetText(label) : null;

        /// <summary>Numeric label value, or <paramref name="defaultValue"/> when absent.</summary>
        public double GetLabelOrDefault(string label, double defaultValue = 0.0)
            => HasProperties && Properties.TryGetDouble(label, out double v) ? v : defaultValue;

        /// <summary>True if a label with this name exists.</summary>
        public bool HasLabel(string label) => HasProperties && Properties.Has(label);

        public double? GetLabelValue(string label)
            => HasProperties && Properties.TryGetDouble(label, out double v) ? v : (double?)null;

        /// <summary>Every label (read-only view, values boxed: double, string, entity, array) — no copy is made.</summary>
        public IReadOnlyDictionary<string, object> Labels => Properties.View;

        /// <summary>Copies all labels from another item.</summary>
        public void CopyLabelsFrom(Item other)
        {
            if (other != null && other.HasProperties) Properties.CopyFrom(other.Properties);
        }
    }
}

