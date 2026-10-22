# Signals and reactions

A **reaction** does something when an element of the model **signals** — an item enters or leaves it, it finishes a
process, its state changes, a stop starts or ends — or when the **clock** does (the run starts, every period, at a time),
and a **condition** holds. It reads as a sentence:

> **When** *Buffer*: an item enters, **if** `element.content >= 10` **→** stop *Source* until resumed

Reactions are model logic without code: stop a line when a buffer fills up and start it again when it drains, switch a
routing rule after a time, mark the items that went through a station, post an order to a list when stock runs low.

**Example:** `Assets/Scenes/ReactionsDemo.unity` — Source → Buffer → Machine → Sink, the source faster than the
machine. *Buffer full* (an item enters the Buffer, if `element.content >= 8` → stop Source) and *Buffer drained* (an item
leaves it, if `element.content <= 2` → resume Source) keep the buffer at 8 at most; switch *Buffer full* off and it grows
past 80 in 2000 s.

## Creating one

- **From an element:** its inspector has a **Reactions** section — the reactions that listen to it or act on it, each a
  link that selects it — and **+ Reaction**, which creates one already listening to the element.
- **From the Library:** SimuLean window ▸ Library ▸ **Logic ▸ Reaction**, then choose its elements.

Reactions sit in the model hierarchy's **Logic** group. Their name is the GameObject's.

## The parts

| Part | What it is |
|---|---|
| **Enabled** | Off: the reaction does nothing (a study can switch it). |
| **When ▸ Elements** | The elements it listens to (any number; not for clock signals). |
| **When ▸ Signal** | Of the elements: *Item Entered*, *Item Exited*, *Process Ended* (a station finished an item, before sending it on), *State Changed* (optionally **to** one state: `PROCESSING`, `BLOCKED`, `STOPPED`…), *Stopped* / *Resumed* (optionally stops in one state). Of the clock: *Run Started*, *Every* (a period; the first time optional, else one period after the start), *At Time*. |
| **If ▸ Condition** | Optional. An [expression](Expressions.md) over the signal's item and element: `element.content >= 5`, `item.type == 'A'`, `now() > 3600`, `rand() < 0.1`, `get('Line 2', 'mode') == 1`, data tables. Empty = always. |
| **Do** | Its actions, in order (below). |
| **Advanced ▸ Timing** | *Next Event* (default) or *Immediate* (below). |

## Actions

| Action | Fields | Does |
|---|---|---|
| **Set Item Label** | Label, Value | `item.label = value` on the signal's item (item signals only: entered, exited, process ended). |
| **Set Element Property** | Element (empty = the one that signalled), Property, Value | Sets a property of an element. Routing, admission and time expressions read it — `element.mode`, `get('Router', 'lane')` — so a reaction changes the model's behaviour. |
| **Stop** | Of (Element / Team members), Element or Team + Members, State, Mode, Duration, Group | Stops the element, or *Members* of a resource team (0 = all; the last ones still working) — the downtime mechanism: its state (`OFF_SHIFT` for members sent off), *Immediate* pauses the work in progress, *After Current* lets it finish. For *Duration* time units, or — 0 — until a **Resume** of its group. |
| **Resume** | Of, Element or Team + Members, Group | Ends the stops **reactions** started on the element **in its group**, or calls back *Members* of the team that group sent off (in the order they were sent off) — whichever reaction stopped them; never a downtime's stops. |
| **Push To List** | List, Value | Pushes the value to a model list (an order, a request). |

A **Value** is a number (`3`) or an expression (`item.weight * 2`, `'red'`, `now()`).

Every action has an optional **Only if**: an expression checked when its turn comes, after the actions before it. One
reaction can then *decrement, then stop if zero*:

> **When** *Line*: a process ends (*Immediate*) **→** set `element.left = element.left - 1`; **only if**
> `element.left <= 0`: stop *Line* (*After Current*, group `orders`)

**Groups.** A stop belongs to a **Group** (empty = the default one) and a resume ends only the stops of its group. Give
independent controls of one element their own groups — orders opening and closing a station (`orders`), an incident
stopping it (`incident`) — so an order arriving during the incident does not restart the station: it runs again only
when no stop is left. Team members likewise: a stop takes members no group has stopped, a resume calls back only its
group's.

