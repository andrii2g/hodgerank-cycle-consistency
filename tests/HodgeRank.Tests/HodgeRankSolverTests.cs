using HodgeRank.Core;
using Xunit;

namespace HodgeRank.Tests;

public sealed class HodgeRankSolverTests
{
    private readonly HodgeRankSolver solver = new();

    [Fact]
    public void ConsistentTriangleRecoversZeroMeanScoresAndMatrices()
    {
        var result = solver.Solve(Triangle(1, 1, 2));

        Assert.Equal(new[] { "A", "B", "C" }, result.Scores.Select(score => score.Object));
        AssertScores(result, 1, 0, -1);
        Assert.Equal(new[] { 1d, -1, 0 }, result.IncidenceMatrix[0]);
        Assert.Equal(new[] { 0d, 1, -1 }, result.IncidenceMatrix[1]);
        Assert.Equal(new[] { 1d, 0, -1 }, result.IncidenceMatrix[2]);
        Assert.Equal(new[] { 2d, -1, -1 }, result.Laplacian[0]);
        Assert.Equal(new[] { -1d, 2, -1 }, result.Laplacian[1]);
        Assert.Equal(new[] { -1d, -1, 2 }, result.Laplacian[2]);
        Assert.InRange(result.Metrics.LeastSquaresError, 0, 1e-24);
        Assert.InRange(result.Metrics.CycleInconsistency, 0, 1e-24);
        Assert.Equal(3, result.Metrics.ObjectCount);
        Assert.Equal(3, result.Metrics.ComparisonCount);
        Assert.Equal(1, result.Metrics.IndependentCycleCount);
        Assert.True(result.Metrics.HasPreferenceSignal);
        Assert.InRange(result.Metrics.RuntimeMilliseconds, 0, double.MaxValue);
        AssertDecomposition(result);
    }

    [Fact]
    public void RockPaperScissorsIsEntirelyCyclic()
    {
        var result = solver.Solve(Triangle(1, 1, -1));

        AssertScores(result, 0, 0, 0);
        AssertNear(3, result.Metrics.LeastSquaresError);
        AssertNear(1, result.Metrics.RootMeanSquaredError);
        AssertNear(1, result.Metrics.CycleInconsistency);
        foreach (var edge in result.Edges)
        {
            AssertNear(0, edge.Consistent);
            AssertNear(edge.Observed, edge.Residual);
        }

        AssertDecomposition(result);
    }

    [Fact]
    public void TransitiveDirectionsCanHaveInconsistentStrengths()
    {
        var result = solver.Solve(Triangle(1, 1, 1));

        AssertScores(result, 2d / 3, 0, -2d / 3);
        AssertNear(1d / 3, result.Metrics.LeastSquaresError);
        AssertNear(1d / 9, result.Metrics.CycleInconsistency);
        AssertDecomposition(result);
    }

    [Fact]
    public void TreeAlwaysFitsItsComparisons()
    {
        var problem = new RankingProblem(
            ["A", "B", "C", "D", "E"],
            [new("A", "B", 3), new("B", "C", -2), new("B", "D", 0), new("D", "E", 4)]);

        var result = solver.Solve(problem);

        Assert.Equal(0, result.Metrics.IndependentCycleCount);
        Assert.InRange(result.Metrics.CycleInconsistency, 0, 1e-24);
        Assert.All(result.Edges, edge => AssertNear(edge.Observed, edge.Consistent));
        AssertDecomposition(result);
    }

    [Fact]
    public void FourCycleWithoutTrianglesStillHasCyclicResidual()
    {
        var problem = new RankingProblem(
            ["A", "B", "C", "D"],
            [new("A", "B", 1), new("B", "C", 1), new("C", "D", 1), new("D", "A", 1)]);

        var result = solver.Solve(problem);

        AssertScores(result, 0, 0, 0, 0);
        AssertNear(4, result.Metrics.LeastSquaresError);
        AssertNear(1, result.Metrics.CycleInconsistency);
        Assert.Equal(1, result.Metrics.IndependentCycleCount);
        AssertDecomposition(result);
    }

    [Fact]
    public void ObservedTiesRemainEdgesAndConnectTheGraph()
    {
        var problem = new RankingProblem(
            ["A", "B", "C"],
            [new("A", "B", 0), new("B", "C", 3)]);

        var result = solver.Solve(problem);

        AssertScores(result, 1, 1, -2);
        Assert.Equal(2, result.Metrics.ComparisonCount);
        AssertNear(0, result.Edges[0].Observed);
        AssertDecomposition(result);
    }

    [Fact]
    public void AllZeroObservationsHaveNoPreferenceSignal()
    {
        var result = solver.Solve(Triangle(0, 0, 0));

        AssertScores(result, 0, 0, 0);
        Assert.False(result.Metrics.HasPreferenceSignal);
        Assert.Equal(0, result.Metrics.LeastSquaresError);
        Assert.Equal(0, result.Metrics.RootMeanSquaredError);
        Assert.Equal(0, result.Metrics.CycleInconsistency);
        Assert.Equal(0, result.Metrics.LaplacianResidual);
        AssertDecomposition(result);
    }

