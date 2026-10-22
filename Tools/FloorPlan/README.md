# Floor-plan underlay pipeline (CAD drawing → texture → Unity)

Lays a factory drawing on the floor under a SimuLean Unity model: the Python script turns a DWG/DXF into a
single-channel line texture plus a placement JSON, and the optional component
`Assets/SimuLean.Unity/Visuals/FloorPlanUnderlay.cs` (shader `SimuLean/FloorPlanUnderlay`) draws it as one
unlit, alpha-blended quad that can be toggled at run time (key **P** by default). Purely visual: no colliders,
no simulation events. See `docs/architecture.md` §4.7.2.

> **Drawings are often confidential.** The texture is a faithful copy of the drawing: keep the source drawing,
> the generated PNG/JSON and the `work/` cache out of shared repositories unless you are allowed to publish
> them (`Tools/FloorPlan/work/` and `Tools/LibreDWG/` are git-ignored; put model-specific outputs in a
> git-ignored folder of your model).

## 1. Requirements

- Python 3.10+ with `ezdxf`, `numpy`, `pillow`:
  ```powershell
  py -3.12 -m venv Tools/FloorPlan/.venv
  Tools/FloorPlan/.venv/Scripts/python.exe -m pip install ezdxf numpy pillow
  ```
- **DWG input only:** LibreDWG's `dwg2dxf` (GPL-3, *not bundled*). Get a Windows build from the LibreDWG
  releases (<https://github.com/LibreDWG/libredwg/releases>, `libredwg-*-win64.zip`) or your package manager
  (`apt install libredwg-tools`, MSYS2 `mingw-w64-x86_64-libredwg`). The script looks for it in this order:
  `--dwg2dxf <path>`, environment variable `LIBREDWG_DWG2DXF`, `Tools/LibreDWG/dwg2dxf(.exe)` (unzip it there),
  then `PATH`. Alternatively export a DXF from your CAD tool and pass `--dxf`.

## 2. Make the texture

```powershell
$py = "Tools/FloorPlan/.venv/Scripts/python.exe"
# 1) see which layers the drawing has, how many polylines each, and which style group they fall in
& $py Tools/FloorPlan/make_floor_texture.py --dwg C:/drawings/plant.dwg --list-layers
# 2) copy layer_styles.example.json, adapt the patterns to those layer names, then render
& $py Tools/FloorPlan/make_floor_texture.py --dwg C:/drawings/plant.dwg --config my_styles.json `
      --out-dir Assets/MyModel/FloorPlan --name floorplan --crop 0 0 180 120 --debug
```

- Steps: DWG → DXF (cached in `work/`), DXF → polylines per layer in metres (ezdxf `recover`, INSERTs exploded
  recursively with layer-0 inheritance, curves flattened; cached as `work/<name>.segments.npz`), layers → style
  groups → anti-aliased grey texture (one channel = line coverage × group intensity, sides a multiple of 4).
- `units_to_metres` (config or `--units-to-metres`): drawing units → metres (mm drawings: `0.001`).
- `--crop X0 Y0 X1 Y1`: window in drawing metres (default: extents of the kept layers — pass a crop when the
  drawing has far-away title blocks). `--max-px` (default 8192) is the longest side; `--ss` the supersampling.
- Layer styles (`layer_styles.example.json`): case-insensitive glob patterns per group, `drop` patterns (win over
  groups), line width in metres, grey intensity, optional `label_layers` whose TEXT/MTEXT are drawn as labels.
- Outputs: `<name>.png` (grayscale) and `<name>.json`:
  `{"rect_m": [x0, y0, x1, y1], "pixels_per_metre": …, "size_px": [w, h], "source": …}` — `rect_m` is the exact
  drawing rectangle the pixels cover (image row 0 = max y). `--debug` also writes `work/<name>_debug.png` with
  each group in colour.

## 3. Use it in Unity

1. Import the PNG: *Texture Type* Default, *sRGB* off, *Alpha Source* None, *Generate Mip Maps* on,
   *Max Size* ≥ the image side (e.g. 8192), *Format* R8 or BC4 (single channel) to save memory. Import the JSON
   as a TextAsset (automatic for `.json`).
2. Add *SimuLean → Visuals → Floor Plan Underlay* to an empty GameObject; assign `texture` and `placementJson`
   (or type the rectangle into `drawingRect` when you have no JSON).
3. Align: `drawingOrigin` is the drawing point (metres) placed at the GameObject's origin (drawing +X → local
   +X, drawing +Y → local +Z); move / rotate the GameObject to fit the model (keep scale 1 when the model is in
   metres). The selected component draws the covered rectangle as a gizmo.
4. Tune `lineColor`, `opacity`, `height`; `toggleKey` (default P, `None` = no key) and `visibleAtStart`, or call
   `Visible = …` from your own UI. Missing texture → the component disables itself with a log message.
