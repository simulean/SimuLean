# SimuLean Core — API reference

`index.html` is a **generated**, self-contained API reference for the simulation
core (`Assets/SimuLean.Net/`). Open it in any browser — no server, no build step,
no external assets.

```bash
python docs/api/generate_api_docs.py
```

## What is in it

- Every **public type** of the core, grouped by subsystem (kernel, elements,
  links, stochastic values, statistics, transport, storage, collections,
  serialization, headless, data).
- For each type: kind badge, namespace, `file:line`, base list, the XML
  `<summary>` from the source, and a table of its **public and protected**
  constructors, properties, methods, events, fields and enum values — each with
  its summary, `<param>` descriptions and `<returns>` text.
- Instant client-side search (press `/`), expand/collapse all, deep links
  (`index.html#t-SimuLean-SimClock`), and light/dark theme following the OS.

Internal and private members are deliberately excluded: this is the surface other
layers are allowed to depend on.

## How it works

`generate_api_docs.py` is a small dependency-free C# source scanner (standard
library only). It tracks namespaces and brace depth, reads declarations and their
preceding `///` documentation comments, and falls back to a plain `//` comment
when no `<summary>` exists. Members whose source carries no comment at all are
rendered as *"No summary in source."* — so the page also works as a documentation
to-do list.

Because it reads the sources directly, **the reference cannot drift from the
code**: re-run the script after changing the core and commit the regenerated
`index.html`.

## Adjusting the output

Everything configurable sits at the top of the script:

| Constant | Purpose |
|---|---|
| `SOURCE_ROOT` | Which tree to document (defaults to `Assets/SimuLean.Net`). Point it at `Assets/SimuLean.Unity` to generate a second reference for the Unity layer. |
| `GROUPS` | Folder → section title + description, and the section ordering in the sidebar. |
| `SKIP_DIR_PARTS` | Directories to ignore (currently `Examples`). |
| `OUTPUT_FILE` | Destination HTML file. |

The `Where to start` table near the top of the page is also defined in the
script (`build_html`), so keep it in sync when a new subsystem lands.

## Related

- [`../architecture.md`](../architecture.md) — the authoritative, exhaustive
  architecture document.
