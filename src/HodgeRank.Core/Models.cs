namespace HodgeRank.Core;

/// <summary>Positive strength means First is preferred to Second.</summary>
public sealed record Comparison(string First, string Second, double Strength);

public sealed record RankingProblem(string[] Objects, Comparison[] Comparisons);

public sealed record ObjectScore(string Object, double Score);

public sealed record EdgeFlow(
    string First, string Second, double Observed, double Consistent, double Residual);

public sealed record RankingMetrics(
    int ObjectCount,
    int ComparisonCount,
    int IndependentCycleCount,
    double LeastSquaresError,
    double RootMeanSquaredError,
    double CycleInconsistency,
    double LaplacianResidual,
    double RelativeLaplacianResidual,
    bool HasPreferenceSignal,
    double RuntimeMilliseconds);

/// <summary>Scores retain input order; matrices use comparison and object input order.</summary>
public sealed record RankingResult(
    ObjectScore[] Scores,
    EdgeFlow[] Edges,
    double[][] IncidenceMatrix,
    double[][] Laplacian,
    RankingMetrics Metrics);
