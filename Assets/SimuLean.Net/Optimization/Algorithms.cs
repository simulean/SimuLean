using System;
using System.Collections.Generic;

namespace SimuLean.Optimization
{
    /// <summary>
    /// Uniform random sampling of the space, in batches: the baseline any algorithm must beat, and a reasonable choice
    /// for small spaces. Stops after <see cref="Samples"/> evaluations (or the run's budget).
    /// </summary>
    public sealed class RandomSearch : IOptimizationAlgorithm
    {
        public string Name => "Random search";

        /// <summary>Candidates to evaluate.</summary>
        public int Samples { get; set; } = 100;

        /// <summary>Candidates per iteration (one progress point each).</summary>
        public int BatchSize { get; set; } = 10;

        public void Run(OptimizationRun run)
        {
            int done = 0, attempts = 0;
            while (done < Samples && !run.ShouldStop && attempts < Samples * 20)
            {
                int n = Math.Min(Math.Max(1, BatchSize), Samples - done);
                var batch = new List<Candidate>(n);
                for (int i = 0; i < n; i++) batch.Add(run.RandomCandidate());
                int before = run.Evaluations;
                run.Evaluate(batch);
                done += Math.Max(1, run.Evaluations - before);   // repeated points cost nothing; avoid looping forever
                attempts += n;
                run.EndIteration();
            }
        }
    }

    /// <summary>
    /// Genetic algorithm for mixed spaces (integers, reals — single or vectors —, choices, permutations, binary
    /// vectors), reproducible with the run's seed: tournament selection, elitism, and per-variable operators — uniform /
    /// blend crossover and step / Gaussian mutation for numbers (per component for vectors), a parent's option and a
    /// different option for choices, order crossover (OX) and swap mutation for permutations, uniform crossover and bit
    /// flips for binary vectors. One iteration = one generation.
    /// </summary>
    public sealed class GeneticOptimizer : IOptimizationAlgorithm
    {
        public string Name => "Genetic algorithm";

        public int PopulationSize { get; set; } = 30;
        public int Generations { get; set; } = 50;
        /// <summary>Probability that two parents are crossed (else the child copies the first parent).</summary>
        public double CrossoverRate { get; set; } = 0.9;
        /// <summary>Probability that each variable of a child is mutated.</summary>
        public double MutationRate { get; set; } = 0.15;
        /// <summary>Best candidates copied unchanged to the next generation.</summary>
        public int Elites { get; set; } = 2;
        public int TournamentSize { get; set; } = 3;

        public void Run(OptimizationRun run)
        {
            int size = Math.Max(2, PopulationSize);
            var population = new List<Candidate>(size);
            for (int i = 0; i < size; i++) population.Add(run.RandomCandidate());
            var fitness = Fitness(run.Evaluate(population));
            run.EndIteration();

            for (int g = 1; g < Generations && !run.ShouldStop; g++)
            {
                var order = Ranking(fitness);
                var next = new List<Candidate>(size);
                for (int e = 0; e < Math.Min(Elites, size); e++) next.Add(population[order[e]]);
                while (next.Count < size)
                {
                    Candidate a = population[Tournament(run, fitness)], b = population[Tournament(run, fitness)];
                    Candidate child = run.Random.NextDouble() < CrossoverRate ? Cross(run, a, b) : a.Clone();
                    Mutate(run, child);
                    next.Add(child);
                }
                population = next;
                fitness = Fitness(run.Evaluate(population));
                run.EndIteration();
            }
        }

        private static double[] Fitness(IReadOnlyList<Evaluation> evals)
        {
            var f = new double[evals.Count];
            for (int i = 0; i < f.Length; i++) f[i] = evals[i]?.Fitness ?? double.NegativeInfinity;   // beyond the budget
            return f;
        }

        private static int[] Ranking(double[] fitness)
        {
            var idx = new int[fitness.Length];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            Array.Sort(idx, (x, y) => { int c = fitness[y].CompareTo(fitness[x]); return c != 0 ? c : x.CompareTo(y); });
            return idx;
        }

        private int Tournament(OptimizationRun run, double[] fitness)
        {
            int best = run.RandomInt(0, fitness.Length - 1);
            for (int k = 1; k < Math.Max(1, TournamentSize); k++)
            {
                int c = run.RandomInt(0, fitness.Length - 1);
                if (fitness[c] > fitness[best]) best = c;
            }
            return best;
        }

