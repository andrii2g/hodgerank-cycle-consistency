using System.Globalization;
using System.Text;
using System.Xml.Linq;
using HodgeRank.Core;

namespace HodgeRank.Cli;

internal static class SvgReport
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const double PanelWidth = 448;
    private const double PanelTop = 218;
    private const double PanelHeight = 542;

    public static string Render(RankingProblem problem, RankingResult result)
    {
        var root = Element("svg",
            new XAttribute("width", 1440),
            new XAttribute("height", 950),
            new XAttribute("viewBox", "0 0 1440 950"),
            new XAttribute("role", "img"),
            new XAttribute("aria-labelledby", "report-title report-description"),
            Element("title", new XAttribute("id", "report-title"), "HodgeRank: ranking and cycle consistency"),
            Element("desc", new XAttribute("id", "report-description"),
                "Three graphs use identical node positions to compare observed preferences, globally consistent score differences, and the remaining cyclic flow. " +
                "Arrows follow the sign of each flow. Scores are shown on the nodes; hover over an edge or node for its full value."),
            Element("style", """
                text { font-family: 'Segoe UI', Arial, sans-serif; fill: #172033; }
                .muted { fill: #56647a; }
                .card-label { font-size: 12px; letter-spacing: .6px; }
                .metric { font-size: 25px; font-weight: 650; }
                .node-name { font-size: 12px; font-weight: 650; }
                .node-score { font-variant-numeric: tabular-nums; }
                .edge-label { font-size: 11px; paint-order: stroke; stroke: white; stroke-width: 4px; stroke-linejoin: round; }
                .edge:hover line { opacity: 1; stroke-width: 4; }
                """),
            Element("rect", new XAttribute("width", 1440), new XAttribute("height", 950), new XAttribute("fill", "#f3f6fb")));

        root.Add(Text(36, 52, "HodgeRank", 32, "font-weight", "700"));
        root.Add(Text(225, 52, "Ranking & cycle consistency", 25, "fill", "#56647a"));
        root.Add(Text(36, 81, "Pairwise observations = consistent score differences + cyclic residual", 16, "class", "muted"));

        var metrics = result.Metrics;
        var metricCards = new (string Label, string Value, string Note)[]
        {
            ("CYCLE INCONSISTENCY", (100 * metrics.CycleInconsistency).ToString("0.##", Invariant) + "%",
                metrics.HasPreferenceSignal ? "fraction of observed flow energy" : "no preference signal"),
            ("LEAST-SQUARES ERROR", Number(metrics.LeastSquaresError), "sum of squared residuals"),
            ("INDEPENDENT CYCLES", metrics.IndependentCycleCount.ToString(Invariant), "m \u2212 n + 1"),
            ("COMPARISONS", metrics.ComparisonCount.ToString(Invariant), $"{metrics.ObjectCount} objects \u00b7 connected graph"),
            ("LAPLACIAN RESIDUAL", Number(metrics.LaplacianResidual), "numerical solve accuracy"),
            ("CALCULATION TIME", Number(metrics.RuntimeMilliseconds) + " ms", "excludes report and file output")
        };

        for (var index = 0; index < metricCards.Length; index++)
        {
            var x = 36 + index * 230;
            var card = metricCards[index];
            root.Add(Rect(x, 108, 218, 86, "white", "#e1e7f0", 12));
            root.Add(Text(x + 14, 129, card.Label, 12, "class", "card-label muted"));
            root.Add(Text(x + 14, 159, card.Value, 25, "class", "metric"));
            root.Add(Text(x + 14, 179, card.Note, 11, "class", "muted"));
        }

        var maxFlow = result.Edges.SelectMany(edge => new[]
        {
            Math.Abs(edge.Observed), Math.Abs(edge.Consistent), Math.Abs(edge.Residual)
        }).DefaultIfEmpty(0).Max();
        var showLabels = result.Edges.Length <= 18;
        AddPanel(root, problem, result, 36, "Observed preferences", "The strengths supplied in the input", "#2563eb", "observed", edge => edge.Observed, maxFlow, showLabels);
        AddPanel(root, problem, result, 496, "Consistent component", "Differences between the global scores: Bs", "#0d9488", "consistent", edge => edge.Consistent, maxFlow, showLabels);
        AddPanel(root, problem, result, 956, "Cyclic residual", "What score differences cannot explain: y \u2212 Bs", "#d65d4f", "residual", edge => edge.Residual, maxFlow, showLabels);

        var notes = new List<string>
        {
            "Arrows follow flow signs; observed arrows point from preferred to less preferred. Edge labels show magnitudes.",
            "Nodes show zero-mean global scores. Line widths share one scale across all panels; hover for full labels and signed values.",
            "A residual measures disagreement with score differences; it does not identify a uniquely wrong comparison."
        };
        notes.Add(showLabels
            ? "Very small flows relative to the report scale appear as dashed zero flows; their full values remain available on hover."
            : "Dense graph: edge labels are hidden to reduce overlap. Hover over an edge to inspect its signed numerical value.");
        if (!metrics.HasPreferenceSignal)
        {
            notes.Add("No preference signal: all observed strengths are zero. The inconsistency fraction is defined as zero by convention.");
        }
        else if (metrics.IndependentCycleCount == 0)
        {
            notes.Add("Tree graph: every set of edge strengths can fit exactly. There are no cycles available to test consistency.");
        }
        else
        {
            notes.Add("Inconsistency concerns additive strengths; it may occur even when the preference directions are transitive.");
        }

        root.Add(Rect(36, 780, 1368, 140, "#e9eff8", "#dce5f1", 12));
        for (var index = 0; index < notes.Count; index++)
        {
            root.Add(Text(54, 805 + index * 23, notes[index], 13, "class", "muted"));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
    }

    private static void AddPanel(
        XElement root,
        RankingProblem problem,
        RankingResult result,
        double left,
        string heading,
        string subtitle,
        string color,
        string markerId,
        Func<EdgeFlow, double> selectFlow,
        double maxFlow,
        bool showLabels)
    {
        root.Add(Rect(left, PanelTop, PanelWidth, PanelHeight, "white", "#dfe6f0", 16));
        root.Add(Element("rect", new XAttribute("x", left + 18), new XAttribute("y", PanelTop + 24),
            new XAttribute("width", 5), new XAttribute("height", 24), new XAttribute("rx", 2), new XAttribute("fill", color)));
        root.Add(Text(left + 34, PanelTop + 43, heading, 22, "font-weight", "650"));
        root.Add(Text(left + 22, PanelTop + 69, subtitle, 12, "class", "muted"));
        root.Add(Element("defs", Element("marker",
            new XAttribute("id", markerId), new XAttribute("viewBox", "0 0 10 10"),
            new XAttribute("refX", 9), new XAttribute("refY", 5),
            new XAttribute("markerWidth", 7), new XAttribute("markerHeight", 7),
            new XAttribute("markerUnits", "userSpaceOnUse"), new XAttribute("orient", "auto"),
            Element("path", new XAttribute("d", "M 0 0 L 10 5 L 0 10 z"), new XAttribute("fill", color)))));

        var count = problem.Objects.Length;
        var nodeWidth = count <= 8 ? 76.0 : count <= 12 ? 60.0 : 42.0;
        var nodeHeight = count <= 12 ? 42.0 : 34.0;
        var positions = problem.Objects.Select((name, index) =>
        {
            var angle = -Math.PI / 2 + 2 * Math.PI * index / count;
            return (Name: name, X: left + PanelWidth / 2 + 158 * Math.Cos(angle), Y: PanelTop + 292 + 158 * Math.Sin(angle));
        }).ToDictionary(point => point.Name, StringComparer.Ordinal);

        foreach (var edge in result.Edges)
        {
            var value = selectFlow(edge);
            var magnitude = Math.Abs(value);
            var relativeMagnitude = maxFlow > 0 ? magnitude / maxFlow : 0;
            var isVisibleFlow = relativeMagnitude >= 1e-10;
            var first = positions[value < 0 ? edge.Second : edge.First];
            var second = positions[value < 0 ? edge.First : edge.Second];
            var dx = second.X - first.X;
            var dy = second.Y - first.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            var ux = dx / length;
            var uy = dy / length;
            var trim = Math.Min(
                Math.Abs(ux) > 1e-15 ? (nodeWidth / 2 + 2) / Math.Abs(ux) : double.PositiveInfinity,
                Math.Abs(uy) > 1e-15 ? (nodeHeight / 2 + 2) / Math.Abs(uy) : double.PositiveInfinity);
            trim = Math.Min(trim, .45 * length);
            var line = Element("line",
                Attribute("x1", first.X + ux * trim), Attribute("y1", first.Y + uy * trim),
                Attribute("x2", second.X - ux * trim), Attribute("y2", second.Y - uy * trim),
                new XAttribute("stroke", isVisibleFlow ? color : "#b7c4d5"),
                Attribute("stroke-width", isVisibleFlow ? 1.2 + 3.2 * Math.Sqrt(relativeMagnitude) : 1),
                Attribute("opacity", isVisibleFlow ? showLabels ? .76 : .28 : .35));
            if (isVisibleFlow)
            {
                line.Add(new XAttribute("marker-end", $"url(#{markerId})"));
            }
            else
            {
                line.Add(new XAttribute("stroke-dasharray", "3 5"));
            }

            var group = Element("g", new XAttribute("class", "edge"),
                Element("title", $"{edge.First} minus {edge.Second}: {value.ToString("G17", Invariant)}" +
                    (value == 0 ? " (tie / zero flow)" : $" ({first.Name} \u2192 {second.Name})")), line);
            if (showLabels)
            {
                var x = (first.X + second.X) / 2;
                var y = (first.Y + second.Y) / 2 - 5;
                var label = isVisibleFlow ? Number(magnitude) : value == 0 ? "0" : "\u22480";
                group.Add(Text(x, y, label, 11, "class", "edge-label", "text-anchor", "middle", "fill", isVisibleFlow ? color : "#8290a5"));
            }
            root.Add(group);
        }

        var scores = result.Scores.ToDictionary(item => item.Object, item => item.Score, StringComparer.Ordinal);
        var maxLabelLength = count <= 8 ? 11 : count <= 12 ? 8 : 6;
        foreach (var name in problem.Objects)
        {
            var point = positions[name];
            var score = scores[name];
            var textElements = new StringInfo(name);
            var label = textElements.LengthInTextElements <= maxLabelLength
                ? name
                : textElements.SubstringByTextElements(0, maxLabelLength - 1) + "\u2026";
            var relativeScore = maxFlow > 0 ? Math.Abs(score) / maxFlow : 0;
            var scoreLabel = score != 0 && relativeScore < 1e-10 ? "\u22480" : Number(score, count > 12);
            var scoreText = Text(point.X, point.Y + 12, scoreLabel, count <= 12 ? 11 : 9,
                "class", "node-score", "text-anchor", "middle", "fill", color);
            if (count > 12 && scoreLabel.Length >= 8)
            {
                scoreText.Add(new XAttribute("textLength", 34), new XAttribute("lengthAdjust", "spacingAndGlyphs"));
            }
            root.Add(Element("g", Element("title", $"{name}: score {score.ToString("G17", Invariant)}"),
                Rect(point.X - nodeWidth / 2, point.Y - nodeHeight / 2, nodeWidth, nodeHeight, "#ffffff", "#becbdd", 10),
                Text(point.X, point.Y - 3, label, 12, "class", "node-name", "text-anchor", "middle"),
                scoreText));
        }
        root.Add(Text(left + PanelWidth / 2, PanelTop + PanelHeight - 21,
            showLabels ? "Edge magnitude \u00b7 node score" : "Hover edges for values \u00b7 node score", 12,
            "class", "muted", "text-anchor", "middle"));
    }

    private static string Number(double value, bool compact = false)
    {
        if (value == 0)
        {
            return "0";
        }
        var magnitude = Math.Abs(value);
        var format = magnitude >= 100_000 || magnitude < .001
            ? compact ? "0.#E+0" : "0.###E+0"
            : "0.####";
        return value.ToString(format, Invariant).Replace('-', '\u2212');
    }

    private static XElement Element(string name, params object[] contents) =>
        new(Svg + name, contents.Select(content => content is string text ? XmlText(text) : content));

    private static string XmlText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            var valid = value is 9 or 10 or 13 || value is >= 0x20 and <= 0xd7ff ||
                value is >= 0xe000 and <= 0xfffd || value is >= 0x10000 and <= 0x10ffff;
            builder.Append(valid ? rune.ToString() : "\ufffd");
        }
        return builder.ToString();
    }

    private static XAttribute Attribute(string name, double value) => new(name, value.ToString("0.###", Invariant));

    private static XElement Rect(double x, double y, double width, double height, string fill, string stroke, double radius) =>
        Element("rect", Attribute("x", x), Attribute("y", y), Attribute("width", width), Attribute("height", height),
            Attribute("rx", radius), new XAttribute("fill", fill), new XAttribute("stroke", stroke));

    private static XElement Text(double x, double y, string text, int size, params string[] attributes)
    {
        var element = Element("text", Attribute("x", x), Attribute("y", y), new XAttribute("font-size", size));
        for (var index = 0; index < attributes.Length; index += 2)
        {
            element.Add(new XAttribute(attributes[index], attributes[index + 1]));
        }
        element.Add(XmlText(text));
        return element;
    }
}
