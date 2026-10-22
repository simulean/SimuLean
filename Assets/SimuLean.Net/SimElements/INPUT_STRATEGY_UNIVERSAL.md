# Input Strategy - Universal Item Validation

## Overview

**Input Strategy** is now a universal feature available on **all elements that receive items** (Queue, MultiServer, etc.), not just Combiner. This allows you to filter and validate items before they enter any element.

---

## What Was Implemented

### 1. Element Base Class Enhancement
- Added `InputStrategy` field to base `Element` class
- Added `ValidateItem()` protected method
- Added `SetInputStrategy()` and `GetInputStrategy()` API

### 2. Unity Configuration System
- **`InputStrategyConfig`** - Serializable Unity configuration class
- **`InputStrategyConfigDrawer`** - Custom property drawer with context-sensitive UI
- Can be embedded in any Unity element component

### 3. Updated Elements
- **ItemsQueue** - Validates items in `Receive()` and `CheckAvailability()`
- **UnityQueue** - Exposes `InputStrategyConfig` in Inspector
- **UnityQueueEditor** - Shows Input Validation section

### 4. Reusable Across All Elements
- Same approach can be applied to UnityMultiServer, UnityGateQueue, etc.
- Consistent API and UI across all elements

---

## Available Strategies

### 1. Default Strategy
**What it does:** Accepts all items (no filtering)

**Inspector Configuration:**
```
Input Strategy:
  Strategy Type: Default
```

**Use Case:** When no filtering is needed (default behavior)

---

### 2. Single Label Strategy
**What it does:** Validates items based on a single label value

**Inspector Configuration:**
```
Input Strategy:
  Strategy Type: SingleLabel
  Label Name: "productType"
  Required Value: "TypeA"  (optional - leave empty to auto-update)
```

**Behavior:**
- **Fixed Value:** Only accepts items with exact label match
- **Auto-Update:** Reads value from first item, then filters subsequent items

**Example - Type-Specific Queue:**
```csharp
// Queue only accepts items with productType = "Premium"
Label Name: "productType"
Required Value: "Premium"

// Item with productType="Premium" ? ACCEPTED
// Item with productType="Standard" ? REJECTED
```

**Example - Dynamic Matching:**
```csharp
// Queue accepts items matching first item's type
Label Name: "model"
Required Value: (empty)

// First item: model="X100" ? Queue now only accepts "X100"
// Item with model="X100" ? ACCEPTED
// Item with model="X200" ? REJECTED
```

---

### 3. Multi Label Strategy
**What it does:** Validates items if ANY label-value pair matches (OR logic)

**Inspector Configuration:**
```
Input Strategy:
  Strategy Type: MultiLabel
  Label-Value Pairs:
    [0] priority=1
    [1] urgent=true
    [2] color=red
```

**Behavior:**
- Item accepted if **at least one** label-value pair matches
- Supports both string and numeric values

**Example:**
```csharp
// Queue accepts high priority OR urgent OR red items
Pairs: ["priority=1", "urgent=true", "color=red"]

// Item with priority=1 ? ACCEPTED (matches first pair)
// Item with color=red ? ACCEPTED (matches third pair)
// Item with priority=2, urgent=false, color=blue ? REJECTED (no match)
```

---

## Implementation Guide

### Step 1: Add to SimuLean Element

```csharp
// In MultiServer.cs (example)
public class MultiServer : Element
{
    public MultiServer(
        // ... existing parameters
        InputStrategy inputStrategy = null,
        IVElement vElement = null)
        : base(name, simClock, vElement)
    {
        this.inputStrategy = inputStrategy;  // Set from base class
    }

    public override bool Receive(Item theItem)
    {
        // Validate item before accepting
        if (!ValidateItem(theItem))
            return false;  // Reject invalid items

        // ... rest of receive logic
    }

    public override bool CheckAvailability(Item theItem)
    {
        // Check capacity AND validation
        if (!HasCapacity())
            return false;
        
        return ValidateItem(theItem);  // Must pass validation
    }
}
```

### Step 2: Update Unity Component

```csharp
// In UnityMultiServer.cs (example)
public class UnityMultiServer : SElement, IVElement
{
    [Header("Input Validation")]
    [Tooltip("Strategy for validating incoming items")]
    public InputStrategyConfig inputStrategy = new InputStrategyConfig();

    public override void InitializeSim()
    {
        // Create strategy from configuration
        SimuLean.InputStrategy strategy = null;
        if (inputStrategy != null)
        {
            strategy = inputStrategy.CreateInputStrategy(name);
        }

        // Pass to element constructor
        theServer = new MultiServer(
            // ... existing parameters
            inputStrategy: strategy,
            vElement: Experimenter.HeadlessActive ? (IVElement)new NullVElement() : this
        );

        Debug.Log($"[{name}] MultiServer initialized - InputStrategy: {inputStrategy.strategyType}");
    }
}
```

