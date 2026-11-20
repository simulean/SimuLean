# Topic: decisions — routing, admission, release order, reactions, rules

Read after the core ([workflow.md](../workflow.md)) when the model decides something: where an item goes, which item
goes next, what an element admits, which request a team serves, what happens when something changes. Read the model's
decisions with `simulean_model section=logic` (one sentence each, its raw `settings`, what it `reads` / `writes`; in Play
its `live` counters) before changing them.

## Routing (where an item goes)

`outputStrategy.strategyType` = `FirstAvailable` (default), `ShortestQueue`, `RoundRobin`, `MostAvailableCapacity`,
`PriorityBased`, `Parameterized` (`outputStrategy.parameterKey`: the label naming the output index), `Expression`
(`outputStrategy.routingExpression`: the output number), `Probability` (`outputStrategy.weights`), `BestScore`, `Rule`.

- **The best destination by a rule:** `BestScore` with `outputStrategy.scoreExpression` over `out.x` (the candidate:
  `out.content`, `out.name`, `get(out.name, 'orders')`) and `item.x`; highest wins, ties to the first;
  `outputStrategy.whenBestFull = Wait` waits for the best (else `Skip`). Never nested ternaries over the outputs.
- **`Skip` vs `Wait` and admission:** `Skip` scores only the outputs that can take the item now (room, admission, not
  stopped); `Wait` scores every output whose admission accepts the item and waits for the best. An output whose admission
  refuses the item is never chosen in either mode — keep an item from an output with that output's admission.
- **`Probability` draws once per item and keeps the draw:** when the drawn output is full the item waits for it (the
  sender shows `BLOCKED`); `whenBestFull` does not apply to it (only to `BestScore` and `Rule`). The shares hold only
  if every output has room for its share: give them capacity, or route by `BestScore` / `FirstAvailable` when an item
  must go elsewhere rather than wait.
- The order of the outputs is the Next Elements' order (`simulean_reorder`).
- Live counters: items per output and the last choice with its scores and the item's labels (the first four), e.g.
  `last → Cell B (10) for item 120 (P) {family=2, prio=3}` (data: `last`, `lastScores`, `lastItem` {id, name, labels}).
  The size labels of the item's 3D model (`width`, `height`, `length`, `loadFloor`) are not among them: the data gives
  them apart, as `size` (here and in `simulean_diagnose`).

## Expressions: conditions, choices, weights

- **Conditional:** `cond ? a : b` (nests: `a ? b : c ? d : e`) or `if(cond, a, b)`; only the chosen branch is evaluated.
- **`pick(w1, v1, w2, v2, …)`**: a random value with probability proportional to its weight — **weights are normalised**
  (`pick(7, 'std', 3, 'rush')` = `pick(0.7, 'std', 0.3, 'rush')`); a negative weight stops the run, all zero gives no value.

## Admission (what an element takes)

