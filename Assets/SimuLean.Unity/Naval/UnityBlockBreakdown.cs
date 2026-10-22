using System.Collections.Generic;
using System.Linq;
using SimuLean.Data;
using SimuLean.Naval;
using UnityEngine;

namespace SimuLean.Unity.Naval
{
    /// <summary>
    /// The ship's block breakdown in a scene: which table of
    /// the project database holds it (one row per sub-block), how the generated items are released, and which sources
    /// read its parts — the sub-block kits, the block orders (the block-assembly bays' main items, with <c>nsub</c> = the
    /// sub-blocks to gather) and the erection plan. <see cref="ApplyToSources"/> points those sources at the breakdown
    /// (<c>tableSource = naval:&lt;part&gt;:&lt;table&gt;</c>), so every run, headless run and study reads the same current
    /// table and the three parts always agree. Excel / CSV files are imported into the table
    /// (inspector). Data only: nothing here runs during a simulation.
    /// </summary>
    [AddComponentMenu("SimuLean/Data/Block Breakdown")]
    [SimuLeanTool(SimuLeanToolCategories.Data, "Block Breakdown", Pack = SimuLeanPacks.Shipbuilding, Icon = "Assets/SimuLean.Unity/Art/Models/ShipBlock/ShipBlockCurved.fbx",
        Description = "The ship's block breakdown (one row per sub-block) in the project database; generates the sub-block kits, block orders and erection plan of the sources.")]
    public class UnityBlockBreakdown : MonoBehaviour
    {
        [Tooltip("Table of the project database (or a SELECT query returning the breakdown's columns).")]
        public string table = BlockBreakdown.DefaultTable;
        [Tooltip("Empty = the project database (Project Settings: the Data tab).")]
        public string databasePath = "";

        [Header("Release (model time units)")]
        [Tooltip("Time between consecutive blocks, in erection order (0 = every block at the start).")]
        [Min(0)] public double blockInterval;
        [Tooltip("Time of the first block.")]
        [Min(0)] public double start;
        [Tooltip("How long before its block a sub-block kit is released.")]
        [Min(0)] public double subBlockLead;

        [Header("Sources it feeds (Schedule mode)")]
        [Tooltip("One item per sub-block (every breakdown column as a label).")]
        public UnitySource subBlockSource;
        [Tooltip("One block order per block (block, nsub, weight, erect_seq, hull_x…): the block-assembly bays' main items.")]
        public UnitySource blockOrderSource;
        [Tooltip("One erection token per block, in erection order.")]
        public UnitySource erectionSource;

        /// <summary>The database file the breakdown is read from.</summary>
        public string DatabasePath() => string.IsNullOrWhiteSpace(databasePath)
            ? ResultsOutput.DatabasePath(ResultsOutput.ProjectRoot)
            : databasePath;

        /// <summary>Reads the breakdown (throws when the table cannot be read).</summary>
        public BlockBreakdown Load() => BlockBreakdown.Load(table, DatabasePath());

        /// <summary>The table spec a source reads for <paramref name="part"/>.</summary>
        public string SpecFor(BlockBreakdown.Part part) => TableSources.NavalSpec(part, table, blockInterval, start, subBlockLead);

        /// <summary>Points the linked sources at the breakdown's parts (Schedule mode, table source, same database);
        /// returns how many were set.</summary>
        public int ApplyToSources()
        {
            int n = 0;
            foreach (var (src, part) in Links())
            {
                if (src == null) continue;
                src.mode = SourceMode.Schedule;
                src.tableSource = SpecFor(part);
                src.tableDatabase = string.IsNullOrWhiteSpace(databasePath) ? "" : databasePath;
                n++;
            }
            return n;
        }

        /// <summary>The linked sources and the part each reads.</summary>
        public IEnumerable<(UnitySource source, BlockBreakdown.Part part)> Links()
        {
            yield return (subBlockSource, BlockBreakdown.Part.SubBlocks);
            yield return (blockOrderSource, BlockBreakdown.Part.Blocks);
            yield return (erectionSource, BlockBreakdown.Part.Erection);
        }

        /// <summary>One-line summary of a breakdown (blocks, sub-blocks, steel weight, erection chain).</summary>
        public static string Summary(BlockBreakdown bd)
        {
            int chain = bd.BlockCount == 0 ? 0 : bd.ErectionLevels().Values.DefaultIfEmpty(0).Max() + 1;
            return $"{bd.BlockCount} blocks · {bd.SubBlocks.Count} sub-blocks · {bd.SteelWeight:0.#} t steel · " +
                   $"{bd.SubBlocks.Count(s => s.Turn)} turned · erection chain {chain} block(s)";
        }
    }
}