### Clock signals

A clock signal has no element of its own: its actions name the element or team they act on, and its condition reads
elements by name — `content('Buffer') > 10`, `get('Line', 'mode') == 1`. Examples:

> **Every** 86400 **from** 79200 **→** stop 2 of *Operators* (`OFF_SHIFT`) for 28800 — a night shift with two fewer
> operators.
>
> **When** *Queue*: an item enters, **if** `element.content >= 10` **→** resume 1 of *Operators* — call one back when the
> queue grows.

## Timing

By default a reaction runs **right after** the step that signalled, at the same time (a zero-delay
event). The model is then consistent — the item has finished entering, the station has started — and the order is the
same in every run with the same seed. **Immediate** runs inside that step, before anything else moves; use it only when
the reaction must see that instant's state — e.g. to stop a station at *Process Ended* before it takes the next item (a
*Next Event* stop arrives once it already has: *After Current* then waits for that item too).

**Order.** Reactions on the same signal of the same element run in the order they were built (the order of the
reaction components in the scene's hierarchy, of `Reactions` in a config): the *Immediate* ones during the step, then
the *Next Event* ones, in that same order. To sequence dependent actions, prefer one reaction with *Only if* to relying
on that order.

Random functions in a reaction (`rand()`, `pick()`…) draw from the reaction's own stream: adding a reaction does not
change the numbers the elements draw.

## Seeing it work

**In the editor:** the SimuLean connection lines (the Connect tool, Shift+C, and the drawing of the selection) show each
reaction in purple, dotted: a line from every element it listens to, and one to every element or team it acts on.
The Connect tool also links an element to a reaction (it then listens to it). A team's inspector lists the reactions that
send its members off or call them back.

**In Play,** the element inspector panel (click an element) lists, under **Reactions**, each reaction of the element with
how many times it fired, how many times its condition was not met, and when it last fired with which item. The
reaction's own inspector shows the same under *This run*, and the element's **Reactions** section shows the count.

A problem in a reaction stops the run with an error naming it (e.g. an expression reading a table that does not exist).
The inspector warns before: no elements, no actions, an expression that does not parse, a label on a signal without an
item, a missing name, value or list.

## In the results

Each reaction is in the run's results (the *Reactions* sheet of the exports, scope `Reaction`): **`fired`** — how many
times its actions ran — and **`not_met`** — how many signals its condition turned down —, counted after the warm-up like
every other statistic. A study or the optimiser reads them as metrics: `reaction:Buffer full.fired`, or
*Metrics ▸ Reaction* in the Experimenter and the Optimizer.

## In studies

The Experimenter and the Optimizer offer a reaction's **Enabled** and the **numbers** of its actions (a value that is a
plain number, a stop's duration): *Parameters ▸ Reaction*. In a study by code or through the MCP:

```
reaction:Buffer full.enabled=true|false
reaction:Switch lane.a1.number=0|1      (action 1's value)
reaction:Buffer full.a1.duration=5|10   (action 1's stop duration)
reaction:Night shift.period=43200|86400 (a clock signal's period; also first, at)
reaction:Call one more.a1.count=1|2     (members sent off / called back)
```

What the reaction *is* — its signal, its actions, their elements and expressions — is its design, not a parameter.

Through the Unity MCP: `simulean_add_element type=Reaction`, then `simulean_set` — `sources`, `signal`, `condition`,
and the actions by index: `actions[0].kind=Stop`, `actions[0].target=Source` (an index past the end adds an action).

## Headless and code

The scene is extracted as a `ReactionConfig` (`SimulationConfig.Reactions`) and built by the engine
(`ReactionBuilder`), so a headless run, a study and Play behave the same. In code:

```csharp
var r = new ReactionConfig("Buffer full", "buffer");          // listens to the element with id "buffer"
r.Parameters["condition"] = "element.content >= 10";
r.AddAction("Stop", target: "source");                        // until a Resume
config.Reactions.Add(r);
```

Engine: `SimuLean.Signals` (`SignalHub` on `SimContext.Signals`, `Reaction`); architecture §3.21.
