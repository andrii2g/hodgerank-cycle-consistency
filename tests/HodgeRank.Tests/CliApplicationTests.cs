using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using HodgeRank.Cli;
using HodgeRank.Core;
using Xunit;

namespace HodgeRank.Tests;

public sealed class CliApplicationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly XNamespace SvgNamespace = "http://www.w3.org/2000/svg";

    [Theory]
    [InlineData()]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpSucceedsWithoutWritingErrors(params string[] args)
    {
        var result = Run(args);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("analyze", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("demo", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Error);
    }

    [Fact]
    public void AnalyzeWritesScoresResidualsAndAnSvgReport()
    {
        using var directory = new TemporaryDirectory();
        var input = WriteInput(directory, Triangle(1, 1, -1));
        var json = directory.File("result.json");
        var svg = directory.File("report.svg");

        var run = Run("analyze", input, "--json", json, "--svg", svg);

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Error);
        Assert.Contains("%", run.Output, StringComparison.Ordinal);
        var result = ReadResult(json);
        Assert.Equal(new[] { "A", "B", "C" }, result.Scores.Select(score => score.Object));
        Assert.All(result.Scores, score => Assert.InRange(Math.Abs(score.Score), 0, 1e-10));
        Assert.Equal(3, result.Metrics.ComparisonCount);
        Assert.Equal(1, result.Metrics.CycleInconsistency, 10);
        Assert.Equal(3, result.Metrics.LeastSquaresError, 10);
        Assert.All(result.Edges, edge => Assert.Equal(edge.Observed, edge.Consistent + edge.Residual, 10));
        AssertReport(XDocument.Load(svg), 3);
    }

    [Fact]
    public void AnalyzeCanPrintASummaryWithoutCreatingOutputFiles()
    {
        using var directory = new TemporaryDirectory();
        var input = WriteInput(directory, Triangle(1, 1, 2));

        var run = Run("analyze", input);

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Error);
        Assert.Contains("A", run.Output, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void TreeSummaryExplainsTheLackOfCycleEvidence()
    {
        using var directory = new TemporaryDirectory();
        var input = WriteInput(directory, new RankingProblem(
            ["A", "B", "C"], [new("A", "B", 1), new("B", "C", 2)]));

        var run = Run("analyze", input);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("tree", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cycle", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZeroSignalSummaryExplainsTheZeroInconsistencyConvention()
    {
        using var directory = new TemporaryDirectory();
        var input = WriteInput(directory, Triangle(0, 0, 0));

        var run = Run("analyze", input);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("no preference signal", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DemoCreatesBothDocumentedExamplesAndAllSixFiles()
    {
        using var directory = new TemporaryDirectory();

        var run = Run("demo", "--output", directory.Path);

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Error);
        Assert.Equal(6, Directory.GetFiles(directory.Path).Length);
        foreach (var name in new[] { "consistent", "rock-paper-scissors" })
        {
            var input = JsonSerializer.Deserialize<RankingProblem>(File.ReadAllText(directory.File(name + ".input.json")), JsonOptions);
            Assert.NotNull(input);
            Assert.Equal(3, input.Objects.Length);
            var result = ReadResult(directory.File(name + ".result.json"));
            Assert.Equal(name == "consistent" ? 0 : 1, result.Metrics.CycleInconsistency, 10);
            AssertReport(XDocument.Load(directory.File(name + ".svg")), 3);
        }
    }

    [Fact]
    public void ExistingDemoOutputIsRejectedBeforeWritingAnyOtherFiles()
    {
        using var directory = new TemporaryDirectory();
        var existing = directory.File("rock-paper-scissors.svg");
        File.WriteAllText(existing, "keep this report");

        var run = Run("demo", "--output", directory.Path);

        Assert.Equal(1, run.ExitCode);
        Assert.NotEmpty(run.Error);
        Assert.Equal("keep this report", File.ReadAllText(existing));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void ForceAllowsReplacingExistingDemoReports()
    {
        using var directory = new TemporaryDirectory();
        var existing = directory.File("rock-paper-scissors.svg");
        File.WriteAllText(existing, "old report");

        var run = Run("demo", "--output", directory.Path, "--force");

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Error);
        Assert.Equal(6, Directory.GetFiles(directory.Path).Length);
        AssertReport(XDocument.Load(existing), 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InputOutputCollisionIsRejectedEvenWithForce(bool force)
    {
        using var directory = new TemporaryDirectory();
        var input = WriteInput(directory, Triangle(1, 1, 2));
        var original = File.ReadAllText(input);
        var args = new List<string> { "analyze", input, "--json", System.IO.Path.Combine(directory.Path, ".", "input.json") };
        if (force)
        {
            args.Add("--force");
        }

        var run = Run(args.ToArray());

        Assert.Equal(2, run.ExitCode);
        Assert.NotEmpty(run.Error);
        Assert.Equal(original, File.ReadAllText(input));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonAndSvgCannotShareTheSameOutputPath(bool force)
    {
        using var directory = new TemporaryDirectory();
        var input = WriteInput(directory, Triangle(1, 1, 2));
        var output = directory.File("shared.output");
        var args = new List<string> { "analyze", input, "--json", output, "--svg", output };
        if (force)
        {
            args.Add("--force");
        }

        var run = Run(args.ToArray());

        Assert.Equal(2, run.ExitCode);
        Assert.NotEmpty(run.Error);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ExistingOutputIsPreservedUnlessForceIsExplicit()
    {
        using var directory = new TemporaryDirectory();
        var input = WriteInput(directory, Triangle(1, 1, 2));
        var output = directory.File("result.json");
        var svg = directory.File("report.svg");
        File.WriteAllText(output, "keep existing output");

        var rejected = Run("analyze", input, "--svg", svg, "--json", output);

        Assert.Equal(1, rejected.ExitCode);
        Assert.Equal("keep existing output", File.ReadAllText(output));
        Assert.False(File.Exists(svg));

        var accepted = Run("analyze", input, "--json", output, "--force");

        Assert.Equal(0, accepted.ExitCode);
        Assert.Equal(3, ReadResult(output).Scores.Length);
    }

    [Fact]
    public void DirectoryCannotBeUsedAsAnOutputFile()
    {
        using var directory = new TemporaryDirectory();
        var input = WriteInput(directory, Triangle(1, 1, 2));

        var run = Run("analyze", input, "--json", directory.Path, "--force");

        Assert.Equal(1, run.ExitCode);
        Assert.NotEmpty(run.Error);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void MissingInputProducesAnIoError()
    {
        using var directory = new TemporaryDirectory();

        var run = Run("analyze", directory.File("missing.json"));

        Assert.Equal(1, run.ExitCode);
        Assert.NotEmpty(run.Error);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    public static IEnumerable<object[]> InvalidAnalyzeOptions()
    {
        yield return [new[] { "--unknown" }];
        yield return [new[] { "--json" }];
        yield return [new[] { "--svg" }];
        yield return [new[] { "--json", "first.json", "--json", "second.json" }];
        yield return [new[] { "--force", "--force" }];
        yield return [new[] { "--json", "--svg", "report.svg" }];
        yield return [new[] { "extra-input.json" }];
        yield return [new[] { "--output", "directory" }];
    }

    [Theory]
    [MemberData(nameof(InvalidAnalyzeOptions))]
    public void InvalidOptionsProduceUsageErrorsBeforeWritingFiles(string[] options)
    {
        using var directory = new TemporaryDirectory();
        var input = WriteInput(directory, Triangle(1, 1, 2));

        var localOptions = options.Select(option => option.EndsWith(".json", StringComparison.Ordinal) ||
            option.EndsWith(".svg", StringComparison.Ordinal) ? directory.File(option) : option).ToArray();
        var run = Run(["analyze", input, .. localOptions]);

        Assert.Equal(2, run.ExitCode);
        Assert.NotEmpty(run.Error);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("analyze")]
    [InlineData("demo", "--output")]
    [InlineData("demo", "--json", "result.json")]
    public void InvalidCommandsProduceUsageErrors(params string[] args)
    {
        var run = Run(args);

        Assert.Equal(2, run.ExitCode);
        Assert.NotEmpty(run.Error);
    }

    public static IEnumerable<object[]> InvalidJsonInputs()
    {
        yield return ["{"];
        yield return ["null"];
        yield return ["{}"];
        yield return ["{\"objects\":[\"A\",\"B\",\"C\"]}"];
        yield return ["{\"Objects\":[\"A\",\"B\",\"C\"],\"comparisons\":[]}"];
        yield return ["{\"objects\":null,\"comparisons\":[]}"];
        yield return ["{\"objects\":[\"A\",\"B\",\"C\"],\"comparisons\":null}"];
        yield return ["{\"objects\":[\"A\",\"B\",\"C\"],\"comparisons\":[{\"first\":\"A\",\"second\":\"B\",\"strength\":1},{\"first\":\"B\",\"second\":\"C\",\"strength\":1}],\"unexpected\":true}"];
        yield return ["{\"objects\":[\"A\",\"B\",\"C\"],\"objects\":[\"A\",\"B\",\"C\"],\"comparisons\":[{\"first\":\"A\",\"second\":\"B\",\"strength\":1},{\"first\":\"B\",\"second\":\"C\",\"strength\":1}]}"];
        yield return ["{\"objects\":[\"A\",\"B\",\"C\"],\"comparisons\":[{\"first\":\"A\",\"second\":\"B\"},{\"first\":\"B\",\"second\":\"C\",\"strength\":1}]}"];
        yield return ["{\"objects\":[\"A\",\"B\",\"C\"],\"comparisons\":[{\"first\":\"A\",\"second\":\"B\",\"strength\":1,\"unexpected\":true},{\"first\":\"B\",\"second\":\"C\",\"strength\":1}]}"];
        yield return [JsonSerializer.Serialize(new RankingProblem(["A", "B", "C"], [new("A", "B", 1), new("B", "A", -1)]), JsonOptions)];
        yield return [JsonSerializer.Serialize(new RankingProblem(["A", "B", "C", "D"], [new("A", "B", 1), new("B", "C", 1), new("A", "C", 2)]), JsonOptions)];
    }

    [Theory]
    [MemberData(nameof(InvalidJsonInputs))]
    public void InvalidJsonOrGraphProducesDataErrorsWithoutOutputFiles(string json)
    {
        using var directory = new TemporaryDirectory();
        var input = directory.File("input.json");
        var output = directory.File("result.json");
        File.WriteAllText(input, json);

        var run = Run("analyze", input, "--json", output);

        Assert.Equal(2, run.ExitCode);
        Assert.NotEmpty(run.Error);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void InputLargerThanOneMiBIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var input = directory.File("large.json");
        File.WriteAllText(input, new string(' ', 1024 * 1024 + 1));

        var run = Run("analyze", input);

        Assert.Equal(2, run.ExitCode);
        Assert.NotEmpty(run.Error);
    }

    [Fact]
    public void SvgEscapesLabelsAndSanitizesInvalidXmlCharacters()
    {
        const string scriptLabel = "<script>alert(\"x\")</script>& ' >";
        const string controlLabel = "Control\u0001\U0001F680";
        var problem = new RankingProblem(
            [scriptLabel, controlLabel, "Third"],
            [new(scriptLabel, controlLabel, 1), new(controlLabel, "Third", 1), new(scriptLabel, "Third", 2)]);
        var result = new HodgeRankSolver().Solve(problem);

        var svg = SvgReport.Render(problem, result);

        var document = XDocument.Parse(svg);
        AssertReport(document, 3);
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName.Equals("script", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(document.Descendants(SvgNamespace + "title"), title => title.Value.Contains(scriptLabel, StringComparison.Ordinal));
        Assert.Contains(document.Descendants(SvgNamespace + "title"), title => title.Value.Contains("Control\ufffd\U0001F680", StringComparison.Ordinal));
        Assert.DoesNotContain('\u0001', svg);
    }

    [Fact]
    public void OutputRemainsInvariantUnderFrenchCulture()
    {
        using var directory = new TemporaryDirectory();
        var input = WriteInput(directory, Triangle(1.5, 1.5, 3));
        var json = directory.File("result.json");
        var svg = directory.File("report.svg");
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

            var run = Run("analyze", input, "--json", json, "--svg", svg);

            Assert.Equal(0, run.ExitCode);
            Assert.Contains("1.5", run.Output, StringComparison.Ordinal);
            Assert.Equal(1.5, ReadResult(json).Scores[0].Score, 10);
            AssertReport(XDocument.Load(svg), 3);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void DenseTwentyObjectReportContainsFiniteGeometry()
    {
        var objects = Enumerable.Range(0, 20).Select(index => $"Item {index}").ToArray();
        var comparisons = new List<Comparison>();
        for (var first = 0; first < objects.Length; first++)
        {
            for (var second = first + 1; second < objects.Length; second++)
            {
                comparisons.Add(new Comparison(objects[first], objects[second], (first - second) / 3d));
            }
        }
        var problem = new RankingProblem(objects, comparisons.ToArray());
        var result = new HodgeRankSolver().Solve(problem);

        var svg = SvgReport.Render(problem, result);

        var document = XDocument.Parse(svg);
        AssertReport(document, 190);
        Assert.DoesNotContain("NaN", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", svg, StringComparison.Ordinal);
    }

    private static RankingProblem Triangle(double ab, double bc, double ac) =>
        new(["A", "B", "C"], [new("A", "B", ab), new("B", "C", bc), new("A", "C", ac)]);

    private static string WriteInput(TemporaryDirectory directory, RankingProblem problem)
    {
        var path = directory.File("input.json");
        File.WriteAllText(path, JsonSerializer.Serialize(problem, JsonOptions));
        return path;
    }

    private static RankingResult ReadResult(string path) =>
        JsonSerializer.Deserialize<RankingResult>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException("Expected a JSON ranking result.");

    private static InvocationResult Run(params string[] args)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = CliApplication.Run(args, output, error);
        return new InvocationResult(exitCode, output.ToString(), error.ToString());
    }

    private static void AssertReport(XDocument document, int comparisonCount)
    {
        Assert.NotNull(document.Root);
        Assert.Equal(SvgNamespace + "svg", document.Root.Name);
        Assert.NotEmpty(document.Descendants(SvgNamespace + "title"));
        Assert.Equal(comparisonCount * 3, document.Descendants(SvgNamespace + "line").Count());
        string[] numericAttributes = ["x", "y", "width", "height", "x1", "x2", "y1", "y2", "rx", "stroke-width", "opacity", "font-size"];
        foreach (var attribute in document.Descendants().Attributes().Where(attribute => numericAttributes.Contains(attribute.Name.LocalName)))
        {
            Assert.True(double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value),
                $"Invalid SVG numeric attribute {attribute.Name}={attribute.Value}.");
            Assert.True(double.IsFinite(value), $"Nonfinite SVG numeric attribute {attribute.Name}.");
        }
    }

    private sealed record InvocationResult(int ExitCode, string Output, string Error);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HodgeRank.Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
