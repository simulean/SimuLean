# Editor Design Standard for SimuLean Elements

## Philosophy

All Unity element editors follow a consistent, hierarchical structure that:
- **Prioritizes** commonly used settings
- **Groups** related functionality
- **Separates** core simulation logic from Unity-specific visuals
- **Reveals complexity progressively** through collapsible sections
- **Maintains consistency** across all element types

---

## Standard Section Structure

Every element editor follows this order:

### 1. **CONNECTIONS** (Always Visible)
The most critical configuration - defines the simulation flow.

```
Next Elements     [Array of SElements]
Input Elements    [Array] (only for Combiner, etc.)
```

**Why first?** Without connections, the element is isolated and non-functional.

---

### 2. **CORE PARAMETERS** (Foldout)
Essential settings that define the element's primary behavior.

Common fields:
- Capacity
- Element Name
- Requirements (Combiner)
- Delay Strategy / Process Time
- Expected Items (Sink)

**Default state:** Expanded

---

### 3. **ROUTING & VALIDATION** (Foldout) (new)
Controls how items flow through the element.

```
Output Strategy       [Dropdown: FirstAvailable, ShortestQueue, RoundRobin, etc.]
Input Strategy        [Config with strategy type and parameters]
```

**Why separate?** These are advanced flow-control mechanisms that most users won't need initially, but are powerful when needed.

**Default state:** Collapsed

---

### 4. **ELEMENT-SPECIFIC SETTINGS** (Foldout)
Unique features specific to this element type.

Examples:
- **Combiner:** Batch Mode, Pull Mode, Update Requirements
- **GateQueue:** Release controls
- **Source:** Schedule mode, item types
- **Sink:** Counting options

**Default state:** Collapsed

---

### 5. **VISUAL CONFIGURATION** (Foldout)
Unity-specific rendering and animation settings.

Common fields:
- Item Positions (Transform)
- Prefabs
- Separation distance
- Animators
- Visual modes

**Why separate?** These don't affect simulation logic - purely visual/Unity.

**Default state:** Collapsed

---

### 6. **ADVANCED OPTIONS** (Foldout)
Optional, advanced features for complex scenarios.

Common fields:
- Custom behaviors
- Performance tuning

**Default state:** Collapsed

---

### 7. **LIVE STATUS** (Play Mode Only, Always Visible)
Real-time information visible only during simulation.

```
Live Status
Current State:    PROCESSING
Current Content:  5
Queue Length:     3
```

**Why special?** Provides immediate feedback during debugging.

---

### 8. **HELPER BUTTONS** (Always Visible)
Quick access to add optional components.

```
+ Add Stats Display    + Add Element Labels
Stats Display shows live statistics during Play mode
```

---

## Element-Specific Mapping

| Element | Core Parameters | Specific Settings | Visual | Advanced |
|---------|----------------|-------------------|---------|----------|
| **Queue** | Capacity | - | Position, Separation | - |
| **MultiServer** | Capacity, Delay | Server count | Positions, Animator | - |
| **Combiner** | Requirements, Delay | Batch, Pull mode, Update req | Positions, Prefab, Mode | - |
| **Sink** | Expected Items | Count types | Position | - |
| **Source** | Delay, Item Type | Schedule mode | Prefab, Position | - |
| **GateQueue** | Capacity | Release control | Position, Separation | - |
| **Operator** | Capacity, Load Time | Way Back | Positions | - |

---

## Visual Elements Guide

### Foldout Headers
```csharp
showCoreParams = EditorGUILayout.Foldout(showCoreParams, "Core Parameters", true);
if (showCoreParams) { /* content */ }
```

### Section Spacing
```csharp
EditorGUILayout.Space();  // Between sections
```

### Bold Labels
```csharp
EditorGUILayout.LabelField("Section Name", EditorStyles.boldLabel);
```

### Indentation for Dependencies
```csharp
EditorGUI.indentLevel++;
EditorGUILayout.PropertyField(/* dependent field */);
EditorGUI.indentLevel--;
```

### Help Boxes
```csharp
EditorGUILayout.HelpBox("Explanation text", MessageType.Info);
```

### Disabled Fields (Read-only)
```csharp
EditorGUI.BeginDisabledGroup(true);
EditorGUILayout.IntField("Current Content", value);
EditorGUI.EndDisabledGroup();
```

---

## Implementation Pattern