        private static Candidate Cross(OptimizationRun run, Candidate a, Candidate b)
        {
            var child = a.Clone();
            for (int i = 0; i < run.Space.Count; i++)
            {
                var v = run.Space.Variables[i];
                switch (v.Kind)
                {
                    case VariableKind.Integer:
                    case VariableKind.Choice:
                        if (v.Length == 1) child.SetNumber(i, run.Random.NextDouble() < 0.5 ? a.Number(i) : b.Number(i));
                        else
                        {
                            var x = a.Values(i); var y = b.Values(i); var r = new double[x.Length];
                            for (int k = 0; k < r.Length; k++) r[k] = run.Random.NextDouble() < 0.5 ? x[k] : y[k];
                            child.SetValues(i, r);
                        }
                        break;
                    case VariableKind.Real:
                    {
                        // blend (BLX-0.5): anywhere between the parents, a little beyond (per component)
                        var x = a.Values(i); var y = b.Values(i); var r = new double[x.Length];
                        for (int k = 0; k < r.Length; k++)
                        {
                            double lo = Math.Min(x[k], y[k]), hi = Math.Max(x[k], y[k]), d = hi - lo;
                            r[k] = lo - 0.5 * d + run.Random.NextDouble() * 2 * d;
                        }
                        if (r.Length == 1) child.SetNumber(i, r[0]); else child.SetValues(i, r);
                        break;
                    }
                    case VariableKind.Permutation:
                        child.SetVector(i, OrderCrossover(run, a.Vector(i), b.Vector(i)));
                        break;
                    case VariableKind.Binary:
                    {
                        var x = a.Vector(i); var y = b.Vector(i); var bits = new int[x.Length];
                        for (int k = 0; k < bits.Length; k++) bits[k] = run.Random.NextDouble() < 0.5 ? x[k] : y[k];
                        child.SetVector(i, bits);
                        break;
                    }
                }
            }
            return child;
        }

        /// <summary>OX1: a slice of the first parent, the rest in the second parent's order.</summary>
        private static int[] OrderCrossover(OptimizationRun run, int[] p1, int[] p2)
        {
            int n = p1.Length;
            if (n < 2) return (int[])p1.Clone();
            int s = run.RandomInt(0, n - 1), e = run.RandomInt(0, n - 1);
            if (s > e) { int t = s; s = e; e = t; }
            var child = new int[n];
            var used = new bool[n];
            for (int k = s; k <= e; k++) { child[k] = p1[k]; used[p1[k]] = true; }
            int pos = (e + 1) % n;
            for (int k = 0; k < n; k++)
            {
                int gene = p2[(e + 1 + k) % n];
                if (used[gene]) continue;
                child[pos] = gene; used[gene] = true;
                pos = (pos + 1) % n;
            }
            return child;
        }

        private void Mutate(OptimizationRun run, Candidate c)
        {
            for (int i = 0; i < run.Space.Count; i++)
            {
                if (run.Random.NextDouble() >= MutationRate) continue;
                var v = run.Space.Variables[i];
                switch (v.Kind)
                {
                    case VariableKind.Integer:
                    {
                        int range = (int)(v.Max - v.Min);
                        int step = Math.Max(1, range / 10);
                        int delta = run.RandomInt(1, step) * (run.Random.NextDouble() < 0.5 ? -1 : 1);
                        int k = v.Length == 1 ? 0 : run.RandomInt(0, v.Length - 1);   // one component of a vector
                        var x = (double[])c.Values(i).Clone(); x[k] += delta;
                        c.SetValues(i, x);
                        break;
                    }
                    case VariableKind.Real:
                    {
                        int k = v.Length == 1 ? 0 : run.RandomInt(0, v.Length - 1);
                        var x = (double[])c.Values(i).Clone(); x[k] += run.Random.NextStandardNormal() * 0.1 * (v.Max - v.Min);
                        c.SetValues(i, x);
                        break;
                    }
                    case VariableKind.Choice:
                        if (v.Options.Count > 1)
                        {
                            int o = run.RandomInt(0, v.Options.Count - 2);   // another option
                            c.SetNumber(i, o >= (int)c.Number(i) ? o + 1 : o);
                        }
                        break;
                    case VariableKind.Permutation:
                    {
                        var p = (int[])c.Vector(i).Clone();
                        int x = run.RandomInt(0, p.Length - 1), y = run.RandomInt(0, p.Length - 1);
                        int t = p[x]; p[x] = p[y]; p[y] = t;
                        c.SetVector(i, p);
                        break;
                    }
                    case VariableKind.Binary:
                    {
                        var bits = (int[])c.Vector(i).Clone();
                        bool flipped = false;
                        for (int k = 0; k < bits.Length; k++)
                            if (run.Random.NextDouble() < 1.0 / bits.Length) { bits[k] = 1 - bits[k]; flipped = true; }
                        if (!flipped) { int k = run.RandomInt(0, bits.Length - 1); bits[k] = 1 - bits[k]; }
                        c.SetVector(i, bits);
                        break;
                    }
                }
            }
        }
    }
}
