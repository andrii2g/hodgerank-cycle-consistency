# HodgeRank: cycle consistency

Convert signed pairwise preferences into zero-mean global scores and measure the
part that cannot be explained by score differences. Supports connected graphs
with 3–20 objects and one observation per unordered pair.

## Build and test

Requires the .NET 11 SDK. Math.NET Numerics supplies the Cholesky factorization;
the implementation uses its managed provider and needs no native numerical library.

```sh
dotnet restore HodgeRank.slnx
dotnet test HodgeRank.slnx -c Release
```

## Run

```sh
dotnet run --project src/HodgeRank.Cli -c Release -- demo
dotnet run --project src/HodgeRank.Cli -c Release -- analyze examples/rock-paper-scissors.json --json artifacts/result.json --svg artifacts/report.svg
```

The demo writes both input JSON files, result JSON files, and SVG reports to
`artifacts/demo`. Open the SVG files in a browser to compare observed, consistent,
and residual flows using identical node positions. Arrow direction follows the
sign of the flow; observed arrows point from preferred to less preferred objects.
Dense graphs hide edge labels; hover for exact values. Reports contain no scripts
or external assets. A large residual does not identify a uniquely wrong edge.

Use `--output <directory>` with `demo` to change its destination. Existing output
files require `--force`; input and output paths must remain distinct. Analysis
without output options prints scores and metrics without creating files. Run
`--help` for usage. Exit codes: 0 success/help, 2 invalid arguments or input data,
1 file/access error. Output files are replaced individually, not as a transaction;
an I/O failure can leave a partially written collection of reports.

JSON property names are case-sensitive camelCase. Unknown, missing, and duplicate
properties are rejected. Input files are limited to 1 MiB. Object names are
case-sensitive and must be unique and nonblank. Reversed duplicate comparisons
are rejected too. The examples also include a transitive graph with inconsistent
strengths and a tree containing an observed tie.

| Example | Scores A, B, C | Squared error | Cyclic energy |
| --- | --- | --- | --- |
| `consistent.json` | 1, 0, -1 | approximately 0 | approximately 0% |
| `rock-paper-scissors.json` | 0, 0, 0 | 3 | 100% |
| `transitive-inconsistent.json` | 2/3, 0, -2/3 | 1/3 | 11.11% |

Result JSON includes scores in input order, edge observations/fits/residuals, the
incidence matrix, graph Laplacian, and metrics. Equal scores represent ties; the
console sorts scores without assigning artificial ordinal positions. Very small
scores relative to observed strengths are displayed as approximately zero; JSON
retains full precision.

## Mathematical convention

`(A, B, +2)` means A is preferred to B by 2, modeled as `score(A) - score(B) = 2`.
Each row of the incidence matrix B has +1 at the first object and -1 at the second.
Given strengths y, solve `min ||B s - y||²` with `sum(s) = 0`.

The library constructs `L = BᵀB` and solves `(L + 11ᵀ/n)s = Bᵀy` using Cholesky.
The added term fixes the constant direction; it does not regularize the ranking.
The consistent component is `g = Bs`, and the cyclic residual is `r = y - g`.
All observations have equal importance; strengths are not confidence weights.

The residual measures **additive inconsistency**, which need not imply a directed
preference cycle. A tree always fits perfectly because it has no cycles to check.
A zero-strength comparison is an observed tie, not a missing edge. This first
release combines all cyclic flow; it does not split local curl from harmonic flow.

Reference: Jiang, Lim, Yao, and Ye,
[Statistical ranking and combinatorial Hodge theory](https://doi.org/10.1007/s10107-010-0419-x).