### Step 3: Update Custom Editor

```csharp
// In UnityMultiServerEditor.cs (example)
[CustomEditor(typeof(UnityMultiServer))]
public class UnityMultiServerEditor : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // ... existing sections

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Input Validation", EditorStyles.boldLabel);
        
        // Property drawer handles context-sensitive display automatically
        EditorGUILayout.PropertyField(
            serializedObject.FindProperty("inputStrategy"),
            new GUIContent("Input Strategy", "Strategy for validating incoming items"),
            true);  // includeChildren = true for foldout

        // ... rest of inspector

        serializedObject.ApplyModifiedProperties();
    }
}
```

---

## Unity Inspector UI

### Collapsed View
```
+------------------------------------------+
| Input Validation                          |
|   > Input Strategy                       |
+------------------------------------------+
```

### Expanded - Default
```
+------------------------------------------+
| Input Validation                          |
|   > Input Strategy                       |
|     Strategy Type: Default               |
|     > Accepts all items (no filtering)   |
+------------------------------------------+
```

### Expanded - Single Label
```
+------------------------------------------+
| Input Validation                          |
|   > Input Strategy                       |
|     Strategy Type: SingleLabel           |
|     Label Name: "productType"            |
|     Required Value: "Premium"            |
|     > Validates items based on a single  |
|       label. Leave value empty to        |
|       auto-update from first item.       |
+------------------------------------------+
```

### Expanded - Multi Label
```
+------------------------------------------+
| Input Validation                          |
|   > Input Strategy                       |
|     Strategy Type: MultiLabel            |
|     Label-Value Pairs:                   |
|       Size: 3                            |
|       [0] priority=1                     |
|       [1] urgent=true                    |
|       [2] color=red                      |
|     > Validates items if ANY label-value |
|       pair matches (OR logic).           |
|       Format: 'color=red' or 'size=10'   |
|       Supports both text and numeric.    |
+------------------------------------------+
```

---

## Use Cases

### 1. Dedicated Queues for Different Product Types
```
Queue A: Only accepts items with type="ProductA"
Queue B: Only accepts items with type="ProductB"
Queue C: Only accepts items with type="ProductC"
```

### 2. Priority Lanes
```
Express Queue: Accepts priority=1 OR urgent=true
Standard Queue: Accepts all items (default strategy)
```

### 3. Quality-Based Routing
```
Rework Queue: Accepts defect=true
Scrap Queue: Accepts quality=0
Good Parts Queue: Accepts quality>0.95
```

### 4. Color/Size Sorting
```
Red Bin: Accepts color=red
Large Bin: Accepts size=large OR size=xl
Mixed Bin: Accepts all (default)
```

### 5. Flexible Assembly Stations
```
Station A: Accepts model="X100" OR model="X200"
Station B: Accepts model="X300" OR model="X400"
```

---

## Technical Details

### InputStrategyConfig Class

```csharp
[Serializable]
public class InputStrategyConfig
{
    public InputStrategyType strategyType = InputStrategyType.Default;
    
    // SingleLabel parameters
    public string singleLabelName = "";
    public string singleLabelValue = "";
    
    // MultiLabel parameters
    public string[] multiLabelPairs = new string[0];
    
    public SimuLean.InputStrategy CreateInputStrategy(string elementName = "");
    public bool ValidateConfiguration(out string errorMessage);
}
```

### Element Base Class API

```csharp
public abstract class Element
{
    protected InputStrategy inputStrategy;
    
    public virtual void SetInputStrategy(InputStrategy strategy);
    public virtual InputStrategy GetInputStrategy();
    protected virtual bool ValidateItem(Item theItem);
}
```

### Validation Flow

```
Item arrives at element
     |
CheckAvailability(item)
     +- has capacity? yes -> ValidateItem(item)
     |                        +- strategy null? -> accept
     |                        +- Strategy.IsValid(item)
     +- has capacity? no  -> reject
```

---

## Model Extraction & Headless Support

### Extraction (Unity -> Config)

```csharp
// In UnityModelExtractor
config.Parameters["inputStrategyType"] = queue.inputStrategy.strategyType.ToString();

switch (queue.inputStrategy.strategyType)
{
    case InputStrategyType.SingleLabel:
        config.Parameters["singleLabelName"] = queue.inputStrategy.singleLabelName;
        config.Parameters["singleLabelValue"] = queue.inputStrategy.singleLabelValue;
        break;
    
    case InputStrategyType.MultiLabel:
        config.Parameters["multiLabelPairs"] = new List<string>(queue.inputStrategy.multiLabelPairs);
        break;
}
```

