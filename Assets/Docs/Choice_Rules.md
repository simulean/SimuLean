# Choice rules — one decision in C#

A **choice rule** is a small C# class that makes one decision of a model. The engine calls it at a decision site; every
other part of the model stays declarative. It is the **last rung** of the escalation ladder — a field, an expression, a
query / order, a reaction, a rule; when to move up and an example per rung:
[`docs/agents/topics/logic.md` — *The escalation ladder*](../../docs/agents/topics/logic.md#the-escalation-ladder).

Every rule carries a `Why` that names the declarative form tried first and why it was not enough; `simulean_validate`
warns on a rule without one, and on a rule used where the primitive it declares equal (`Equivalent`) would do.

## The sites

| Site | Interface | Candidates | Set in the model |
|---|---|---|---|
| Routing | `IRoutingRule` | the element's next elements | `outputStrategy.strategyType = Rule`, `outputStrategy.rule = <Type>` (and `whenBestFull`) |
| Release | `IReleaseRule` | the items a Queue holds | the Queue's `offerRule = <Type>` (not with `offerSort`) |
| Dispatch | `IDispatchRule` | a team's pending requests | the team's request order `rule:<Type>` (in place, or as a named order's query) |

Each mirrors the declarative form of the same site: **the highest score wins**, ties keep the candidates' order
(connection order, arrival order, request order). Eligibility stays the engine's: a routing rule scores only the outputs
that can take the item (`whenBestFull = Wait`: those that admit it, and the item waits for the best), a team still serves
only requests it can serve now. To keep an item from an output, use that output's admission.

## Writing one

Rules live in `Assets/SimuLean.UserLogic` (assembly `SimuLean.UserLogic`: references the engine only, no Unity). Start
from a template: `simulean_rule_new kind=routing name=NearestFreeCell why="…"`.

```csharp
using SimuLean.Rules;

namespace SimuLean.UserLogic
{
    [ChoiceRule("Shortest queue", Why = "…", Reads = new[] { "out.content", "out.free" })]
    public sealed class ShortestQueueRule : IRoutingRule
    {
        [RuleParameter("Weight of free room", Min = 0)] public double FreeWeight = 0;

        public double Score(in ChoiceContext context, in ElementView output)
            => -output.Content + FreeWeight * output.Free;
    }
}
```

**What a rule reads** — `ChoiceContext`: `Now`, `Owner`, `Element` (the deciding element), `Item` (the item routed),
`Candidates`, `Site`; model reads by name `Content`, `Free`, `Get`, `Stat`, `Team`, `List`, `ElementNamed`; and `Random`,
the rule's own stream. The candidate is a read-only view: `ElementView` (`Name`, `Index`, `Content`, `Free`, `State`,
`Get`, `Text`, `Has`, `Stat`), `ItemView` (`Id`, `Type`, `Priority`, `Created`, `Age`, `Get`, `Text`, `Has`) or
`RequestView` (`Priority`, `RequestTime`, `Age`, `Count`, `TaskType`, `Location`, `Requester`, `Get`, `Text`).

**What a rule may not do** — write anything (move items, schedule, set labels or properties: use a reaction), keep
static state, draw from `System.Random` (use `context.Random`), use Unity, threads or I/O. Instance fields are its memory
within a run (one instance per element and site). The build refuses a rule with static mutable fields or forbidden field
types (`simulean_rule_check` lists them).

**Random numbers** — `context.Random` draws from the stream `<owner>/rule:<site>`, created at the first draw. The draws
are kept per decision: an item re-scored while it waits, a held item, a pending request gets the same numbers again. A
seeded run is reproducible.

**Errors** — an exception or a NaN score stops the run: `t=… Element 'Router': routing rule 'X': message`.

## Parameters

Public fields marked `[RuleParameter]` (numbers, whole numbers, yes / no, texts, options) take their values from the
owner's `rule` records. Numbers and yes / no are catalog parameters `<Element>.rule.<Field>` (a team's
`team:T.rule.<Field>`): the optimiser and the Experimenter vary them. A field not given keeps its initial value; `Min` /
`Max` are checked at build. A routing and a release rule of the same Queue share a name's value. In the scene: the
*Rule parameters* list of the routing, the Queue or the team (name, value) — a Queue has two: its own *Rule parameters*
for its release rule (`ruleParameters`) and the routing's (`outputStrategy.ruleParameters`); a list whose rule is not set,
or a name its rule lacks, is a warning (`simulean_set`, `simulean_apply`, `simulean_validate`, the scene extraction). The rule itself is a choice of the site's
rules (`outputStrategy.rule`, `offerRule`); `strategyType = Rule` sits beside the declarative modes.

## Checking and showing

- `simulean_rule_check name=X [object=Element]`: compiled, site, contract scan, parameters, source hash; with an element
  or team that uses it, the model builds and two seeded runs make the same decisions.
- `simulean_rule_show name=X`: site, `Why`, reads, parameters with their catalog keys, the source file and its hash, the
  elements that use it. Empty name lists every rule.
- `simulean_model section=logic` and *Tools ▸ SimuLean ▸ Model Logic* show a rule as `rule: X (source 1a2b3c4d)` with its
  parameters and declared reads; a rule writes nothing. In Play they add its counters: calls, errors and the last score
  with the item it scored and that item's first labels (`last score 0.5 for item 27 (P) {family=2, prio=1}`; on a
  routing line that already names the item, just the score). An admission counts `entered` (items) apart from its
  `checks` (evaluations: an item may be checked several times): `entered 20 · refused 180 of 226 checks · woken 6`.
- `simulean_card` lists the open model's rules under `scripts` (owner, setting, label, `Why`, reads, parameters with their
  catalog keys, source hash) with its decisions counted by rung (`decisions`: field, expression, query, reaction, rule;
  `declarative` / `scripted`) — how much of the model is scripted.

## Outside the editor

Headless runs, the standalone tests and the external runner (`Tools/Runner`) compile `Assets/SimuLean.UserLogic` with
the engine; the runner's fingerprint covers those sources, so a runner built before a rule changed is never used.

## Examples

`Assets/SimuLean.UserLogic/Examples`: `ShortestQueueRule` (= BestScore `-out.content`), `EarliestDueDateRule` (= `offerSort
= due`), `HighestPriorityRule` (= request order `|priority desc`, plus ageing). They show the contract. A model should use
the declarative forms they mirror: each declares it (`[ChoiceRule(Equivalent = …)]`, valid with its parameters at their
initial values), and `simulean_validate` warns when a model uses one so.

**Try it:** open `Assets/Scenes/ChoiceLogicDemo.unity`, then *Tools ▸ SimuLean ▸ Model Logic* — the *Pack Queue* routes by `ShortestQueueRule` (with `FreeWeight = 0.5`, so not at its `Equivalent`), next to the declarative forms of the other rungs; select its card `Assets/Scenes/ChoiceLogicDemo/ModelCard.asset` to see the rule under *Scripts / Logic*.
