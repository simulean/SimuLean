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
    public class ElementProperty
    {
        public string key;
        public double value;
        [Tooltip("Text value (used instead of the number when not empty).")]
        public string text = "";
        [Tooltip("Reference to another element (used instead of number / text when set).")]
        public SElement reference;

        public ElementProperty(string key = "", double value = 0)
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
    public class SElementProperties : MonoBehaviour
    {
        [Tooltip("The element's properties: a name and a number, a text or a reference to another element")]
        [SerializeField]
        private List<ElementProperty> properties = new List<ElementProperty>();

        private Dictionary<string, double> numbers;

        private void Awake()
        {
            Rebuild();
        }

        /// <summary>
        /// Rebuilds the internal dictionary from the serialized list.
        /// Call this after modifying the list in the Inspector.
        /// </summary>
        public void Rebuild()
        {
            numbers = new Dictionary<string, double>();
            
            foreach (var pair in properties)
            {
                if (!string.IsNullOrWhiteSpace(pair.key))
                {
                    numbers[pair.key] = pair.value;
                }
            }
        }

        // The running engine element (null at edit time / before the run initialises).
        private Element Running
        {
            get { var se = GetComponent<SElement>(); return se != null ? se.GetElement() : null; }
        }

        /// <summary>The properties as config records (references by element name).</summary>
        public List<SimuLean.Serialization.PropertySettings> Records()
        {
            var list = new List<SimuLean.Serialization.PropertySettings>();
            foreach (var p in properties)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.key)) continue;
                if (p.reference != null) list.Add(SimuLean.Serialization.PropertySettings.Reference(p.key, p.reference.name));
                else if (!string.IsNullOrEmpty(p.text)) list.Add(SimuLean.Serialization.PropertySettings.OfText(p.key, p.text));
                else list.Add(SimuLean.Serialization.PropertySettings.Number(p.key, p.value));
            }
            return list;
        }

        /// <summary>
        /// Gets the number of a property by name (the running element's property during a run).
        /// </summary>
        /// <param name="key">Property name</param>
        /// <returns>Its number if exists, null otherwise</returns>
        public double? GetValue(string key)
        {
            var running = Running;
            if (running != null) return running.Properties.TryGetDouble(key, out double v) ? v : (double?)null;
            if (numbers == null)
                Rebuild();

            if (numbers.TryGetValue(key, out double value))
                return value;

            return null;
        }

        /// <summary>
        /// Sets the number of a property, adding it when missing.
        /// </summary>
        /// <param name="key">Property name</param>
        /// <param name="value">Its number</param>
        public void SetValue(string key, double value)
        {
            Running?.Properties.SetDouble(key, value);
            if (numbers == null)
                Rebuild();

            numbers[key] = value;

            // Update the serialized list
            bool found = false;
            foreach (var pair in properties)
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
                properties.Add(new ElementProperty(key, value));
            }
        }

        /// <summary>
        /// Whether the element has a numeric property of this name.
        /// </summary>
        public bool Has(string key)
        {
            if (numbers == null)
                Rebuild();

            return numbers.ContainsKey(key);
        }

        /// <summary>
        /// Removes a property.
        /// </summary>
        public bool Remove(string key)
        {
            if (numbers == null)
                Rebuild();

            bool removed = numbers.Remove(key);
            
            if (removed)
            {
                properties.RemoveAll(p => p.key == key);
            }

            return removed;
        }

        /// <summary>
        /// Gets all numbers as a dictionary.
        /// </summary>
        public Dictionary<string, double> GetAll()
        {
            if (numbers == null)
                Rebuild();

            return new Dictionary<string, double>(numbers);
        }

        /// <summary>
        /// The properties as edited in the Inspector.
        /// </summary>
        public List<ElementProperty> GetProperties()
        {
            return properties;
        }

        /// <summary>
        /// Clears all numbers.
        /// </summary>
        public void Clear()
        {
            if (numbers != null)
                numbers.Clear();
            
            properties.Clear();
        }

        /// <summary>
        /// Gets the number of numbers.
        /// </summary>
        public int Count
        {
            get
            {
                if (numbers == null)
                    Rebuild();
                return numbers.Count;
            }
        }
    }
}
