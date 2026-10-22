# Optional runtime tools (clock pacing, camera, HUD, element inspector, floor plan, building kit)

Everything in this guide is **opt-in**. Nothing is added to a scene automatically, the sample
scenes do not use these components, and the `UnitySimClock` defaults keep the legacy behaviour
— add a component or switch an option only when you want it. Architecture:
`docs/architecture.md` §4.3, §4.7.1, §4.7.2, §4.8, §4.9.

| Tool | Where | Enable it by |
|---|---|---|
| Paced / maximum-speed clock | `UnitySimClock` | inspector foldout *Optional runtime features*: *Frame Budget (ms)* > 0; *Run As Fast As Possible* under *Simulation Controls* (Edit or Play mode) or `clock.RunAsFastAsPossible = true` |
| Headless run (results only) | `UnitySimClock` | *Run Headless* button under *Headless Run*: runs the scene to the end without views on a worker thread and shows a per-element table (CSV export) |
| Auto start, Esc behaviour | `UnitySimClock` | *Auto Start On Play*; *Escape Quits* (default on) |
| Reproducible runs (seed) | `UnitySimClock` | inspector *Random Numbers*: *Seed Mode* = *Fixed*, or **Keep** the last run's seed (also on the HUD) |
| Orbit camera + picking | `Core/Camera/SimCameraController` | add to the Camera |
| Runtime HUD | `Core/Hud/SimRuntimeHud` | add to any GameObject |
| Element inspector | `Inspector/ElementInspectorPanel` | *Add Component → SimuLean → Runtime → Element Inspector Panel* |
| Floor-plan underlay | `Visuals/FloorPlanUnderlay` | *Add Component → SimuLean → Visuals → Floor Plan Underlay* + a texture from `Tools/FloorPlan` |
| Walls, columns, crane | `Visuals/Building/*` | *GameObject → SimuLean → Building* menu |
| 3D models | `Art/Models/{Forklift, RailShuttle, OverheadCrane, IndustrialBuilding}` | drag the FBX / use them from your `IVElement` |

## Clock pacing (large speed-ups)

The legacy loop advances the simulation to `(Time.time − start) × TimeScale` once per frame. With a
large `TimeScale` a slow model then freezes the view while it catches up. **Paced mode**
(*Frame Budget (ms)* > 0) advances in slices until the frame's wall-clock budget is used and drops
the lag instead; **maximum speed** (`RunAsFastAsPossible`) ignores `TimeScale` and runs as far as
the budget allows every frame (default 40 ms). `EffectiveSpeed` reports the achieved sim-units per
real second. Change the speed while running with the `TimeScale` property (continuous, does not
throttle the frame rate).

To get only the results, faster still, use **Run Headless** instead: the scene is extracted
(max time, warm-up, seed, calendar from the clock), rebuilt without views and run on a worker
thread, in Edit or Play mode; the scene itself does not move. It uses the clock's seed (see *Seed* below).

## Seed (reproducible runs)

Every element draws its random numbers from its own stream, derived from the **model seed** and the
element's name, so adding or removing an element does not change the numbers the others draw, and a
headless run of the same model with the same seed draws the same numbers per element.

- *Seed Mode* **Random each run** (default): a new seed is drawn before every Run / Restart. It is
  shown in the inspector (*Seed of the last run*), in the HUD and in the Console, so a run can be
  reproduced: press **Keep** to switch to *Fixed* with that seed.
- *Seed Mode* **Fixed**: every run uses *Seed* (exported as the headless seed).
- **New** draws a new random seed and fixes it (you see it before running).
- In the HUD, typing a number in the *Seed* field fixes it for the next run; the *random* toggle
  goes back to a new seed each run; **Restart** reruns with the seed shown.
- Seed changes made during Play mode (Keep / New / typed) are kept when Play mode ends.

## Camera and HUD

