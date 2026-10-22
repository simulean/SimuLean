using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// One property of an element: a number (<see cref="value"/>), a text (<see cref="text"/>, when
    /// set) or a reference to another element (<see cref="reference"/>, when set — it wins).
    /// </summary>
    [Serializable]
    public class LabelPair
    {
        public string key;
        public double value;
        [Tooltip("Text value (used instead of the number when not empty).")]
        public string text = "";
        [Tooltip("Reference to another element (used instead of number / text when set).")]
        public SElement reference;

        public LabelPair(string key = "", double value = 0)
        {
            this.key = key;
            this.value = value;
        }
    }

    /// <summary>
    /// The user properties of an element, set in the scene: numbers, texts and references to other
    /// elements. They become the engine element's <see cref="SimEntity.Properties"/> when the run initialises
    /// (<see cref="ApplyTo"/>) and are exported with the model (<c>properties</c> records); during a run the get / set
    /// methods read and write the element's properties (the list stays the edit-time definition).
    /// </summary>
    public class SElementLabels : MonoBehaviour
    {
        [Tooltip("Label key-value pairs for this element")]
        [SerializeField]
        private List<LabelPair> labelPairs = new List<LabelPair>();

        private Dictionary<string, double> labels;

        private void Awake()
        {
            RebuildLabelsDictionary();
        }

        /// <summary>
        /// Rebuilds the internal dictionary from the serialized list.
        /// Call this after modifying labelPairs in the Inspector.
        /// </summary>
        public void RebuildLabelsDictionary()
        {
            labels = new Dictionary<string, double>();
            
            foreach (var pair in labelPairs)
            {
                if (!string.IsNullOrWhiteSpace(pair.key))
                {
                    labels[pair.key] = pair.value;
                }
            }
        }

        // The running engine element (null at edit time / before the run initialises).
        private Element Running
        {
            get { var se = GetComponent<SElement>(); return se != null ? se.GetElement() : null; }
        }

        /// <summary>The properties as config records (references by element name).</summary>
        public List<string> Records()
        {
            var list = new List<string>();
            foreach (var p in labelPairs)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.key)) continue;
                if (p.reference != null) list.Add(SimuLean.Serialization.PropertyRecords.Ref(p.key, p.reference.name));
                else if (!string.IsNullOrEmpty(p.text)) list.Add(SimuLean.Serialization.PropertyRecords.Text(p.key, p.text));
                else list.Add(SimuLean.Serialization.PropertyRecords.Double(p.key, p.value));
            }
            return list;
        }

        /// <summary>
        /// Gets the value of a label by key (the running element's property during a run).
        /// </summary>
        /// <param name="key">Label key</param>
        /// <returns>Label value if exists, null otherwise</returns>
        public double? GetLabelValue(string key)
        {
            var running = Running;
            if (running != null) return running.Properties.TryGetDouble(key, out double v) ? v : (double?)null;
            if (labels == null)
                RebuildLabelsDictionary();

            if (labels.TryGetValue(key, out double value))
                return value;

            return null;
        }

        /// <summary>
        /// Sets the value of a label. Creates the label if it doesn't exist.
        /// </summary>
        /// <param name="key">Label key</param>
        /// <param name="value">Label value</param>
        public void SetLabelValue(string key, double value)
        {
            Running?.Properties.SetDouble(key, value);
            if (labels == null)
                RebuildLabelsDictionary();

            labels[key] = value;

            // Update the serialized list
            bool found = false;
            foreach (var pair in labelPairs)
            {
                if (pair.key == key)
                {
                    pair.value = value;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                labelPairs.Add(new LabelPair(key, value));
            }
        }

        /// <summary>
        /// Checks if a label exists.
        /// </summary>
        public bool HasLabel(string key)
        {
            if (labels == null)
                RebuildLabelsDictionary();

            return labels.ContainsKey(key);
        }

        /// <summary>
        /// Removes a label.
        /// </summary>
        public bool RemoveLabel(string key)
        {
            if (labels == null)
                RebuildLabelsDictionary();

            bool removed = labels.Remove(key);
            
            if (removed)
            {
                labelPairs.RemoveAll(p => p.key == key);
            }

            return removed;
        }

        /// <summary>
        /// Gets all labels as a dictionary.
        /// </summary>
        public Dictionary<string, double> GetAllLabels()
        {
            if (labels == null)
                RebuildLabelsDictionary();

            return new Dictionary<string, double>(labels);
        }

        /// <summary>
        /// Gets the label pairs for Inspector display.
        /// </summary>
        public List<LabelPair> GetLabelPairs()
        {
            return labelPairs;
        }

        /// <summary>
        /// Clears all labels.
        /// </summary>
        public void ClearLabels()
        {
            if (labels != null)
                labels.Clear();
            
            labelPairs.Clear();
        }

        /// <summary>
        /// Gets the number of labels.
        /// </summary>
        public int Count
        {
            get
            {
                if (labels == null)
                    RebuildLabelsDictionary();
                return labels.Count;
            }
        }
    }
}