    [Fact]
    public void ReversingEdgeOrientationsAndNegatingStrengthsPreservesScores()
    {
        var original = Triangle(2, -4, 1);
        var reversed = original with
        {
            Comparisons = original.Comparisons
                .Select(edge => new Comparison(edge.Second, edge.First, -edge.Strength))
                .ToArray(),
        };

        var expected = solver.Solve(original);
        var actual = solver.Solve(reversed);

        AssertScores(actual, expected.Scores.Select(score => score.Score).ToArray());
        AssertNear(expected.Metrics.LeastSquaresError, actual.Metrics.LeastSquaresError);
        AssertNear(expected.Metrics.CycleInconsistency, actual.Metrics.CycleInconsistency);
        for (var index = 0; index < expected.Edges.Length; index++)
        {
            AssertNear(-expected.Edges[index].Consistent, actual.Edges[index].Consistent);
            AssertNear(-expected.Edges[index].Residual, actual.Edges[index].Residual);
        }
    }

    [Fact]
    public void ReorderingObjectsAndComparisonsPreservesObjectScores()
    {
        var problem = Triangle(2, -4, 1);
        var expected = solver.Solve(problem).Scores.ToDictionary(score => score.Object, score => score.Score);
        var reordered = problem with
        {
            Objects = ["C", "A", "B"],
            Comparisons = problem.Comparisons.Reverse().ToArray(),
        };

        var result = solver.Solve(reordered);

        Assert.Equal(reordered.Objects, result.Scores.Select(score => score.Object));
        Assert.All(result.Scores, score => AssertNear(expected[score.Object], score.Score));
        AssertDecomposition(result);
    }

    [Fact]
    public void RandomConnectedGraphsSatisfyProjectionIdentities()
    {
        var random = new Random(93719);
        for (var objectCount = 3; objectCount <= 20; objectCount++)
        {
            for (var sample = 0; sample < 4; sample++)
            {
                var objects = Enumerable.Range(0, objectCount).Select(index => $"Item {index}").ToArray();
                var comparisons = new List<Comparison>();
                for (var first = 0; first < objectCount; first++)
                {
                    for (var second = first + 1; second < objectCount; second++)
                    {
                        if (second == first + 1 || random.NextDouble() < 0.35)
                        {
                            comparisons.Add(new Comparison(objects[first], objects[second], 6 * random.NextDouble() - 3));
                        }
                    }
                }

                var result = solver.Solve(new RankingProblem(objects, comparisons.ToArray()));

                Assert.Equal(comparisons.Count - objectCount + 1, result.Metrics.IndependentCycleCount);
                AssertDecomposition(result);
            }
        }
    }

    [Theory]
    [InlineData(1e-100)]
    [InlineData(1e100)]
    public void ScalingStrengthsScalesScoresButPreservesInconsistency(double scale)
    {
        var reference = solver.Solve(Triangle(2, -4, 1));
        var actual = solver.Solve(Triangle(2 * scale, -4 * scale, scale));

        for (var index = 0; index < reference.Scores.Length; index++)
        {
            AssertNear(reference.Scores[index].Score, actual.Scores[index].Score / scale);
        }

        AssertNear(reference.Metrics.CycleInconsistency, actual.Metrics.CycleInconsistency);
        Assert.True(actual.Metrics.HasPreferenceSignal);
        AssertDecomposition(actual);
    }