- `SimCameraController` (FlexSim-style): left- or middle-drag grab-pans (the grabbed ground point
  stays under the cursor; `leftDragPans` off keeps left for selection only), right-drag orbits
  about the ground point at the screen centre, the wheel zooms towards the cursor by a constant
  ratio per notch (never below `minHeight`), W A S D / arrows pan and Q/E move down/up at a speed
  proportional to the camera height (Shift faster); left-click without drag selects, F frames the
  selection, 1–9 preset views (`presets`), R reset (`resetKey`, `presetKeys` to disable the
  number keys). Light smoothing (`smoothTime`) in unscaled time. `SelectionChanged` event.
  Check: menu *SimuLean › Checks › Camera controller check*.
- `SimRuntimeHud`: date / sim time, Run / Pause (Space), ×0.5 / ×2 (+/−) and speed presets, the
  seed row (see *Seed* above) with Restart, a views list, the selected element's state / content / in / out, and a key legend (H hides it).
  Other IMGUI panels call `SimRuntimeHud.RegisterScreenRect` so the camera ignores the mouse over
  them.

## Element inspector

Click an element (via `SimCameraController`, or a raycast from the main camera when there is none)
to open the panel:

- **KPIs** — state, active stops, content, free capacity (or occupied length for a
  `LengthLimitedQueue`), input / output and rates, time-weighted content average and max, stay
  times, utilisation (the share of time processing; a Multi-Server: the mean of its slots), stops, time per state.
- **Charts** — state split per period (per calendar day when the clock has a start date, else
  *MaxTime / 40*), cumulative split, content mean / max over the run and in the recent buckets.
- **Detail** — type, scene object, capacity / lane set-up, strategies, calendar.