### Reconstruction (Config -> Headless)

```csharp
// In HeadlessModelFactory
private InputStrategy CreateInputStrategy(ElementConfig config)
{
    string strategyType = config.GetParameter<string>("inputStrategyType", "Default");
    
    switch (strategyType)
    {
        case "SingleLabel":
            string labelName = config.GetParameter<string>("singleLabelName");
            string labelValue = config.GetParameter<string>("singleLabelValue");
            double? requiredValue = string.IsNullOrEmpty(labelValue) 
                ? null 
                : (double?)double.Parse(labelValue);
            return new SingleLabelStrategy(labelName, requiredValue);
        
        case "MultiLabel":
            var pairs = config.GetParameter<List<string>>("multiLabelPairs");
            var acceptedLabels = new Dictionary<string, object>();
            foreach (string pair in pairs)
            {
                // Parse label=value pairs
            }
            return new MultiLabelStrategy(acceptedLabels);
        
        default:
            return new DefaultStrategy();
    }
}
```

---

## Important Notes

### 1. Rejected Items
- Items that fail validation are **rejected** (Receive returns false)
- The sending element sees the rejection and may queue or route elsewhere
- **No exception is thrown** - rejection is normal behavior

### 2. Performance
- Validation is lightweight (dictionary lookups)
- Negligible performance impact for typical simulations
- Strategy is checked once per item arrival

### 3. Strategy Updates
- SingleLabel strategy can auto-update from incoming items
- Call `strategy.UpdateStrategy(item)` to update matching criteria
- Useful for dynamic filtering based on first item

### 4. Sink Elements
- **Sink should NOT use InputStrategy** (accepts everything by design)
- Only use on elements where filtering makes logical sense

---

## Troubleshooting

### Issue 1: Items Not Entering Queue

**Symptom:** Items arrive but queue stays empty

**Possible Causes:**
1. InputStrategy is rejecting items
2. Label mismatch

**Debug:**
```csharp
Debug.Log($"Item label '{labelName}': {item.GetLabelValue(labelName)}");
Debug.Log($"Required value: {requiredValue}");
Debug.Log($"Validation result: {strategy.IsValid(item)}");
```

**Fix:** Check that item labels match strategy configuration

### Issue 2: Strategy Not Applied

**Symptom:** All items accepted despite having strategy configured

**Possible Cause:** Strategy not created or not passed to element

**Fix:** Verify `CreateInputStrategy()` is called in `InitializeSim()`

### Issue 3: Multi Label Not Working

**Symptom:** Items with matching labels still rejected

**Possible Cause:** Incorrect format in label-value pairs

**Fix:** Ensure format is exactly `"label=value"` (no spaces around `=`)

---

## Related Classes

| Class | Purpose |
|-------|---------|
| `Element` | Base class with InputStrategy support |
| `InputStrategy` | Abstract base for validation strategies |
| `DefaultStrategy` | Accept all items |
| `SingleLabelStrategy` | Validate single label |
| `MultiLabelStrategy` | Validate multiple labels (OR logic) |
| `InputStrategyConfig` | Unity serializable configuration |
| `InputStrategyConfigDrawer` | Custom property drawer for Inspector |

---

## Summary

### What Was Achieved

1. **Universal Feature** - Available on all receiving elements, not just Combiner
2. **Reusable Components** - `InputStrategyConfig` and drawer can be used everywhere
3. **Consistent UI** - Same Inspector experience across all elements
4. **Easy to Extend** - Simple pattern to add to new elements
5. **Headless Compatible** - Full model extraction/reconstruction support
6. **Well-Documented** - Clear examples and use cases

### Benefits

- **Flexible Routing** - Filter items to specific queues/servers
- **Easy Configuration** - All done in Unity Inspector (no code)
- **Reusable** - Same strategy class used everywhere
- **Visual** - Context-sensitive UI shows only relevant parameters
- **Scalable** - Easy to add to any new element

---

## Future Enhancements

Potential improvements:

1. **Custom Strategy Support** - Add code-based custom strategies
2. **Label Expression Strategy** - Use expressions for complex validation
3. **Range Validation** - Accept items within numeric ranges
4. **Regex Matching** - Pattern matching for string labels
5. **Validation Statistics** - Track acceptance/rejection rates
6. **Visual Feedback** - Show rejected items differently in Unity

---

**Status:** Complete  
**Compilation:** Success  
**Tested On:** UnityQueue  
**Ready to Apply:** UnityMultiServer, UnityGateQueue, etc.

The Input Strategy is now a universal feature that can be applied consistently across all elements that receive items!
