using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HodgeRank.Core;

namespace HodgeRank.Cli;

internal static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
    };

    private const string Help = """
        HodgeRank — ranking and cycle consistency

        Usage:
          hodgerank analyze <input.json> [--json <result.json>] [--svg <report.svg>] [--force]
          hodgerank demo [--output <directory>] [--force]

        Run from source with: dotnet run --project src/HodgeRank.Cli -- <command>
        Demo output defaults to artifacts/demo. Existing output files require --force.
        Input: {"objects":["A","B","C"],"comparisons":[
                 {"first":"A","second":"B","strength":1},
                 {"first":"B","second":"C","strength":1}]}
        Positive strength means first is preferred to second. Zero means an observed tie.
        Exit codes: 0 success/help, 2 invalid arguments/data, 1 file or access error.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Length == 0 || args is ["--help"] or ["-h"] ||
                args is ["analyze" or "demo", "--help" or "-h"])
            {
                output.WriteLine(Help);
                return 0;
            }

            switch (args[0])
            {
                case "analyze":
                    Analyze(args, output);
                    break;
                case "demo":
                    Demo(args, output);
                    break;
                default:
                    throw new ArgumentException($"Unknown command '{args[0]}'. Use --help for usage.");
            }

            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            error.WriteLine($"Invalid input: {exception.Message}");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error.WriteLine($"File error: {exception.Message}");
            return 1;
        }
    }

    private static void Analyze(string[] args, TextWriter output)
    {
        if (args.Length < 2 || args[1].StartsWith('-'))
        {
            throw new ArgumentException("analyze requires an input JSON path.");
        }

        var options = ParseOptions(args, 2, ["--json", "--svg"]);
        string inputPath = Path.GetFullPath(args[1]);
        using var stream = File.OpenRead(inputPath);
        if (stream.Length > 1_048_576)
        {
            throw new ArgumentException("Input JSON must not exceed 1 MiB.");
        }

        var problem = JsonSerializer.Deserialize<RankingProblem>(stream, JsonOptions)
            ?? throw new ArgumentException("Input must be a ranking problem, not null.");
        var result = new HodgeRankSolver().Solve(problem);
        var files = new List<OutputFile>();
        if (options.TryGetValue("--json", out string? jsonPath))
        {
            files.Add(new OutputFile(jsonPath, JsonSerializer.Serialize(result, JsonOptions)));
        }
        if (options.TryGetValue("--svg", out string? svgPath))
        {
            files.Add(new OutputFile(svgPath, SvgReport.Render(problem, result)));
        }

        SaveOutputs(files, options.ContainsKey("--force"), inputPath);
        PrintSummary(result, output);
        foreach (var file in files)
        {
            output.WriteLine($"Written: {Path.GetFullPath(file.Path)}");
        }
    }

    private static void Demo(string[] args, TextWriter output)
    {
        var options = ParseOptions(args, 1, ["--output"]);
        string directory = options.GetValueOrDefault("--output", Path.Combine("artifacts", "demo"));
        var cases = new[]
        {
            (Name: "consistent", Problem: Triangle(1, 1, 2)),
            (Name: "rock-paper-scissors", Problem: Triangle(1, 1, -1)),
        };
        var results = cases.Select(item => (item.Name, item.Problem,
            Result: new HodgeRankSolver().Solve(item.Problem))).ToArray();
        var files = new List<OutputFile>();
        foreach (var item in results)
        {
            files.Add(new OutputFile(Path.Combine(directory, item.Name + ".input.json"),
                JsonSerializer.Serialize(item.Problem, JsonOptions)));
            files.Add(new OutputFile(Path.Combine(directory, item.Name + ".result.json"),
                JsonSerializer.Serialize(item.Result, JsonOptions)));
            files.Add(new OutputFile(Path.Combine(directory, item.Name + ".svg"),
                SvgReport.Render(item.Problem, item.Result)));
        }

        // Check every destination before writing any demo files.
        SaveOutputs(files, options.ContainsKey("--force"));
        foreach (var item in results)
        {
            output.WriteLine(item.Name);
            PrintSummary(item.Result, output);
            output.WriteLine();
        }

        output.WriteLine($"Written {files.Count} files to {Path.GetFullPath(directory)}");
    }

    private static Dictionary<string, string> ParseOptions(string[] args, int start, string[] allowed)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = start; index < args.Length; index++)
        {
            string option = args[index];
            if (result.ContainsKey(option))
            {
                throw new ArgumentException($"Duplicate option '{option}'.");
            }
            if (option == "--force")
            {
                result.Add(option, "");
                continue;
            }
            if (!allowed.Contains(option, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Unknown option '{option}'. Use --help for usage.");
            }
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '{option}' requires a value.");
            }

            result.Add(option, args[index]);
        }

        return result;
    }

    private static void SaveOutputs(List<OutputFile> files, bool force, string? inputPath = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var paths = new HashSet<string>(comparer);
        foreach (var file in files)
        {
            string path = Path.GetFullPath(file.Path);
            if (comparer.Equals(path, inputPath) || !paths.Add(path))
            {
                throw new ArgumentException("Input, JSON output, and SVG output must have distinct paths.");
            }
            if (Directory.Exists(path))
            {
                throw new IOException($"Output path is a directory: {path}");
            }
            if (!force && File.Exists(path))
            {
                throw new IOException($"Output file already exists: {path}. Use --force to replace it.");
            }
        }

        foreach (var file in files)
        {
            string path = Path.GetFullPath(file.Path);
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, file.Content + Environment.NewLine, new UTF8Encoding(false));
                File.Move(temporary, path, force);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    private static void PrintSummary(RankingResult result, TextWriter output)
    {
        var culture = CultureInfo.InvariantCulture;
        double flowScale = result.Edges.Max(edge => Math.Abs(edge.Observed));
        output.WriteLine("Scores (higher is preferred; equal scores are ties):");
        foreach (var item in result.Scores.OrderByDescending(item => item.Score))
        {
            string score = item.Score != 0 && Math.Abs(item.Score) <= flowScale * 1e-10
                ? "approximately 0" : item.Score.ToString("G9", culture);
            output.WriteLine($"  {JsonSerializer.Serialize(item.Object),-20} {score}");
        }

        var metrics = result.Metrics;
        output.WriteLine(FormattableString.Invariant(
            $"Comparisons: {metrics.ComparisonCount}; independent cycles: {metrics.IndependentCycleCount}"));
        output.WriteLine(FormattableString.Invariant(
            $"Least-squares error: {metrics.LeastSquaresError:G6}; RMSE: {metrics.RootMeanSquaredError:G6}"));
        output.WriteLine(FormattableString.Invariant(
            $"Cycle inconsistency: {100 * metrics.CycleInconsistency:0.######}%"));
        output.WriteLine(FormattableString.Invariant(
            $"Laplacian residual: {metrics.LaplacianResidual:G6}; relative: {metrics.RelativeLaplacianResidual:G6}"));
        output.WriteLine(FormattableString.Invariant($"Calculation time: {metrics.RuntimeMilliseconds:G6} ms"));
        if (!metrics.HasPreferenceSignal)
        {
            output.WriteLine("No preference signal: all strengths are zero; inconsistency is defined as zero.");
        }
        if (metrics.IndependentCycleCount == 0)
        {
            output.WriteLine("Tree graph: every observation can fit exactly; no cycles are available to check consistency.");
        }
    }

    private static RankingProblem Triangle(double ab, double bc, double ac) =>
        new(["A", "B", "C"], [new("A", "B", ab), new("B", "C", bc), new("A", "C", ac)]);

    private sealed record OutputFile(string Path, string Content);
}