    [Fact]
    public void DisconnectedGraphWithEnoughComparisonsIsRejected()
    {
        var problem = new RankingProblem(
            ["A", "B", "C", "D"],
            [new("A", "B", 1), new("B", "C", 1), new("A", "C", 2)]);

        var error = Assert.Throws<ArgumentException>(() => solver.Solve(problem));

        Assert.Equal("problem", error.ParamName);
        Assert.Contains("connected", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnrepresentableOutputExplainsHowToRescaleTheInput()
    {
        var problem = Triangle(double.MaxValue, -double.MaxValue, double.MaxValue);

        var error = Assert.Throws<ArgumentException>(() => solver.Solve(problem));

        Assert.Equal("problem", error.ParamName);
        Assert.Contains("rescale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object?[]> InvalidEdgeCases()
    {
        yield return [null, "existing objects"];
        yield return [new Comparison(null!, "B", 1), "existing objects"];
        yield return [new Comparison("A", null!, 1), "existing objects"];
        yield return [new Comparison("A", "missing", 1), "existing objects"];
        yield return [new Comparison("A", "A", 1), "Self-comparisons"];
    }

    [Theory]
    [MemberData(nameof(InvalidEdgeCases))]
    public void InvalidEdgesReachTheirSpecificValidation(Comparison? invalidEdge, string messageFragment)
    {
        var problem = new RankingProblem(
            ["A", "B", "C"],
            [invalidEdge!, new("B", "C", 1)]);

        var error = Assert.Throws<ArgumentException>(() => solver.Solve(problem));

        Assert.Equal("problem", error.ParamName);
        Assert.Contains(messageFragment, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object?[]> InvalidProblems()
    {
        yield return [null];
        yield return [new RankingProblem(null!, [])];
        yield return [new RankingProblem(["A", "B", "C"], null!)];
        yield return [new RankingProblem(["A", "B"], [new("A", "B", 1)])];
        yield return [new RankingProblem(Enumerable.Range(0, 21).Select(index => index.ToString()).ToArray(), [])];
        yield return [new RankingProblem(["A", "A", "C"], [])];
        yield return [new RankingProblem(["A", " ", "C"], [])];
        yield return [new RankingProblem(["A", null!, "C"], [])];
        yield return [new RankingProblem(["A", "B", "C"], [new("A", "B", 1)])];
        yield return [new RankingProblem(["A", "B", "C"], [])];
        yield return [Triangle(double.NaN, 1, 2)];
        yield return [Triangle(double.PositiveInfinity, 1, 2)];
        yield return [Triangle(double.NegativeInfinity, 1, 2)];
        yield return [new RankingProblem(["A", "B", "C"], [new("A", "B", 1), new("B", "C", 1), new("A", "B", 2)])];
        yield return [new RankingProblem(["A", "B", "C"], [new("A", "B", 1), new("B", "C", 1), new("B", "A", -1)])];
    }

    [Theory]
    [MemberData(nameof(InvalidProblems))]
    public void InvalidProblemsProduceArgumentErrors(RankingProblem? problem)
    {
        Assert.ThrowsAny<ArgumentException>(() => solver.Solve(problem!));
    }

    private static RankingProblem Triangle(double ab, double bc, double ac) =>
        new(["A", "B", "C"], [new("A", "B", ab), new("B", "C", bc), new("A", "C", ac)]);

    private static void AssertScores(RankingResult result, params double[] expected)
    {
        Assert.Equal(expected.Length, result.Scores.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            AssertNear(expected[index], result.Scores[index].Score);
        }
    }

    private static void AssertDecomposition(RankingResult result)
    {
        var scale = result.Edges.Max(edge => Math.Abs(edge.Observed));
        if (scale == 0)
        {
            scale = 1;
        }

        var normalizedScores = result.Scores.Select(score => score.Score / scale).ToArray();
        AssertNear(0, normalizedScores.Sum());
        var divergence = new double[result.Scores.Length];
        var dotProduct = 0d;
        var observedEnergy = 0d;
        var fittedEnergy = 0d;
        var residualEnergy = 0d;

        for (var edgeIndex = 0; edgeIndex < result.Edges.Length; edgeIndex++)
        {
            var edge = result.Edges[edgeIndex];
            var observed = edge.Observed / scale;
            var fitted = edge.Consistent / scale;
            var residual = edge.Residual / scale;
            var fromScores = result.IncidenceMatrix[edgeIndex]
                .Select((coefficient, objectIndex) => coefficient * normalizedScores[objectIndex])
                .Sum();
            AssertNear(fitted, fromScores);
            AssertNear(observed, fitted + residual);
            for (var objectIndex = 0; objectIndex < divergence.Length; objectIndex++)
            {
                divergence[objectIndex] += result.IncidenceMatrix[edgeIndex][objectIndex] * residual;
            }

            dotProduct += fitted * residual;
            observedEnergy += observed * observed;
            fittedEnergy += fitted * fitted;
            residualEnergy += residual * residual;
        }

        Assert.All(divergence, value => AssertNear(0, value));
        AssertNear(0, dotProduct);
        AssertNear(observedEnergy, fittedEnergy + residualEnergy);
        AssertNear(residualEnergy, result.Metrics.LeastSquaresError / scale / scale);
        AssertNear(Math.Sqrt(residualEnergy / result.Edges.Length), result.Metrics.RootMeanSquaredError / scale);
        Assert.InRange(result.Metrics.CycleInconsistency, 0, 1);
        AssertNear(observedEnergy == 0 ? 0 : residualEnergy / observedEnergy, result.Metrics.CycleInconsistency);
        Assert.InRange(result.Metrics.LaplacianResidual / scale, 0, 1e-9);
        Assert.InRange(result.Metrics.RelativeLaplacianResidual, 0, 1e-9);
    }

    private static void AssertNear(double expected, double actual)
    {
        Assert.True(double.IsFinite(actual), $"Expected a finite value; actual: {actual:G17}.");
        var tolerance = 1e-10 * Math.Max(1, Math.Abs(expected));
        Assert.True(Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected:G17}; actual {actual:G17}; tolerance {tolerance:G17}.");
    }
}