It is **read-only**: it only reads the Link-layer statistics, state logs and the time-weighted
content integral once per frame, so simulation results are identical with or without it. Settings:
`refreshHz` (default 4), `recordHistory`, `historyBucket` / `statePeriod` (0 = automatic),
`timeUnitLabel`, `closeKey` (default Esc; the clock's Esc-quits is suppressed that frame).

**Adding model-specific rows** — implement `IElementInspectorContributor` (or derive from
`ElementInspectorContributor`):

```csharp
public class MyLineRows : ElementInspectorContributor
{
    public override bool Supports(Element e) => e is MyLine;
    public override void AddKpis(Element e, ElementInspectorContext c, List<InspectorRow> rows)
    {
        var line = (MyLine)e;
        rows.Add(InspectorRow.H("Line"));
        rows.Add(new InspectorRow("Good parts", ElementInspectorContext.N(line.GoodParts), c.Rate(line.GoodParts)));
    }
}
```

Put it on the element's GameObject (picked up automatically), list it in the panel's
`contributors`, or call `ElementInspectorPanel.RegisterContributor(...)` from code. Contributors
must not change the model.

Only the elements in the panel's **Inspectable Elements** list open it when clicked. The list fills
itself with every element of the scene (also the ones you create later); remove an element by hand
or press **Clear** and it is not added again — **Add All Scene Elements** lists everything again.

## Floor-plan underlay

1. Make the texture with `Tools/FloorPlan/make_floor_texture.py` (README there: LibreDWG for DWG,
   configurable layer styles, `--list-layers`).
2. Import the PNG (sRGB off, mipmaps on, single channel) and add *Floor Plan Underlay*; assign
   `texture` and `placementJson`.
3. Align with `drawingOrigin` (drawing point at the GameObject origin) and the GameObject
   position / rotation; tune colour, opacity, `toggleKey` (default P).

Drawings are often confidential: keep them and the generated texture out of shared repositories.

## Building kit and models

`UnityModularWall`, `UnitySteelColumn`, `UnityModularStrip` (beams, crane runways) and
`UnityOverheadCrane` assemble layouts from the kit FBXs — set sizes and press *Rebuild* (never scale
the GameObject). `UnityIndustrialDoor` animates doors; `UnityOverheadCrane.MoveTo / MoveHookTo`
animate the crane. They are visual only (no `SElement`). The model import conventions are applied
by `SimuLeanModelPostprocessor` to library folders only (`<Folder>/<Folder>_Albedo.png`); models are
regenerated with the Blender scripts in `Tools/Blender/`.

## Element Label

*SimuLean window ▸ Settings ▸ Visualization ▸ Element Label* (or *Add Component ▸ SimuLean ▸ Element Label*), on an
element or on its own GameObject:

- **On an element** (the component on the element or on a child): `text` with `{n}` — what the element holds now — and
  `{name}`, e.g. `{n} patients waiting (at home)`. Tick **Hide items** to draw the element as a number instead of a crowd
  (the items still flow; only their drawing is off).
- **Without an element**: a zone label ("Waiting room", "Diagnostic unit").
- **Facing**: *Camera* (upright, always readable) or *Floor* (lying on the floor, read from above). A recording camera can
  be set with `UnityElementLabel.ViewCamera`.

It is a view: the run is the same with or without it.

## Speed Profile and Recorder

*SimuLean window ▸ Settings ▸ Visualization*:

- **Speed Profile** — the playback speed by the model's calendar: *Open Hours* (e.g. `Mon-Fri 08:00-15:00`) at *Open
  Speed*, the rest at *Closed Speed* (nights and weekends fly by), and *Walking Speed* while someone walks (at most
  *Max Slow Seconds* per walk). Speeds are model time units per second (with the model in hours, 0.08 ≈ 5 minutes per
  second).
- **Recorder** — a demo video in Play mode: *Start At* (a model date; the clock runs there first, without recording),
  then the *Shots*, at *Fps* and the frame size. Each shot has a **framing** — *Fixed* (a camera position, taken from the
  Scene view), *Pose* (an empty GameObject), *Element* (an element seen from a *Distance*, *Elevation* and *Azimuth*: it
  follows the element when the layout changes) or *Orbit* (the same, turning *Orbit Degrees* during the shot) — its
  *Seconds*, *Glide* (a smooth curve from where the previous shot ended; *Glide Seconds*, 0 = the whole shot) and a
  caption *Title* / *Subtitle*. In the inspector: **Add shot from view** (where the Scene view looks now), **Frame
  selection** (the selected element at the Scene view's angle and distance), *View* / *Set* per shot and a *Preview*
  slider along the fly-path (drawn in the Scene view while the Recorder is selected).
  **Captions** burnt into the video: the shot's title, the model date and time (top right; *Clock Format*, *Culture*
  e.g. `es-ES`) and *Live Values*, lines such as `{content:Waiting Room} patients waiting` (`{content:X}`, `{input:X}`,
  `{output:X}` of an element by name, `{time}`, `{shot}`).
  **Output**: *Mp4* — Unity's own encoder, nothing to install — or *Ffmpeg* (JPG frames encoded by ffmpeg on the PATH
  or *Ffmpeg Path*) or *Frames*. **Quality**: *Standard* (small files), *High* (the default: sharp detail in motion,
  ≈ 8 Mbps at 1280×720 and 30 fps) or *Maximum* (≈ 17 Mbps); *Bitrate Mbps* sets the MP4's bitrate directly. The
  inspector shows the bitrate and the expected file size. Each take goes to `Recordings/<scene>_<time>` in the project (`video.mp4`,
  `frames.csv`). Through the Unity MCP: `simulean_record_shot`, `simulean_record_start`, `simulean_record_status`
  (a take in three calls). Disable the Recorder component to play the scene by hand without recording; a take started
  from the MCP still records.

Both are views: the results are the same with or without them.

## Floor Zone and decoration

*SimuLean window ▸ Settings ▸ Visualization ▸ Floor Zone* (or *Presets ▸ Healthcare ▸ Decor ▸ Floor Zone*): a coloured
area on the floor with its name — a waiting area, a unit, a corridor — read from above. Set *Size* (m), *Color* and
*Label*. The other *Decor* presets place library models with no element (Hospital Bed, Stretcher, Wheelchair, Side
Table); through the Unity MCP, `simulean_place_model` places any library model (`HospitalBed`,
`WaitingChairs/Seat_Table`, an asset path) under a *Decor* group. Decoration never changes the results.
