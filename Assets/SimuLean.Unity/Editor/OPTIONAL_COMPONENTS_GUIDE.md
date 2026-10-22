# Optional Components for SElements

## Overview

All SElement editors now include quick-access buttons to add optional components for enhanced functionality:

- **Stats Display**: Shows live statistics during Play mode
- **Element Labels**: Adds configurable key-value pairs for expressions

## Features

### Automatic Button Display

The optional components section appears at the bottom of any SElement custom editor inspector, showing:

- Checkmark if component is already added
- `+ Add` button if component is missing
- Help text explaining what each component does

### Smart Detection

- Buttons are **disabled** if the component is already attached
- Prevents duplicate components (Unity's `[DisallowMultipleComponent]` attribute)
- Uses `Undo.AddComponent` for proper undo/redo support

## Usage

### In UnityMultiServer, UnityCombiner, UnitySource (or any SElement)

1. Select the GameObject with an SElement component
2. Scroll to the bottom of the Inspector
3. See the **Optional Components** section
4. Click `+ Add Stats Display` or `+ Add Element Labels` as needed

### Stats Display Component

Shows during Play mode:
- **Current State** (IDLE, PROCESSING, BLOCKED, etc.)
- **Throughput**: Items In, Items Out
- **Content**: Current and Peak
- **Stay Time**: Average, Min, Max per item

### Element Labels Component

Allows you to define key-value pairs that can be used in:
- Expression-based delays
- Custom strategies
- Conditional logic
- Item routing decisions

Example labels:
- `efficiency`: 0.95
- `setupTime`: 10.0
- `capacity`: 100.0

## Implementation Details

### Helper Class

The functionality is provided by `SElementEditorHelper.cs`:

```csharp
UnitySimuLean.Editor.SElementEditorHelper.DrawAddComponentButtons(target);
```

This single line in any SElement editor's `OnInspectorGUI()` method adds the buttons.

### Current Integration

Updated editors:
- `UnityMultiServerEditor`
- `UnityCombinerEditor`
- `UnitySourceEditor`

### Adding to Future Editors

To add this functionality to any new SElement custom editor:

```csharp
[CustomEditor(typeof(YourSElement))]
public class YourSElementEditor : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        // ... your custom inspector code ...
        
        // Add at the end, before ApplyModifiedProperties
        UnitySimuLean.Editor.SElementEditorHelper.DrawAddComponentButtons(target);
        
        serializedObject.ApplyModifiedProperties();
    }
}
```

## Benefits

- **Convenience**: One-click component addition
- **Consistency**: Same interface across all SElements
- **Safety**: Prevents duplicate components
- **Discovery**: Users learn about available features
- **Undo Support**: Proper Unity undo/redo integration

## Related Components

- `SElementStatsDisplay.cs` - Statistics display component
- `SElementLabels.cs` - Labels management component
- `SElementStatsDisplayEditor.cs` - Custom editor for stats
- `SElementLabelsEditor.cs` - Custom editor for labels

---

**Last Updated**: 2025
**Status**: Implemented and tested
