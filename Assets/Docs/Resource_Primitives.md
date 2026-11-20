# Resource rules: several teams, room downstream, operators before the part

Three options of a station's resource use. Each is off by default: a model that does not set them runs as
before. The architecture is in `docs/architecture.md` §3.17.

## 1. Resources from several teams, all at once or in order

A job may need members of more than one team — a crane and a driver, a welder and a fitter of another crew.

- **A requirement's team.** In the element's *Process* block (or a setup crew, a downtime's *Repair by*), each row of
  *Requirements* has a **Team** field. Empty = the block's team. The block's team may stay empty when every requirement
  names its own (a crane of the cranes' team and a driver of the drivers' team); a requirement without a team then has no
  team to take from, and the build stops with a message saying to set the block's team or the requirement's. Key: `processBy.requirements[j].team`
  (the team's id; `count`, `query`, `label` beside it).
- **Acquire** (`processBy.acquire`, `setup.setupBy.acquire`, `repairBy.acquire`):
  - **All At Once** (default): the job gets its members only when every requirement can be met at the same moment,
    across teams. Nothing is held while one is missing.
  - **In Order**: the requirements are taken one by one, in list order. The members already granted walk to the station
    and wait there, **HELD**, while the next one is waited for. Use it when the first resource must be committed first:
    a crane booked, then a driver from the pool.
- **Priority and preemption** are the block's: each team is asked with the job's priority and mode. The priority is a
  property of the request: the team's *Request Order* decides whether its queue uses it (a priority rule), otherwise the
  requests are served in their order; preemption uses it always. A member that waits HELD for the others can be taken by
  work of higher priority; the job then asks for that requirement again.
- **Statistics:** each team's request waits are its own (`team:<id>.request_wait_avg`); the time HELD counts in the
  member's utilisation and `state:HELD`, and the team's `state:HELD` is its members' mean (`team:<id>.state:HELD`,
  also as a time series for charts).
- **Assembly Station:** a task may have its own *Requirements* (field `tasks[i].requirements`, key
  `tasks[i].processBy.requirements`) and its own *Resource Settings* (key `tasks[i].processBy.*`: priority, preemption,
  preemptable, acquisition order, max wait and action). Such a task asks them instead of the station's; the station's
  Process block gives what the task does not set (default team, priority, preemption, release, acquire).
- **Not for transport:** a *Transport* block takes all its carriers from its team (a requirement of another team fails
  the build).

**The risk of In Order — hold-and-wait.** Two stations that take the same two teams in opposite orders can each hold
what the other waits for. The run reports it: the deadlock check at the idle point, the end condition *Stop When No
Progress For*, and `simulean_diagnose` (the stations WAITING_FOR_RESOURCE, the pending requests, the members HELD).
Order the requirements the same way everywhere, or use All At Once.

## 2. Reserve room downstream before a job

**Reserve Exit** (`reserveExit`, on the Multi-Server, Combiner and Picking Station): a job starts — before its setup,
its resources and its process — only when a place at the next element can be promised. Until then the job waits,
its item inside, the slot **BLOCKED**, no resource asked. The place stays the item's until it enters, so the item never
waits blocked after its process. Example: a bed keeps its node until a bed downstream is free.

- **Which destination:** a routing by rule (Expression, Probability) is evaluated when the job starts and kept — the job
  waits for room *there*. Any other routing chooses among the next elements with room.
- **Every link honours it:** a reserved place is not taken by another origin, even through a plain connection.
- **Carried or walking items:** the transport (or walk) takes over the same reservation; it is counted once.
- **Several slots** reserve one place each.
- **Not** behind a list or a milk run (no destination is known when the job starts).

## 3. Secure the process resources before taking the part

**Before The Part** (`processBy.acquireBeforeEntry`, in the Process block of the **Multi-Server**): the station secures its
resources first and takes an item only once they are at the station (walked in). The part waits upstream meanwhile, so
the buffer in front shows the real wait and the station is never WAITING_FOR_RESOURCE with a part inside. Two steps:

1. **The request:** when an item cannot be sent anywhere, the station it could go to checks that it would take it — its
   admission, a free slot, with *Reserve Exit* a place downstream — and only then asks for its resources. Nothing is asked
   for an item the station refuses, nor for one that went elsewhere.
2. **The entry:** the grant is for **the next item that enters**. Its entry ends the wait (and its *Max Wait*). The members
   wait HELD until the work starts — and HELD while a setup crew of its own sets up. They are not preempted while they wait.

- **Setups:** a setup crew of its own comes after the part, and can be preempted as usual. With *Keep For Process*, the
  secured members set up.
- **Only the Multi-Server.** The build refuses it on other stations, with *On Timeout = Reroute* (the wait comes before the
  part: no item to send) and on a station that pulls from a list.

## 4. A limit on any wait: Max Wait

A wait may carry a **Max Wait**: a time, or an expression over the item and the element evaluated when the wait starts
(`uniform(5, 10)`, `item.priority > 1 ? 2 : 8`); empty or 0 = no limit (the default). **On Timeout** says what happens then:

- **Keep Waiting** (default) — only record it;
- **Give Up** — cancel the wait and release everything held for it;
- **Reroute** — items only: the item leaves by the **Timeout Target**.

Whatever the action, a timeout is **counted** (`timeouts`, and per kind `timeouts:resource`, `timeouts:queue`,
`timeouts:list_pull`, only on elements with a limit) and raises the signal **Wait Timed Out**, so a reaction can do anything
else (`signal.kind`, `signal.waited`, `signal.action` in its expressions; the item if any).

| Where | Fields / keys | Give Up | Reroute |
|---|---|---|---|
| A job waiting for its resources — the Process block, a Setup crew, a downtime's Repair crew, an Assembly Station's tasks | *Max Wait*, *On Timeout* (`processBy.maxWait` / `.onTimeout`, `setup.setupBy.…`, `repairBy.…`) | the members already granted (held **in order**, secured **before the part**) go free; the job **asks again** behind the others — before the part: dropped, asked again at the next offer | Multi-Server process only: the item leaves **unprocessed** by *Timeout Target* (`processBy.timeoutTarget`) |
| An item waiting in a **Queue** (reneging) | *Max wait*, *On timeout*, *Timeout target* (`maxWait`, `onTimeout`, `timeoutTarget`) | as Reroute (an item holds nothing; it is never destroyed) | to the target (a Sink = a lost customer) |
| An element waiting for a **list** entry | the list block's *Max Wait* (`pullMaxWait`) | — | — |

- **The wait** of a job runs from its request until every requirement is granted (walking not included) — with *Before
  The Part*, until a part enters; an item's, from its entry into the queue until it leaves; a puller's, from its pull to
  a match.
- **Why:** a station secures its operator before the part, but the part goes elsewhere — the operator
  would wait HELD for ever. *Max Wait* 5, *Give Up*: after 5 the operator is free for the other stations.
- **In order:** a crane held while the driver is waited for is released on *Give Up*: the station that needs the crane
  gets it, and the job asks again (a deadlock of two uses in opposite orders breaks).
- **A full timeout target:** the item keeps its place in the line and leaves by whichever comes first — its normal turn,
  or room at the target. A stopped queue or target holds it until the stop ends.
- **Not covered:** transport (an item waiting for its carrier: use the queue's *Keep Waiting*), the milk run (its own
  ready / load timeouts), the accumulation lane (no exit from the middle), the Agenda (the wait is the booking), list
  pushes (a list never refuses). A Queue's Reroute needs plain *Next Elements*, not a list or a transport team.

## 5. Named request orders: compare dispatch rules in a study

A team's **Named Orders** (`namedOrders`: a name and a query, e.g. `Urgent = |priority desc`, `Oldest = |oldestItemTime`,
`Nearest = |origin.distance`) and its **Request Order Name** (`requestOrder = Urgent`) make the dispatch rule a choice:
the Experimenter and the optimiser offer `team:Welders.requestOrder` with the names as its levels
(`factors = team:Welders.requestOrder=Urgent|Oldest`). A request order written in place (`|priority desc`) still works but
is not offered. A name that is none of the named orders fails the build, listing them.

## Which one?

| The case says… | Use |
|---|---|
| "the crane, then a driver from the pool" | In Order with a requirement of the drivers' team |
| "a crane and a driver together, or nobody" | All At Once (default) with a requirement of the drivers' team |
| "a bed keeps its node until a downstream bed is free" / "do not start what cannot leave" | Reserve Exit |
| "the part is not loaded until the operator is there" | Before The Part |
| "if the driver is not there in 30 min, release the crane" / "the operator stops waiting for a part after 5 min" | Max Wait + Give Up |
| "customers leave after waiting 10 min" | the Queue's Max wait + Reroute to a Sink |
| "at most N parts in the rework zone" / "N moves at once" | a token pool: a team of N abstract members (`travel=Abstract`; the Library's *Token Pool* preset) required by the zone's stations, In Order before their own crew — not a counter in reactions |

Reference models `resource_primitives` (the first three) and `max_wait` (queue reneging, an operator released by Give Up);
tests `StandaloneTests/primitivetests`, `StandaloneTests/maxwaittests`. Example scene with every option at work:
`Assets/Scenes/LibraryExamples/Workshop_Advanced.unity` (its model card gives the numbers: the crane HELD while its
driver comes, the timeouts per kind, the inspection BLOCKED before its process).
