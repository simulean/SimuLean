# Expressions — one syntax for every decision

Wherever a property of the item or of the element can drive a decision, SimuLean uses the same expression
syntax (engine: `SimuLean.Expressions.Expression`, parsed once, evaluated without allocating):

| Where | Field | Context | Result |
|---|---|---|---|
| Process / cycle / transport times | delay *Expression* (`RandomProcessConfig`) | item being processed + station | the time (negative → 0) |
| Request priority (Resources ▸ Process / Transport) | *Priority* | item + station | higher first (with the team's request order `priority desc`) |
| Routing | output strategy *Expression* | item + routing element | the output index (0 = first) |
| Admission | input strategy *Condition* | item + receiving element | admitted when true |
| Lists | calculated field (*Expression* kind) | the list entry | the field's value |

## Syntax

- **Values:** numbers `12`, `3.5`, `1e3`; texts `'red'` or `"red"`.
- **Properties:** `item.weight`, `element.speed` (`station.speed` is the same), `item['any name']`.
  A **bare name** (`weight`) is the context's default: the item's property for times, priorities, routing and
  admission; the entry's field in a list (where `item.x` still reads the entry's item).
- **Built-in values** (when no property has that name): `item.id`, `item.type`, `item.priority`,
  `item.created`, `item.age`; `element.name`, `element.content`, `element.free`.
- **Operators:** `+ - * / %` (division by 0 gives 0), comparisons `== != < <= > >=` (true = 1), `&& || !`,
  `condition ? a : b`, parentheses.
- **Texts:** compare with `==` / `!=` (`item.color == 'red'`); a missing value equals the empty text `''`; a text
  compared with a number compares as numbers (`'007' == 7`).
- **Functions:** `min(a, b, …) max(…) abs floor ceil round sqrt pow(x, y) if(c, a, b) now() has(item.x)`.
- **Data tables** (the model's named tables: a *Data Table* in the scene, Library ▸ Data):
  - `table('t', key, 'col')` — the cell of the first row whose **first column** equals `key`;
    `table('t', key, 'col', 'keyCol')` looks the key up in another column. A number matches a cell of the same value
    (`3` finds `'3.0'`).
  - `cell('t', row, 'col')` — by row number, from 1.
  - `rows('t')` — how many rows; `rows('t', 'col', value)` — how many with that value.
  - `sum('t', 'col')`; `sum('t', 'col', 'keyCol', key)` — of the rows with that key.

  A cell that reads as a number is a number, else a text; no such row (or an empty cell) is a missing value (0). An
  unknown table or column stops the run with its name. Tables are read once when the run starts, never during it.
- **Missing** properties count as 0 (or as no text) — use `has(item.x)` to test for them.

## Examples

```
processingTime                                   # a label of the item (bare name)
item.weight * element.secondsPerKg + 2           # item and station properties
item.color == 'red' ? 5 : 3                      # decision on a text property
item.prio * 10 + item.age                        # priority: urgency grows while waiting
item.type == 'A' ? 0 : 1                         # routing: first output for type A
item.weight <= element.maxWeight && has(item.code)   # admission condition
prio * 10 + age                                  # list field over other fields
table('times', item.kind, 'time')                # process time per item kind, from a data table
table('setup', item.kind, 'minutes', 'product') * 60   # another key column, unit conversion
sum('orders', 'qty', 'line', element.name)       # a station's total from an order table
```

Element properties are set in the scene with *Element Labels* (numbers, texts, references to other elements);
item properties are labels set by sources (`assignLabels`), stations or code.

Syntax errors are reported with their position (in the inspectors and when the model is built). The former
`LabelExpression` (`item.GetLabelValue('x')`, regex + `DataTable`) and `FieldExpression` were replaced by this
syntax; the sample scenes and prefabs were migrated to the new fields (`expression`, `priority`) and a
bare label name is a valid expression.
