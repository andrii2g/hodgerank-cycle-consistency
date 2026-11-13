using System.Diagnostics;
using MathNet.Numerics.LinearAlgebra;

namespace HodgeRank.Core;

/// <summary>Unweighted least-squares projection onto the graph's gradient flows.</summary>
public sealed class HodgeRankSolver
{
    public RankingResult Solve(RankingProblem problem)
    {
        long started = Stopwatch.GetTimestamp();
        var indices = Validate(problem);
        int n = problem.Objects.Length;
        int m = problem.Comparisons.Length;
        var incidence = Matrix<double>.Build.Dense(m, n);
        var observations = Vector<double>.Build.Dense(m);
        double scale = problem.Comparisons.Max(edge => Math.Abs(edge.Strength));
        bool hasSignal = scale > 0;

        // Normalize before solving so the matrix calculation is independent of input units.
        // The all-zero case still has the same graph topology and a unique zero-mean fit.
        double divisor = hasSignal ? scale : 1;
        for (int e = 0; e < m; e++)
        {
            var edge = problem.Comparisons[e];
            incidence[e, indices[edge.First]] = 1;
            incidence[e, indices[edge.Second]] = -1;
            observations[e] = edge.Strength / divisor;
        }

        var laplacian = incidence.TransposeThisAndMultiply(incidence);
        var rightHandSide = incidence.TransposeThisAndMultiply(observations);

        // L is singular along the constant vector. This rank-one term fixes that
        // direction without changing L on the zero-mean subspace (no ridge bias).
        var constrained = laplacian + Matrix<double>.Build.Dense(n, n, 1.0 / n);
        var scores = constrained.Cholesky().Solve(rightHandSide);
        scores -= Vector<double>.Build.Dense(n, scores.Sum() / n);

        var consistent = incidence * scores;
        var residual = observations - consistent;
        double residualNorm = residual.L2Norm();
        double scaledResidualNorm = residualNorm * divisor;
        double squaredError = scaledResidualNorm * scaledResidualNorm;
        double observationEnergy = observations.DotProduct(observations);
        double cycleInconsistency = hasSignal
            ? Math.Clamp(residual.DotProduct(residual) / observationEnergy, 0, 1)
            : 0;
        double laplacianResidual = (laplacian * scores - rightHandSide).L2Norm();

        var objectScores = problem.Objects.Select((name, i) =>
            new ObjectScore(name, scores[i] * divisor)).ToArray();
        var edgeFlows = problem.Comparisons.Select((edge, e) =>
            new EdgeFlow(edge.First, edge.Second, edge.Strength,
                consistent[e] * divisor, residual[e] * divisor)).ToArray();

        var metrics = new RankingMetrics(n, m, m - n + 1,
            squaredError, scaledResidualNorm / Math.Sqrt(m), cycleInconsistency,
            laplacianResidual * divisor,
            laplacianResidual / Math.Max(1, rightHandSide.L2Norm()),
            hasSignal, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        // Reject unrepresentable output instead of emitting Infinity or invalid JSON.
        if (objectScores.Any(value => !double.IsFinite(value.Score)) ||
            edgeFlows.Any(edge => !double.IsFinite(edge.Consistent) || !double.IsFinite(edge.Residual)) ||
            !double.IsFinite(metrics.LeastSquaresError) ||
            !double.IsFinite(metrics.RootMeanSquaredError) ||
            !double.IsFinite(metrics.LaplacianResidual))
        {
            throw new ArgumentException(
                "Preference strengths exceed the representable output range. Rescale the strengths.",
                nameof(problem));
        }

        return new RankingResult(objectScores, edgeFlows,
            ToRows(incidence), ToRows(laplacian), metrics);
    }

    private static Dictionary<string, int> Validate(RankingProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(problem.Objects);
        ArgumentNullException.ThrowIfNull(problem.Comparisons);
        if (problem.Objects.Length is < 3 or > 20)
        {
            throw new ArgumentException("Provide between 3 and 20 objects.", nameof(problem));
        }

        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < problem.Objects.Length; i++)
        {
            string name = problem.Objects[i];
            if (string.IsNullOrWhiteSpace(name) || !indices.TryAdd(name, i))
            {
                throw new ArgumentException("Object names must be nonblank and unique (case-sensitive).", nameof(problem));
            }
        }

        int n = problem.Objects.Length;
        int maximumEdges = n * (n - 1) / 2;
        if (problem.Comparisons.Length < n - 1 || problem.Comparisons.Length > maximumEdges)
        {
            throw new ArgumentException($"Provide {n - 1} to {maximumEdges} distinct comparisons for {n} objects.", nameof(problem));
        }

        var pairs = new HashSet<(int, int)>();
        var adjacency = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        foreach (var edge in problem.Comparisons)
        {
            if (edge is null || edge.First is null || edge.Second is null ||
                !indices.TryGetValue(edge.First, out int first) ||
                !indices.TryGetValue(edge.Second, out int second))
            {
                throw new ArgumentException("Every comparison must refer to two existing objects.", nameof(problem));
            }

            if (first == second)
            {
                throw new ArgumentException("Self-comparisons are not allowed.", nameof(problem));
            }

            if (!double.IsFinite(edge.Strength))
            {
                throw new ArgumentException("Comparison strengths must be finite numbers.", nameof(problem));
            }

            var pair = (Math.Min(first, second), Math.Max(first, second));
            if (!pairs.Add(pair))
            {
                throw new ArgumentException("Each unordered pair may appear only once, including reversed comparisons.", nameof(problem));
            }

            adjacency[first].Add(second);
            adjacency[second].Add(first);
        }

        var visited = new HashSet<int> { 0 };
        var pending = new Queue<int>();
        pending.Enqueue(0);
        while (pending.TryDequeue(out int current))
        {
            foreach (int neighbor in adjacency[current])
            {
                if (visited.Add(neighbor))
                {
                    pending.Enqueue(neighbor);
                }
            }
        }

        if (visited.Count != n)
        {
            throw new ArgumentException("The comparison graph must be connected, including every object.", nameof(problem));
        }

        return indices;
    }

    private static double[][] ToRows(Matrix<double> matrix) =>
        Enumerable.Range(0, matrix.RowCount).Select(i => matrix.Row(i).ToArray()).ToArray();
}
