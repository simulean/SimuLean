using Unity.Pipeline.Commands;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Diagnosis through the MCP: the agents wrote their own scripts to
    /// find why a model stalls or deadlocks (build headless, advance, dump the elements and the resources).
    /// </summary>
    public static partial class SimuLeanCommands
    {
        [CliCommand("simulean_diagnose", "Run the scene's model headless (one replication) to a time — or, with time 0, until nothing " +
                                         "can happen any more (stall, deadlock or empty; at most the horizon) — and report its state: " +
                                         "stalled / deadlocked and when, each element holding items, stopped or busy (state, items, " +
                                         "what it waits for), each team's pending requests (who, how many, for how long) and members " +
                                         "(state, task, for whom, since when), the lists, and the progress (last item movement, oldest " +
                                         "request, longest task) with notes when the model has not moved for a quarter of the run " +
                                         "or more although events keep it from being called stalled (shifts, reactions): facts to " +
                                         "check — a real blocking of the design or the model's logic. set = one level per factor " +
                                         "(a what-if). The scene is not changed.", Tags = new[] { Tag })]
        public static object Diagnose(
            [CliArg("time", "Model time to stop at (0 = until nothing can happen, at most the horizon).")] double time = 0,
            [CliArg("seed", "Model seed of the run.")] int seed = 1,
            [CliArg("set", "Catalog overrides 'path=value;…' or JSON, one level each (as simulean_run_headless).")] string set = "",
            [CliArg("all", "List every element, not only those holding items, stopped or busy.")] bool all = false,
            [CliArg("root", "Model root GameObject (empty = the scene's model).")] string root = "")
            => AgentReply.Of(() => MakeStudy(0, 1, seed, set, "", root).Diagnose(time, seed, all));
    }
}
