using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Unity-serializable configuration for InputStrategy.
    /// Can be embedded in any Unity element component that needs item validation.
    /// </summary>
    [Serializable]
    public class InputStrategyConfig
    {
        [Tooltip("Type of input validation strategy")]
        public InputStrategyType strategyType = InputStrategyType.Default;

        // ── Single Label Strategy ──────────────────────────────────────────
        [Tooltip("For SingleLabel: Label name to validate")]
        public string singleLabelName = "";

        [Tooltip("For SingleLabel: Required value (leave empty to update from main item)")]
        public string singleLabelValue = "";

        // ── Multi Label Strategy ───────────────────────────────────────────
        [Tooltip("For MultiLabel: Label-value pairs in format 'label=value'")]
        public string[] multiLabelPairs = new string[0];

        [Tooltip("For Condition: admits the item when this expression holds, e.g. item.color == 'red' && item.weight <= element.maxWeight")]
        public string condition = "";

        /// <summary>
        /// Validates the configuration and returns an error message if invalid.
        /// </summary>
        public bool ValidateConfiguration(out string errorMessage)
        {
            errorMessage = null;

            switch (strategyType)
            {
                case InputStrategyType.SingleLabel:
                    if (string.IsNullOrEmpty(singleLabelName))
                    {
                        errorMessage = "SingleLabel strategy requires a label name";
                        return false;
                    }
                    break;

                case InputStrategyType.MultiLabel:
                    if (multiLabelPairs == null || multiLabelPairs.Length == 0)
                    {
                        errorMessage = "MultiLabel strategy requires at least one label-value pair";
                        return false;
                    }
                    break;
            }

            return true;
        }
    }

    /// <summary>
    /// Types of input validation strategies available.
    /// </summary>
    public enum InputStrategyType
    {
        /// <summary>Accepts all items (no filtering)</summary>
        Default,
        /// <summary>Validates based on a single label value</summary>
        SingleLabel,
        /// <summary>Validates based on multiple label-value pairs (OR logic)</summary>
        MultiLabel,
        /// <summary>Admits when an expression over the item and the element holds</summary>
        Condition
    }
}
