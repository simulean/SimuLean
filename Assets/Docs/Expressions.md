# Expressions — one syntax for every decision

Wherever a value of the model drives a decision, SimuLean uses one expression syntax (engine:
`SimuLean.Expressions.Expression`, parsed once, evaluated without allocating). What a name means depends only on
**where the expression is written** (its *context*): the context says which **roots** it reads, what a **bare name**
is and which **function families** it may call. The same value has one name everywhere.

## Where expressions are written — the contexts

| Context (`simulean_vocabulary` key) | Fields | Roots | Bare name | Random | Model | Tables |
|---|---|---|---|---|---|---|
| a time (`time`) | process / cycle / transport / setup / hold / delay / split / inter-arrival times: sampler type *Expression* | `item`, `element` (the element whose time it is) | item label | ✓ | ✓ | ✓ |
| a request priority (`priority`) | Resources ▸ Process / Transport *Priority*, assembly tasks | `item`, `element` (the requesting station) | item label | ✓ | ✓ | ✓ |
| a routing expression (`routing`) | output strategy *Expression* | `item`, `element` (the router) | item label | ✓ | ✓ | ✓ |
| a routing score (`score`) | output strategy *BestScore* (`scoreExpression`): one value per next element, the highest wins | `item`, `element` (the router), `out` (the next element scored) | item label | — | ✓ | ✓ |
| a queue's offer order (`offerSort`) | Queue *Offer by* (`offerSort`): each order key | `item` (the held item), `element` (the queue) | item label | — | ✓ | ✓ |
| an admission condition (`admission`) | input strategy *Condition* | `item`, `element` (the receiver), `origin` (the sender) | item label | ✓ | ✓ | ✓ |
| a label record (`label`) | creation, entry and exit labels (`name = expression`) | `item`, `element` | item label | ✓ | ✓ | ✓ |
| an item statistic (`tally`) | *Item statistics* (`name = expression`) | `item`, `element` | item label | ✓ | ✓ | ✓ |
| a max wait (`maxWait`) | queue reneging, resources' max wait, list pull max wait | `item`, `element` (the waiting element) | item label | ✓ | ✓ | ✓ |
| an agenda's probability (`agenda`) | no-show, cancellation | `item`, `element` (the agenda) | item label | ✓ | ✓ | ✓ |
| a call-off condition (`callOff`) | call-off family condition | `item`, `element` (the paced line) | item label | ✓ | ✓ | ✓ |
| a sequencing condition (`sequencing`) | sequencing station *Applies* | `item`, `element` (the station) | item label | ✓ | ✓ | ✓ |
| a milk run's filter (`milkRunFilter`) | flow filter, empties filter | `item` (the carrier), `element` (the milk run), `origin` (the carrier's origin) | item label | ✓ | ✓ | ✓ |
| a resource member's property (`memberProperty`) | team member *Properties* (`name = expression`, evaluated once at build) | `element` (the member) | the member's property | ✓ | ✓ | ✓ |
| a list's calculated field (`listField`) | list *Fields*, kind *Expression* | `item` (the entry's item), `origin` (where it comes from), `entry` | the entry's field | ✓ | ✓ | ✓ |
| a milk run's request field (`requestField`) | milk run *Request fields* (`name = formula`) | `item` (the carrier), `origin`, `destination`, `entry` (the request) | the request's field | ✓ | ✓ | ✓ |
| a reaction, item signal (`reaction`) | condition, action values, *only if* | `item`, `element` (the signalling element), `signal` | item label | ✓ | ✓ | ✓ |
| a reaction, element signal (`reactionElement`) | as above, on StateChanged, stops… | `element`, `signal` | — | ✓ | ✓ | ✓ |
| a reaction, time signal (`reactionTime`) | as above, on Every / AtTime / RunStarted | `signal` | — | ✓ | ✓ | ✓ |
| a fitness (`fitness`) | optimiser *Fitness* | — | a metric or numeric parameter | — | — | — |
| a study formula (`study`) | study `where`, `derived` | — | a factor / a metric | — | — | — |

A root or a function the context does not serve is refused when the text is parsed — in the inspector as you type, by
`simulean_set` / `simulean_apply`, and when the model is built (`'origin.' is not available in a time (roots here:
item, element)`).

Random functions draw from the use's own stream (owner = its element, purpose = the field: `M1/routing`,
`M1/entryLabel:reps`, `M1/priority:processBy.priority`, `milk/filter:F1`, `L/field:score`, `reaction:R/expr`), so
changing one random expression leaves the draws of every other as they were (common random numbers), and a run is
reproducible for a model seed.