### Base Structure
```csharp
[CustomEditor(typeof(UnityElementType))]
public class UnityElementEditor : Editor
{
    // Persistent foldout states
    private static bool showCoreParams = true;
    private static bool showRouting = false;
    private static bool showSpecific = false;
    private static bool showVisual = false;
    private static bool showAdvanced = false;

    public override void OnInspectorGUI()
    {
        var element = (UnityElementType)target;
        serializedObject.Update();

        // 1. CONNECTIONS (always visible)
        DrawConnectionsSection();

        // 2. CORE PARAMETERS (foldout)
        showCoreParams = EditorGUILayout.Foldout(showCoreParams, "Core Parameters", true);
        if (showCoreParams) DrawCoreParameters();

        // 3. ROUTING & VALIDATION (foldout)
        showRouting = EditorGUILayout.Foldout(showRouting, "Routing & Validation", true);
        if (showRouting) DrawRoutingSection();

        // 4. ELEMENT-SPECIFIC SETTINGS (foldout)
        showSpecific = EditorGUILayout.Foldout(showSpecific, 
            GetElementSpecificLabel(), true);
        if (showSpecific) DrawElementSpecificSettings();

        // 5. VISUAL CONFIGURATION (foldout)
        showVisual = EditorGUILayout.Foldout(showVisual, "Visual Configuration", true);
        if (showVisual) DrawVisualConfiguration();

        // 6. ADVANCED OPTIONS (foldout)
        showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced Options", true);
        if (showAdvanced) DrawAdvancedOptions();

        // 7. LIVE STATUS (play mode only)
        if (Application.isPlaying) DrawLiveStatus(element);

        // 8. HELPER BUTTONS
        SElementEditorHelper.DrawAddComponentButtons(target);

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawConnectionsSection()
    {
        EditorGUILayout.LabelField("Connections", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("nextElements"));
        EditorGUILayout.Space();
    }

    private void DrawCoreParameters() { /* ... */ }
    private void DrawRoutingSection() { /* ... */ }
    private void DrawElementSpecificSettings() { /* ... */ }
    private void DrawVisualConfiguration() { /* ... */ }
    private void DrawAdvancedOptions() { /* ... */ }
    private void DrawLiveStatus(UnityElementType element) { /* ... */ }
    
    protected virtual string GetElementSpecificLabel() 
    { 
        return "Element-Specific Settings"; 
    }
}
```

---

## Output Strategy Configuration

The new **Output Strategy** field in the "Routing & Validation" section:

### Unity Component Field
```csharp
[Header("Routing & Validation")]
[Tooltip("Strategy for selecting which output receives items")]
public OutputStrategyConfig outputStrategy = new OutputStrategyConfig();
```

### Config Class (similar to InputStrategyConfig)
```csharp
[System.Serializable]
public class OutputStrategyConfig
{
    public OutputStrategyType strategyType = OutputStrategyType.FirstAvailable;
    
    // Strategy-specific parameters
    public string parameterKey = "";  // For ParameterizedRouting
}

public enum OutputStrategyType
{
    FirstAvailable,
    ShortestQueue,
    RoundRobin,
    MostAvailableCapacity,
    PriorityBased,
    Parameterized
}
```

### Property Drawer
Similar to `InputStrategyConfigDrawer`, shows:
- Dropdown for strategy type
- Conditional parameters based on selection

### In Editor
```csharp
private void DrawRoutingSection()
{
    EditorGUI.indentLevel++;
    
    EditorGUILayout.PropertyField(
        serializedObject.FindProperty("outputStrategy"),
        new GUIContent("Output Strategy", 
            "Determines how items are routed to multiple outputs"));
    
    EditorGUILayout.PropertyField(
        serializedObject.FindProperty("inputStrategy"),
        new GUIContent("Input Strategy", 
            "Validates incoming items"), true);
    
    EditorGUI.indentLevel--;
}
```

---

## Implementation Checklist

When creating or refactoring an editor:

- [ ] Use consistent foldout variable names (`showCoreParams`, etc.)
- [ ] Place connections first (no foldout)
- [ ] Add Routing & Validation section with Output/Input strategies
- [ ] Group element-specific settings separately
- [ ] Separate visual configuration from logic
- [ ] Add live status for runtime debugging
- [ ] Include helper buttons at bottom
- [ ] Add tooltips to complex fields
- [ ] Use indentation for dependent fields
- [ ] Test foldout persistence between selections

---

## Benefits of This Design

1. **Discoverability:** New users find basic settings immediately
2. **Progressive Disclosure:** Advanced features don't overwhelm
3. **Consistency:** Same structure across all elements
4. **Efficiency:** Power users can navigate quickly
5. **Debugging:** Live status provides immediate feedback
6. **Extensibility:** Easy to add new sections without breaking layout
7. **Documentation:** Structure itself documents what's important

---

## Notes

- **Static foldout bools:** Maintain state across selections of different objects
- **Tooltips:** Always add for non-obvious fields
- **Default states:** Core expanded, others collapsed
- **Play mode:** Show live data when available
- **Help boxes:** Use sparingly, only for complex concepts

---

**Last Updated:** 2024
**Version:** 1.0
**Status:** Active Standard