`inputStrategy.strategyType` = `Default`, `SingleLabel` (`inputStrategy.singleLabelName`, `singleLabelValue`),
`MultiLabel`, `Condition` (`inputStrategy.condition`, an expression: `element.open == 1`, `content('Buffer') < 3`).
Live counters: `entered` (items that entered — one each), `refused` of `checks` (evaluations of the condition: each offer
and each check by a sender counts, so an item may be checked several times) and `wakeUps`, e.g.
`entered 20 · refused 180 of 226 checks · woken 6`.
**An admission never sees the labels of its own hand-over** (the sender's `exitLabels`, its own `entryLabels`): they are
applied after routing and admission have decided. To admit or route by a label, set it one step earlier (the sender's
`entryLabels`, the source's `createLabels`).

## Release order (which waiting item goes next)

A Queue: `offerOrder` = `FIFO` (default) or `AnyAccepted` (any item a receiver takes), `offerSort` (an order:
`priority desc`, `item.prio * 10 + age desc`, `due`; **several keys** comma-separated, `prio desc, age desc`: the first
decides, the next breaks its ties, then arrival order; a key that is not a number ranks as 0), `offerScan` (how many it looks at), `offerRule` (a C# rule),
`gated` (released by reactions), `groupBySetup` / `groupLimit` (by the receiver's setup). The live counters: `left`
(every item that left), `released` (those that waited and the order chose) and `passedThrough` (left at once on arrival,
no decision); `lastReleased` with the item's labels (`lastReleasedItem` in the data). A choice rule adds
`ruleCalls`, `ruleErrors` and `last score` with the item it scored (`lastScoreItem`) when the line does not already name it.

**A head item no output can take blocks the queue under `FIFO`:** only the head (the best by `offerSort`, if set) is
offered, and when the routing finds no output for it (`whenBestFull = Skip` with every output full or refusing it, any
routing whose outputs all refuse it) it keeps its place and the items behind wait, even one that another output would take
(the Queue shows `BLOCKED`). To let the next items pass, use `offerOrder = AnyAccepted` (each held item is offered,
oldest / best first, bounded by `offerScan`).

List pulls take expressions in their query and order (`prio * 2 > 3 | max(prio, 1) desc`); a list's own order is
`defaultOrderBy`. A team's request order: [resources.md](resources.md).

## Reactions (something changes when a signal comes)

`simulean_add_element type=Reaction name=Open`, then:

| Key | Values |
|---|---|
| `sources` | the elements it listens to (names) |
| `signal` | `ItemEntered`, `ItemExited`, `StateChanged` (`state` filter), `Stopped`, `Resumed`, `ProcessEnded`, `RunStarted`, `Every` (`period`, `firstTime`: -1, the default, = one period after the start; 0 = at the start too), `AtTime` (`atTime`), `WaitTimedOut` |
| `condition` | an expression; the actions run only when it holds |
| `timing` | `NextEvent` (default) or `Immediate` |
| `reactionEnabled` | on / off (a study factor: `reaction:Open.reactionEnabled=true\|false`) |
| `actions[i].kind` | `SetItemLabel`, `SetElementProperty`, `Stop`, `Resume`, `PushToList`, `Release`, `Retry`, `SetItemLook` |
| `actions[i].…` | `target` (an element; `onTeam` + `team` + `count` for members), `name` (the label or property written — the inspector's *Property*), `value` (an expression), `list`, `state`, `mode`, `duration`, `group`, `onlyIf` (a condition per action), `color` (`#RRGGBB`, `#RRGGBBAA` or a name: `red`) / `scale` (a factor; `SetItemLook`); `SetItemLabel` writes on the signal's item (the one that entered, left, ended its process or timed out) |

- **A reaction opens a gate:** an admission `Condition` over a property, and *SetElementProperty* of that property — the
  write offers the refused items again at once (`wakeUps` in the logic report). Any other condition is checked again
  only when the receiver frees a place (an item leaves it) or ends a stop: enough when what it reads changes with the
  receiver's own flow (its content, its downstream's content in a loop). When it changes elsewhere — `now()`, a table,
  another branch's `content(…)` — add a `Retry` action of the receiver on the signal that changes it (`ItemExited` of
  that element: `actions[0].kind = Retry`, `actions[0].target = <receiver>`). Never Stop + Resume pokes, never `maxWait`
  as a gate.
- **The gate's start state is the property's initial value:** set it explicitly (`simulean_set object=Pack
  key=properties.open value=1` for a gate open at the start; 0 for closed). A toggle every period: a reaction with
  `signal = Every`, `actions[0].kind = SetElementProperty`, `actions[0].target = Pack`, `actions[0].name = open`,
  `actions[0].value = 1 - get('Pack','open')`.
- **`Every` and the horizon:** an `Every` reaction fires at `firstTime`, then each `period`, **up to and including the
  horizon** — every event at the horizon time runs, as arrivals and process ends do. Period 1800 with horizon 28800:
  `fired` = 16 (1800 … 28800); with `firstTime = 0`, 17. The last fire acts at the very end, so a toggle at the horizon
  changes no statistic; count it or not in a KPI as the question asks.
- **Repeat a test:** a counter label and routing back to the input Queue with room for every return, or a zero-time
  Delay that sends it back. Never connect a full station to itself (a deadlock).
- **`SetItemLook` changes only what it sets:** `scale` alone keeps the item's colour, `color` alone keeps its scale.
- **Looks only** (`SetItemLook` with `onlyIf`): a reaction whose every action is `SetItemLook` is **view only** —
  inferred, there is no field to set; `simulean_model section=logic` marks it `viewOnly` and results are unchanged.
- Statistics `reaction:Open.fired`.

## C# choice rules

A rule (C# in `Assets/SimuLean.UserLogic`) scores the candidates of one site: routing (`IRoutingRule`;
`outputStrategy.strategyType = Rule`, `outputStrategy.rule`), release (`IReleaseRule`; a Queue's `offerRule`), dispatch
(`IDispatchRule`; a team's `requestOrderName = rule:X`). `simulean_rule_new kind=routing name=… why=…` writes it from a
template (refused without a `why` naming the declarative form tried), then `simulean_rule_check name=… object=…` after
Unity recompiles; `simulean_rule_show` lists who uses it, and with no name the project's rules. Shipped examples:
`ShortestQueueRule` (routing, `FreeWeight`), `EarliestDueDateRule` (release, `DueLabel`), `HighestPriorityRule`
(dispatch, `AgeWeight`) — each equals a declarative form at its defaults. Setting the rule fills its parameter list
(change them as `FreeWeight = 0.5, DueLabel = due`; factors `<Element>.rule.<Field>`). **A rule is named by its class
name** (or its full name when two share one; case-sensitive), e.g. `outputStrategy.rule = ShortestQueueRule`,
`requestOrderName = rule:HighestPriorityRule`; the `[ChoiceRule]` label is only shown. A rule reads only; state changes
stay in reactions.

**Each site has its own parameter list — a Queue has two:**

| Site | The rule | Its parameters |
|---|---|---|
| routing (any element) | `outputStrategy.rule` (with `outputStrategy.strategyType = Rule`) | `outputStrategy.ruleParameters` |
| release (a Queue) | `offerRule` | `ruleParameters` |
| dispatch (a team) | `requestOrderName = rule:X` | `ruleParameters` (of the team) |

A list set while its rule is empty, or a name its rule does not have, is a warning in `simulean_set`, `simulean_apply` and
`simulean_validate` (naming the other list when the name is its rule's).

## The escalation ladder

Express each decision at the **lowest rung that can express it**; move up one rung only when the rung below cannot.
The card's `scripts` (`simulean_card`) counts the model's decisions by rung.

| Rung | Use it for | Move up when | Example |
|---|---|---|---|
| 1. **Field / option** | the element has an option for the rule | no option says it | `outputStrategy.strategyType = ShortestQueue`, `offerOrder = AnyAccepted` |
| 2. **Expression** | a formula over the item, the element, the candidate or the model | the choice is among list entries or a team's requests, or a study compares named orders | `outputStrategy.scoreExpression = -out.content`; `offerSort = item.prio * 10 + age desc`; `inputStrategy.condition = element.open == 1` |
| 3. **Query / order** | what a puller takes, a list's order, a team's request order | something must change when an event happens | pull query `family == 'A' \| due`; team `namedOrders` + `requestOrderName` |
| 4. **Reaction** | a write on a signal: a property, a label, a stop, a release, a retry, a look | a choice needs memory across decisions or a look-ahead | on `ItemExited` of `Buffer`: `SetElementProperty open = 1` on `M2` |
| 5. **Choice rule** (C#) | the last rung: a class scoring each candidate of one site, read-only | — | `simulean_rule_new kind=routing name=NearestFreeCell why="BestScore cannot … because …"` |

A rule needs a `Why` (`[ChoiceRule(Why = …)]`; validate warns without one) and the check that the declarative form was
tried: write it once (rungs 1–4), run both with the same seed and compare. A rule that equals a primitive at its initial
parameters declares it (`[ChoiceRule(Equivalent = "…")]`) and validate warns when a model uses it so. When no rung
expresses a rule exactly, write **"not expressed (approximated by X)"** in the card's `assumptions`.
