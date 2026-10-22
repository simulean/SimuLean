using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SimuLean.Naval
{
    /// <summary>
    /// The product structure of a ship's hull for a block-assembly / erection model
    ///: one row per <b>sub-block</b> — its block, kind, turning,
    /// dimensions, work content per stage, the block's erection order or predecessors and its place on the hull.
    /// Read from SQL (the project database, <see cref="Load"/>), from a table file or from any column table; checked by
    /// <see cref="Validate"/>; <see cref="Generate"/> derives the tables the model's sources read — the sub-block kits,
    /// the block orders and the erection plan — so the three always agree. Every column becomes an item label.
    ///
    /// <para><b>Columns</b> (names are case-insensitive): <c>block</c>, <c>sub</c> (required); <c>kind</c>,
    /// <c>turn</c> (0 / 1), <c>length</c>, <c>breadth</c>, <c>depth</c> (m), <c>weight</c> (t), <c>erect_seq</c>,
    /// <c>erect_after</c> (blocks erected first, separated by spaces, commas or semicolons), <c>hull_x</c> /
    /// <c>hull_y</c> / <c>hull_z</c> (m), any <c>t_&lt;stage&gt;</c> (work content, model time units); other columns are
    /// carried as labels. Block-level columns (<c>erect_seq</c>, <c>erect_after</c>, <c>hull_*</c>) may be written on one
    /// row of the block or repeated (equal values).</para>
    /// </summary>
    public sealed class BlockBreakdown
    {
        /// <summary>The table the library reads by default in the project database.</summary>
        public const string DefaultTable = "block_breakdown";

        /// <summary>Generated table parts (<see cref="Generate"/>).</summary>
        public enum Part { SubBlocks, Blocks, Erection }

        /// <summary>One sub-block.</summary>
        public sealed class SubBlock
        {
            public int Block, Sub;
            public string Kind = "";
            public bool Turn;
            public double Length, Breadth, Depth, Weight;
            /// <summary>Every column of the row, by its lower-case name (labels of the generated items).</summary>
            public readonly Dictionary<string, string> Columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>One block (its sub-blocks and block-level data).</summary>
        public sealed class Block
        {
            public int Number;
            public readonly List<SubBlock> SubBlocks = new List<SubBlock>();
            public int ErectSeq;
            public readonly List<int> ErectAfter = new List<int>();
            public double HullX, HullY, HullZ;
            public double Weight => SubBlocks.Sum(s => s.Weight);
        }

        /// <summary>A problem found by <see cref="Validate"/>.</summary>
        public readonly struct Issue
        {
            public readonly bool IsError;
            public readonly string Message;
            public Issue(bool isError, string message) { IsError = isError; Message = message; }
            public override string ToString() => (IsError ? "error: " : "warning: ") + Message;
        }

        /// <summary>Release of the generated items (model time units).</summary>
        public sealed class ReleaseOptions
        {
            /// <summary>Time between consecutive blocks in erection order (0 = every block at <see cref="Start"/>).</summary>
            public double BlockInterval;
            /// <summary>Time of the first block.</summary>
            public double Start;
            /// <summary>How long before its block a sub-block kit is released.</summary>
            public double SubBlockLead;
            /// <summary>Item names (the items' type) of the three parts.</summary>
            public string SubBlockName = "SubBlockKit", BlockName = "BlockOrder", ErectionName = "Erection";
        }

        private readonly List<SubBlock> subBlocks = new List<SubBlock>();
        private readonly SortedDictionary<int, Block> blocks = new SortedDictionary<int, Block>();
        private readonly List<Issue> loadIssues = new List<Issue>();

        public IReadOnlyList<SubBlock> SubBlocks => subBlocks;
        public IEnumerable<Block> Blocks => blocks.Values;
        public int BlockCount => blocks.Count;
        public double SteelWeight => subBlocks.Sum(s => s.Weight);
        public Block GetBlock(int number) => blocks.TryGetValue(number, out var b) ? b : null;

        // ------------------------------------------------------------------ reading

        /// <summary>
        /// Reads a breakdown: <paramref name="source"/> is a table name or a <c>SELECT</c> query of the project database at
        /// <paramref name="databasePath"/>, or a <c>.csv</c> / <c>.data</c> file path.
        /// </summary>
        public static BlockBreakdown Load(string source, string databasePath)
        {
            if (string.IsNullOrWhiteSpace(source)) source = DefaultTable;
            string s = source.Trim();
            string ext = System.IO.Path.GetExtension(s).ToLowerInvariant();
            if (ext == ".csv" || ext == ".data") return FromTable(SimuLean.Data.TableReader.Read(s));
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException($"Block breakdown '{s}': no project database given.");
            var db = SimuLean.Data.ProjectDatabaseRegistry.GetOrOpen(databasePath);
            bool query = s.StartsWith("select", StringComparison.OrdinalIgnoreCase) || s.StartsWith("with", StringComparison.OrdinalIgnoreCase);
            if (!query && !db.TableExists(s))
                throw new ArgumentException($"Block breakdown: table '{s}' not found in {databasePath}.");
            var rows = db.Query(query ? s : $"SELECT * FROM \"{s.Replace("\"", "\"\"")}\"");
            return FromRows(rows.Select(r => r.ToDictionary(kv => kv.Key, kv => ToText(kv.Value))).ToList());
        }

        /// <summary>A breakdown from a column table (column name → values, one per row; e.g. <see cref="SimuLean.Data.TableReader"/>).</summary>
        public static BlockBreakdown FromTable(Dictionary<string, List<string>> table)
        {
            var rows = new List<Dictionary<string, string>>();
            int n = table.Count == 0 ? 0 : table.Values.Max(c => c.Count);
            for (int i = 0; i < n; i++)
            {
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in table) row[kv.Key] = i < kv.Value.Count ? kv.Value[i] : "";
                rows.Add(row);
            }
            return FromRows(rows);
        }

        /// <summary>A breakdown from rows (column → text).</summary>
        public static BlockBreakdown FromRows(IEnumerable<Dictionary<string, string>> rows)
        {
            var bd = new BlockBreakdown();
            int line = 0;
            foreach (var raw in rows)
            {
                line++;
                var row = new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase);
                if (row.Values.All(string.IsNullOrWhiteSpace)) continue;
                if (!TryInt(Get(row, "block"), out int block) || !TryInt(Get(row, "sub"), out int sub))
                {
                    bd.loadIssues.Add(new Issue(true, $"row {line}: 'block' and 'sub' must be whole numbers ('{Get(row, "block")}', '{Get(row, "sub")}')."));
                    continue;
                }
                var sb = new SubBlock
                {
                    Block = block, Sub = sub, Kind = Get(row, "kind") ?? "", Turn = Num(row, "turn", bd, line) != 0,
                    Length = Num(row, "length", bd, line), Breadth = Num(row, "breadth", bd, line),
                    Depth = Num(row, "depth", bd, line), Weight = Num(row, "weight", bd, line),
                };
                foreach (var kv in row) sb.Columns[kv.Key.Trim().ToLowerInvariant()] = (kv.Value ?? "").Trim();
                bd.subBlocks.Add(sb);
                if (!bd.blocks.TryGetValue(block, out var b)) bd.blocks[block] = b = new Block { Number = block };
                b.SubBlocks.Add(sb);
                BlockLevel(bd, b, row, line);
            }
            foreach (var b in bd.blocks.Values) b.SubBlocks.Sort((x, y) => x.Sub.CompareTo(y.Sub));
            return bd;
        }

        // block-level columns: written once or repeated equal
        private static void BlockLevel(BlockBreakdown bd, Block b, Dictionary<string, string> row, int line)
        {
            void SetNum(string col, Func<double> get, Action<double> set)
            {
                string t = Get(row, col);
                if (string.IsNullOrWhiteSpace(t)) return;
                double v = Num(row, col, bd, line);
                double old = get();
                if (old != 0 && Math.Abs(old - v) > 1e-9)
                    bd.loadIssues.Add(new Issue(true, $"block {b.Number}: '{col}' differs between its rows ({old} / {v})."));
                set(v);
            }
            SetNum("erect_seq", () => b.ErectSeq, v => b.ErectSeq = (int)Math.Round(v));
            SetNum("hull_x", () => b.HullX, v => b.HullX = v);
            SetNum("hull_y", () => b.HullY, v => b.HullY = v);
            SetNum("hull_z", () => b.HullZ, v => b.HullZ = v);
            string after = Get(row, "erect_after");
            if (!string.IsNullOrWhiteSpace(after))
            {
                var list = new List<int>();
                foreach (var tok in after.Split(new[] { ' ', ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                    if (TryInt(tok, out int p)) list.Add(p);
                    else bd.loadIssues.Add(new Issue(true, $"block {b.Number}: 'erect_after' token '{tok}' is not a block number."));
                if (b.ErectAfter.Count > 0 && !b.ErectAfter.OrderBy(x => x).SequenceEqual(list.Distinct().OrderBy(x => x)))
                    bd.loadIssues.Add(new Issue(true, $"block {b.Number}: 'erect_after' differs between its rows."));
                b.ErectAfter.Clear();
                b.ErectAfter.AddRange(list.Distinct());
            }
        }

        // ------------------------------------------------------------------ checks

        /// <summary>Errors (the breakdown cannot be used) and warnings.</summary>
        public List<Issue> Validate()
        {
            var issues = new List<Issue>(loadIssues);
            if (subBlocks.Count == 0) { issues.Add(new Issue(true, "the breakdown has no rows.")); return issues; }
            foreach (var g in subBlocks.GroupBy(s => (s.Block, s.Sub)).Where(g => g.Count() > 1))
                issues.Add(new Issue(true, $"sub-block {g.Key.Block}.{g.Key.Sub} appears {g.Count()} times."));
            foreach (var b in blocks.Values)
            {
                foreach (int p in b.ErectAfter)
                {
                    if (p == b.Number) issues.Add(new Issue(true, $"block {b.Number} is erected after itself."));
                    else if (!blocks.ContainsKey(p)) issues.Add(new Issue(true, $"block {b.Number}: erected after block {p}, which is not in the breakdown."));
                }
                if (b.SubBlocks.Any(s => s.Weight <= 0) && subBlocks.Any(s => s.Weight > 0))
                    issues.Add(new Issue(false, $"block {b.Number}: a sub-block without a weight."));
            }
            bool anySeq = blocks.Values.Any(b => b.ErectSeq != 0), anyAfter = blocks.Values.Any(b => b.ErectAfter.Count > 0);
            if (anySeq)
            {
                foreach (var b in blocks.Values.Where(b => b.ErectSeq == 0))
                    issues.Add(new Issue(true, $"block {b.Number} has no 'erect_seq' (the others have one)."));
                foreach (var g in blocks.Values.Where(b => b.ErectSeq != 0).GroupBy(b => b.ErectSeq).Where(g => g.Count() > 1))
                    issues.Add(new Issue(true, $"erect_seq {g.Key} is given to blocks {string.Join(", ", g.Select(b => b.Number))}."));
            }
            if (anyAfter)
            {
                var cycle = FindCycle();
                if (cycle != null) issues.Add(new Issue(true, "the erection precedence has a cycle: " + string.Join(" → ", cycle) + "."));
                else if (anySeq)
                    foreach (var b in blocks.Values)
                        foreach (int p in b.ErectAfter)
                            if (blocks.TryGetValue(p, out var pb) && pb.ErectSeq != 0 && b.ErectSeq != 0 && pb.ErectSeq >= b.ErectSeq)
                                issues.Add(new Issue(true, $"block {b.Number} (erect_seq {b.ErectSeq}) is erected after block {p} (erect_seq {pb.ErectSeq}): the sequence breaks the precedence."));
            }
            if (!anySeq && !anyAfter)
                issues.Add(new Issue(false, "no 'erect_seq' nor 'erect_after': blocks are erected in block-number order."));
            return issues;
        }

        /// <summary>True when <see cref="Validate"/> finds no error.</summary>
        public bool IsValid => Validate().All(i => !i.IsError);

        private List<int> FindCycle()
        {
            var state = new Dictionary<int, int>();   // 0 new, 1 on the stack, 2 done
            var stack = new List<int>();
            List<int> found = null;
            bool Visit(int n)
            {
                state[n] = 1; stack.Add(n);
                foreach (int p in blocks[n].ErectAfter)
                {
                    if (!blocks.ContainsKey(p)) continue;
                    state.TryGetValue(p, out int st);
                    if (st == 1) { found = stack.Skip(stack.IndexOf(p)).Concat(new[] { p }).ToList(); return true; }
                    if (st == 0 && Visit(p)) return true;
                }
                stack.RemoveAt(stack.Count - 1); state[n] = 2;
                return false;
            }
            foreach (int n in blocks.Keys)
            {
                state.TryGetValue(n, out int st);
                if (st == 0 && Visit(n)) return found;
            }
            return null;
        }

        // ------------------------------------------------------------------ erection order

        /// <summary>
        /// The blocks in erection order: by <c>erect_seq</c> when given; otherwise a precedence-respecting order
        /// (earliest level first, then block number); without either, block-number order.
        /// </summary>
        public List<Block> ErectionOrder()
        {
            if (blocks.Values.Any(b => b.ErectSeq != 0))
                return blocks.Values.OrderBy(b => b.ErectSeq).ThenBy(b => b.Number).ToList();
            var level = ErectionLevels();
            return blocks.Values.OrderBy(b => level[b.Number]).ThenBy(b => b.Number).ToList();
        }

        /// <summary>Each block's precedence level (0 = no predecessor; a block is one above its latest predecessor).
        /// The highest level + 1 is the length of the critical erection chain in blocks.</summary>
        public Dictionary<int, int> ErectionLevels()
        {
            var level = new Dictionary<int, int>();
            int Level(int n, int depth)
            {
                if (level.TryGetValue(n, out int l)) return l;
                if (depth > blocks.Count) throw new InvalidOperationException("Block breakdown: the erection precedence has a cycle.");
                int v = 0;
                foreach (int p in blocks[n].ErectAfter)
                    if (blocks.ContainsKey(p)) v = Math.Max(v, Level(p, depth + 1) + 1);
                level[n] = v;
                return v;
            }
            foreach (int n in blocks.Keys) Level(n, 0);
            return level;
        }

        // ------------------------------------------------------------------ generation

        /// <summary>
        /// The table of a generated part, in the form a Schedule source reads (<c>Name</c>, <c>Time</c>, <c>Q</c> and the
        /// label columns): <see cref="Part.SubBlocks"/> one kit per sub-block (every breakdown column + <c>nsub</c>),
        /// <see cref="Part.Blocks"/> one order per block (<c>block</c>, <c>nsub</c> = its sub-blocks — the assembly's
        /// component count —, <c>weight</c>, <c>erect_seq</c>, <c>erect_after</c>, <c>hull_*</c>, <c>level</c>),
        /// <see cref="Part.Erection"/> one erection token per block in erection order. Rows follow the erection order;
        /// times from <paramref name="options"/>.
        /// </summary>
        public Dictionary<string, List<string>> Generate(Part part, ReleaseOptions options = null)
        {
            var errors = Validate().Where(i => i.IsError).ToList();
            if (errors.Count > 0)
                throw new FormatException("Block breakdown: " + string.Join(" ", errors.Select(e => e.Message)));
            options = options ?? new ReleaseOptions();
            var order = ErectionOrder();
            var levels = ErectionLevels();
            var rows = new List<Dictionary<string, string>>();
            for (int i = 0; i < order.Count; i++)
            {
                var b = order[i];
                double t = options.Start + i * options.BlockInterval;
                int seq = b.ErectSeq != 0 ? b.ErectSeq : i + 1;
                var blockCols = new Dictionary<string, string>
                {
                    ["block"] = Text(b.Number), ["nsub"] = Text(b.SubBlocks.Count), ["weight"] = Text(b.Weight),
                    ["erect_seq"] = Text(seq), ["erect_after"] = string.Join(" ", b.ErectAfter),
                    ["hull_x"] = Text(b.HullX), ["hull_y"] = Text(b.HullY), ["hull_z"] = Text(b.HullZ),
                    ["level"] = Text(levels[b.Number]),
                };
                switch (part)
                {
                    case Part.SubBlocks:
                        foreach (var s in b.SubBlocks)
                        {
                            var r = Row(options.SubBlockName, Math.Max(0, t - options.SubBlockLead));
                            foreach (var kv in s.Columns) r[kv.Key] = kv.Value;
                            foreach (var kv in blockCols) r[kv.Key] = kv.Value;
                            r["sub"] = Text(s.Sub);
                            r["weight"] = Text(s.Weight);       // the sub-block's own weight
                            r["turn"] = s.Turn ? "1" : "0";
                            rows.Add(r);
                        }
                        break;
                    case Part.Blocks:
                    {
                        var r = Row(options.BlockName, t);
                        foreach (var kv in blockCols) r[kv.Key] = kv.Value;
                        rows.Add(r);
                        break;
                    }
                    case Part.Erection:
                    {
                        var r = Row(options.ErectionName, t);
                        foreach (var kv in blockCols) r[kv.Key] = kv.Value;
                        rows.Add(r);
                        break;
                    }
                }
            }
            return ToColumns(rows);
        }

        private static Dictionary<string, string> Row(string name, double time) =>
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Name"] = name, ["Time"] = Text(time), ["Q"] = "1" };

        private static Dictionary<string, List<string>> ToColumns(List<Dictionary<string, string>> rows)
        {
            var cols = new List<string> { "Name", "Time", "Q" };
            foreach (var r in rows)
                foreach (var k in r.Keys)
                    if (!cols.Contains(k, StringComparer.OrdinalIgnoreCase)) cols.Add(k);
            var table = new Dictionary<string, List<string>>();
            foreach (var c in cols) table[c] = rows.Select(r => r.TryGetValue(c, out var v) ? v : "").ToList();
            return table;
        }

        /// <summary>Parses a part name: <c>subblocks</c> / <c>kits</c>, <c>blocks</c> / <c>orders</c>, <c>erection</c> / <c>plan</c>.</summary>
        public static Part ParsePart(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "subblocks": case "sub-blocks": case "kits": case "subblock": return Part.SubBlocks;
                case "blocks": case "orders": case "block": return Part.Blocks;
                case "erection": case "plan": case "erect": return Part.Erection;
                default: throw new FormatException($"Block breakdown: unknown part '{name}' (subblocks, blocks or erection).");
            }
        }

        // ------------------------------------------------------------------ writing

        /// <summary>Writes the breakdown's rows to <paramref name="table"/> of the database (replacing it): every column as
        /// TEXT, so a table imported from Excel / CSV is read back exactly.</summary>
        public void Save(SimuLean.Data.ProjectDatabase db, string table = DefaultTable)
        {
            var cols = new List<string>();
            foreach (var s in subBlocks)
                foreach (var k in s.Columns.Keys)
                    if (!cols.Contains(k)) cols.Add(k);
            string q(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";
            db.Execute($"DROP TABLE IF EXISTS {q(table)}");
            db.Execute($"CREATE TABLE {q(table)} ({string.Join(", ", cols.Select(c => q(c) + " TEXT"))})");
            db.ExecuteBatch($"INSERT INTO {q(table)} ({string.Join(", ", cols.Select(q))}) VALUES ({string.Join(", ", cols.Select(_ => "?"))})",
                            subBlocks.Select(s => cols.Select(c => (object)(s.Columns.TryGetValue(c, out var v) ? v : "")).ToArray()));
        }

        // ------------------------------------------------------------------ helpers

        private static string Get(Dictionary<string, string> row, string col) => row.TryGetValue(col, out var v) ? v?.Trim() : null;

        private static double Num(Dictionary<string, string> row, string col, BlockBreakdown bd, int line)
        {
            string t = Get(row, col);
            if (string.IsNullOrWhiteSpace(t)) return 0;
            if (double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
            bd.loadIssues.Add(new Issue(true, $"row {line}: '{col}' = '{t}' is not a number."));
            return 0;
        }

        private static bool TryInt(string t, out int v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(t)) return false;
            if (int.TryParse(t.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return true;
            if (double.TryParse(t.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && Math.Abs(d - Math.Round(d)) < 1e-9)
            { v = (int)Math.Round(d); return true; }
            return false;
        }

        private static string Text(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static string Text(int v) => v.ToString(CultureInfo.InvariantCulture);

        private static string ToText(object v) => v switch
        {
            null => "",
            double d => Text(d),
            float f => Text(f),
            long l => l.ToString(CultureInfo.InvariantCulture),
            int i => Text(i),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        };
    }
}
