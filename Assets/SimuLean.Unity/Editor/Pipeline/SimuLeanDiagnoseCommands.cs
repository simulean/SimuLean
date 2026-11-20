using Unity.Pipeline.Commands;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Diagnosis through the MCP, so no script of one's own is needed to
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
                                         "check — a real blocking of the design or the model's logic. factors = one level per factor " +
                                         "(a what-if); noProgress ends the run when nothing has moved for that time (end reason NoProgress). The scene is not changed.", Tags = new[] { Tag })]
        public static object Diagnose(
            [CliArg("until", "Model time to stop at (-1 = until nothing can happen, at most the horizon).")] double until = -1,
            [CliArg("seed", "Model seed of the run.")] int seed = 1,
            [CliArg("factors", "Catalog overrides 'path=value;…' or JSON, one level each (as simulean_run_headless).")] string factors = "",
            [CliArg("all", "List every element, not only those holding items, stopped or busy.")] bool all = false,
            [CliArg("root", "Model root GameObject (empty = the scene's model).")] string root = "",
            [CliArg("noProgress", "End condition: stop when no item has moved for this time while items wait (end reason NoProgress) - finds a stall beside a periodic signal or reaction, which keep the run from becoming idle (0 = off, -1 = the model's).")] double noProgress = -1)
            => AgentReply.Of(() =>
            {
                if (!(until > 0) && until != -1) throw new System.ArgumentException($"until: {until} — a time > 0 (-1 = until nothing can happen).");
                var study = MakeStudy(new SimuLean.Optimization.StudySpec { Seed = seed, Factors = factors ?? "" }, root, "", out _);
                if (noProgress >= 0) study.Model.StopWhenNoProgressFor = noProgress;
                return study.Diagnose(until > 0 ? until : 0, seed, all);
            });
    }
}