## Syntax

- **Values:** numbers `12`, `3.5`, `1e3`; texts `'red'` or `"red"` (a doubled quote inside: `'it''s'`); `true` / `false`
  (the numbers 1 / 0). Every number is finite: a value that is NaN or infinite stops the run (below).
- **Conditions** (`?:`, `&&`, `||`, `!`, `if`, admission, filters, reaction conditions, a study's `where`) are numbers or
  `true` / `false`: 0 and a missing value are false, any other number true. **A text is not a condition** — compare it
  (`item.color == 'red'`) or test it with `has(item.color)`. A constant text where a condition goes (`'yes' ? 1 : 0`) fails
  when the text is parsed; a text read at run time (`item.color ? 1 : 0`) stops the run.
- **Names are case-sensitive.** Functions and roots are lower-case: `max(…)`, `item.x`; `MAX(1, 2)` or `Item.x` fail when
  parsed, with "did you mean 'max'".
- **Roots** — one meaning each:
  - `item.x` — the item: its label `x`, else the built-in `id`, `type`, `priority`, `created` (when its source created
    it), `age` (now − `created`: since creation, not since entering this element; in a list's order or pull query a bare
    `age` is the time since the entry was pushed);
  - `element.x` — the element of the context: its property `x` (*Element Properties*, or set by a reaction), else the
    built-in `name`, `state` (its current state's name: `'IDLE'`, `'PROCESSING'`, `'BLOCKED'`…), `content`, `free`;
  - `origin.x` / `destination.x` — the element an item comes from / goes to (same properties and built-ins);
  - `out.x` — in a routing score, the next element being scored (same properties and built-ins; `out.name` gives its
    name to the model functions: `count(out.name, 'family', item.family)`);
  - `entry.x` — a list entry's field (the bare name in list and request fields);
  - `signal.x` — what a reaction's signal carries: `time`, and a timeout's `kind`, `waited`, `action`.

  `root['any name']` reads a name with spaces. A **bare name** is the context's default root (see the table).
- **Operators:** `+ - * / %` (a division by zero stops the run), comparisons `== != < <= > >=` (true = 1), `&& || !`,
  `cond ? a : b`, `a ?? b` (a, or b when a is missing), parentheses. **Arithmetic takes numbers**: `+ - * / %`, a minus
  sign and the math / random functions refuse a text — `'a' + 1`, `-'x'`, `'3' * 2` (`'3'` is a text, `3` a number) fail
  when parsed; a text read at run time (`item.color + 1`) stops the run. There is no text concatenation.
- **Texts** compare exactly (case-sensitive, ordinal): `item.color == 'red'`, `element.state == 'IDLE'`; two texts order
  as texts with `< <= > >=` (`'abc' < 'abd'`); a text that reads as a number compares as a number with a number
  (`'007' == 7`); a missing value equals the empty text `''`.
- **Missing values** count as 0 (or as no text). Say so when it is expected: `has(x)` tests any value — `has(item.x)`,
  `has(get('M1', 'speed'))`, `has(table('times', item.kind, 'time'))` — and `item.x ?? 5` gives a default. A label record
  that counts is `reps = (reps ?? 0) + 1`.

## Functions

| Family | Functions |
|---|---|
| math | `min(a, b, …)` `max(…)` `abs` `floor` `ceil` `round` (half away from zero) `sqrt` `pow(x, y)` `ln(x)` (x > 0) `exp(x)` `clamp(x, lo, hi)` (lo ≤ hi) `if(c, a, b)` `has(x)` (x not missing) `now()` |
| random | `rand()` (0–1), `uniform(min, max)` (min ≤ max), `exponential(rate)` (rate > 0; mean 1 / rate, the Exponential sampler's parameter), `normal(mean, sd)` (sd ≥ 0), `triangular(min, mode, max)` (min ≤ mode ≤ max, the Triangular sampler's), `lognormal(mu, sigma)` (sigma ≥ 0; mu and sigma of ln x, the LogNormal sampler's: mean e^(mu + sigma²/2)), `pick(w1, v1, w2, v2, …)` (`v1` with weight `w1`…; weights ≥ 0, all 0 = missing) |
| model | `content('X')`, `free('X')` (items held / room left), `get('X', 'p')` (a property or built-in of element X), `stat('X', 'statistic')`, `team('T', 'value')`, `list('L', 'value')`, `count('X', 'field', value)` |
| tables | `table('t', key, 'col'[, 'keyCol'])`, `cell('t', row, 'col')`, `rows('t'[, 'col', value])`, `sum('t', 'col'[, 'keyCol', key])` |

- **`stat('M1', 'output')`** reads an element's statistic, live, by its result name — `input`, `output`, `content`,
  `content_avg`, `content_max`, `stay_avg`, `stay_std`, `stay_min`, `stay_max`, `stay_count`, `utilization`,
  `utilization_onshift`, `stops`, `last_entry`, `last_exit`, `setups`, `setup_time`, `setup_time_avg`, `timeouts`,
  `interruptions`, `state:PROCESSING` (share of time), `tally:NAME:mean` and the element's own statistics. The same
  reader fills the results (`RunResults`), so a live read and the result agree; it counts from the start of the run
  (or the warm-up, once the statistics are reset). A statistic the element does not have yet (no last entry) is missing.
- **`team('Welders', …)`**: `free` (members free now), `busy` (on a task), `waiting` (requests waiting), and the team's
  results `members`, `utilization`, `served`, `tasks`, `request_wait_avg`, `request_wait_std`, `requests_waiting_avg`.
- **`list('Orders', …)`**: `content` (entries now), `pending` (pulls waiting), `pushed`, `pulled`, `content_avg`,
  `content_max`, `wait_avg`, `wait_std`, `pending_pulls_avg`, `field:F:push_avg` of a monitored field.
- **`count('M1', 'type', 'A')`**: the items element M1 holds whose label or built-in equals the value; with a list's
  name, its entries whose field equals the value.
- **Data tables** (*Data Table* in the scene, Library ▸ Data): `table` finds the first row whose key column (the first
  by default) equals the key (`3` matches `'3.0'`); `cell` by row number from 1; `rows` counts; `sum` adds. A cell that
  reads as a number is a number, else a text; no such row or an empty cell is missing. Tables are read once when the run
  starts.

Live reads never change the model: they schedule nothing and move nothing.

## Checked when typed, when built, and while running

- **As you type**: every expression field shows under it whether the text parses for its place (a red error with the
  position, a yellow warning for an element the scene does not have, or a green check). `simulean_set` /
  `simulean_apply` refuse a text that does not parse.
- **When the model is built** (a scene run, `simulean_validate`, the runner's `validate`, every headless build), every
  expression of the model is resolved: an unknown function or root, an element, team, list, table or column that does
  not exist, an element property that is neither a built-in, a declared property nor one a reaction sets, a statistic
  `stat` / `team` / `list` do not know — each fails the build, naming the element, the field, the expression and the
  closest names (`'router' (routing): 'content('m3') > 0 ? 1 : 0': 'content': no element 'm3' in the model; did you
  mean 'm1' or 'm2'?`).
- **Names computed from a label** (`content(item.dest)`, `team(item.crew, 'free')`, `table(item.plan, 1, 'x')`): when every
  write of that label the build sees gives values known then, each value must name an object of the right kind — or the
  build fails, naming the writer and the closest names (`'router' (routing): … 'content' reads the element named by the
  item label 'dest', which 'src' (createLabel:dest) sets to 'm3': no element 'm3' in the model; did you mean 'm1'?`). Known
  writes are a label record or a reaction's value that is a constant, a choice of constants (`?:`, `if`, `pick`) or a
  constant data table's column (`dest = table('routes', item.type, 'to')`), a source's arrivals-table column, a reaction's
  number. If one write is computed (`dest = item.type`), or the label is set only by an element's own code, the name is
  checked when it is read: an unknown one stops the run.
- **While running**: item labels are set during the run, so they are not checked when built. The first time an
  expression reads an item label the item does not have, it warns once — time, element, field, expression and the name
  (`t=12 'router' (routing): the item's label 'tpye' is not set …`). The warning shows in the MCP `warnings` and in
  `simulean_diagnose`. Use `has()` or `??` where a missing label is expected. A routing value that names no output warns
  once per value and the diagnosis says "routing gives output 5 of 2" instead of waiting for the outputs.
- **A run-time error stops the run**, naming the time, the element, the field and the expression — in every place (times,
  routing, conditions, labels, list fields, quantities, a fitness): a value that is NaN or infinite (`10 / 0`, `0 / 0`,
  `sqrt(-1)`, `pow(-1, 0.5)`, `ln(0)`, an `exp` that overflows, a label holding NaN; `t=3 'm1' (delayStrategy): '10 / item.k': 10 / 0 (a division by zero)
  gives an infinite value …`), a text used as a condition or in arithmetic, a computed name the model does not have, a
  random function's invalid parameter (`exponential(item.r)` with `r = 0`: `exponential(0): the rate must be > 0`; a
  `uniform` whose minimum is above its maximum, a negative `normal` / `lognormal` sd or `pick` weight, a `triangular` whose mode
is outside its bounds, a `clamp` whose lower bound is above the upper — a constant one fails the parse). A live statistic
  with no value yet (`stat('M1', 'stay_avg')` before any exit) is missing, not NaN. Guard a division that may be by zero:
  `item.k > 0 ? 10 / item.k : 0`.

## List queries and orders

Pull queries, a list's default order, team request / member orders, requirement queries and milk-run orders are
`field op value; … | order` texts over the entries' fields, with the expression's quoting and names:

- `kind == 'pickup'` (quotes as in an expression; unquoted works too), operators with or without spaces (`prio>=2`,
  `=` is `==`), `| prio desc, distance` with `desc` / `asc` in any case;
- fields: the entry's own (a list's `age`, `pushTime`, its defined fields), `item.x` (the entry's item — `item.type`,
  `item.priority`, a label) and `origin.x` (where it comes from — `origin.name`, `origin.content`); in a list of items a
  bare name may also be one of the item's labels;
- team requests: `priority`, `requestTime`, `count`, `oldestItemTime`, `location`, `taskType`, `skill`, `origin.x`;
- team members: `idleTime`, `utilization`, `tasks`, `capacity`, `distance`, `name`, `location` and their properties;
- milk-run requests: `distance`, `onBoard`, `originFill`, `kind`, `stop`, its request fields, `item.x`, `origin.x`,
  `destination.x`.

A field these entries cannot have fails the build with the closest names; in a list of items, an order or query that
reads a label an entry lacks warns once per list and field.

**Expressions in queries and orders** (lists of items): a condition that is not `field op value` is an expression over
the entry, true when not 0 — `prio * 2 > 3`, `has(item.code)` — and an order key that is not a field name is an
expression — `| item.prio * 10 + age desc`, `| max(prio, 1)`. Their commas, `;` and `|` inside parentheses or quotes do not
split them (`||` is the "or"). Team request / member orders and milk-run orders take field names only.

**A Queue's offer order** (`offerSort`) uses the same order grammar: `priority desc, due`, `item.prio * 10 + age desc`;
each key reads the held item (a bare name: its label, else `id`, `type`, `priority`, `created`, `age`) and the queue
(`element.x`), and may count or read tables; no random functions (keys are read again at every offer).

## Examples

```
processingTime                                   # a label of the item (bare name)
item.weight * element.secondsPerKg + 2           # item and element properties
item.color == 'red' ? 5 : 3                      # decision on a text
item.prio * 10 + item.age                        # priority: urgency grows while waiting
item.type == 'A' ? 0 : 1                         # routing: first output for type A
get('M1', 'state') == 'IDLE' ? 0 : 1             # routing by a machine's state
stat('M1', 'utilization') > 0.8 ? 1 : 0          # routing by a live statistic
team('Welders', 'free') > 0 ? 0 : 1              # routing by free resources
out.speed - out.content                          # routing score (BestScore): fast and empty first
count(out.name, 'family', item.family)           # routing score: the aisle holding most of the item's family
origin.name == 'Line A' && has(item.code)        # admission: from line A, with a code
(item.rework ?? 0) < 2                           # a label that may be missing, on purpose
prio * 10 + age                                  # list field over other fields
table('times', item.kind, 'time')                # process time per item kind, from a data table
exponential(0.2)                                 # random time, mean 5 (rate 0.2, as the Exponential sampler)
triangular(2, 3, 6) * item.qty                   # an expert's estimate per piece (min, most likely, max)
lognormal(ln(item.est), 0.3)                     # a duration around the item's estimate (median item.est)
clamp(item.qty / 10, 1, 5)                       # bounded
```

Element properties are set in the scene with *Element Properties* (numbers, texts, references to other elements) or by
reactions; item labels by sources (`createLabels`), stations (`entryLabels` / `exitLabels`), reactions or code. Every label a
model sets is a `name = expression` record; a text constant is quoted (`sku = '007'` is a text, `n = 007` the number 7);
`true` / `false` are not names.
