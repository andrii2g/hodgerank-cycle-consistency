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
