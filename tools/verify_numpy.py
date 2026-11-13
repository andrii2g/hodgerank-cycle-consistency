#!/usr/bin/env python3
"""Check the .NET CLI against independent NumPy least-squares calculations.

Build first with ``dotnet build -c Release``. NumPy is a development-only
dependency; install it with ``python -m pip install -r tools/requirements.txt``.
The verifier never installs dependencies or downloads anything itself.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
from pathlib import Path
import subprocess
import sys
import tempfile

try:
    import numpy as np
except ImportError as error:
    raise SystemExit(
        "NumPy is required. Run: python -m pip install -r tools/requirements.txt"
    ) from error


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CLI = (
    REPOSITORY_ROOT
    / "src/HodgeRank.Cli/bin/Release/net11.0/HodgeRank.Cli.dll"
)
TOLERANCE = 2e-9


class VerificationError(Exception):
    """A failed invocation, malformed result, or numerical disagreement."""


@dataclass(frozen=True)
class Case:
    name: str
    objects: list[str]
    comparisons: list[dict[str, str | float]]

    def as_input(self) -> dict:
        return {"objects": self.objects, "comparisons": self.comparisons}


def comparison(first: str, second: str, strength: float) -> dict:
    return {"first": first, "second": second, "strength": float(strength)}


def fixtures() -> list[Case]:
    def triangle(name: str, strengths: tuple[float, float, float]) -> Case:
        return Case(
            name,
            ["A", "B", "C"],
            [
                comparison("A", "B", strengths[0]),
                comparison("B", "C", strengths[1]),
                comparison("A", "C", strengths[2]),
            ],
        )

    return [
        triangle("consistent-triangle", (1, 1, 2)),
        triangle("rock-paper-scissors", (1, 1, -1)),
        triangle("all-zero-observations", (0, 0, 0)),
        triangle("transitive-but-additively-inconsistent", (1, 1, 1)),
        triangle("tiny-strengths", (1e-8, 1e-8, -1e-8)),
        triangle("large-strengths", (1e8, -2e8, 4e8)),
        triangle("very-small-consistent-strengths", (1e-100, 1e-100, 2e-100)),
        triangle("squared-error-underflow", (1e-200, 1e-200, -1e-200)),
        triangle("very-large-inconsistent-strengths", (1e100, -2e100, 4e100)),
        Case(
            "tree-with-negative-and-zero-strengths",
            ["A", "B", "C", "D", "E"],
            [
                comparison("A", "B", -3),
                comparison("B", "C", 0),
                comparison("D", "B", 2),
                comparison("D", "E", -1),
            ],
        ),
        Case(
            "triangle-free-four-cycle",
            ["A", "B", "C", "D"],
            [
                comparison("A", "B", 1),
                comparison("B", "C", 1),
                comparison("C", "D", 1),
                comparison("D", "A", 1),
            ],
        ),
        Case(
            "reversed-orientations",
            ["A", "B", "C"],
            [
                comparison("B", "A", -1),
                comparison("C", "B", -1),
                comparison("C", "A", -2),
            ],
        ),
    ]


def random_case(index: int, rng: np.random.Generator) -> Case:
    # The first 18 random cases cover every supported object count.
    n = 3 + index if index < 18 else int(rng.integers(3, 21))
    objects = [f"Object {number + 1}" for number in rng.permutation(n)]
    order = [int(value) for value in rng.permutation(n)]
    shape = index % 4

    if shape == 1:
        pairs = {(i, j) for i in range(n) for j in range(i + 1, n)}
    elif shape == 2:
        pairs = {
            tuple(sorted((order[i], order[(i + 1) % n]))) for i in range(n)
        }
    else:
        # A randomly relabeled spanning tree guarantees connectivity.
        pairs = {
            tuple(sorted((order[i], order[int(rng.integers(i))])))
            for i in range(1, n)
        }
        if shape == 3:
            density = float(rng.uniform(0.05, 0.6))
            pairs.update(
                (i, j)
                for i in range(n)
                for j in range(i + 1, n)
                if rng.random() < density
            )

    scale = (1e-8, 1e-3, 1.0, 1e3, 1e8)[index % 5]
    latent_scores = rng.normal(size=n) * scale
    comparisons = []
    for edge_index, (first, second) in enumerate(sorted(pairs)):
        if rng.random() < 0.5:
            first, second = second, first
        if index % 6 == 0:
            strength = latent_scores[first] - latent_scores[second]
        elif index % 11 == 0:
            strength = 0.0
        else:
            strength = 0.0 if edge_index % 7 == 0 else rng.normal() * scale
        comparisons.append(comparison(objects[first], objects[second], strength))

    return Case(f"random-{index + 1:03d}-n{n}", objects, comparisons)


def independent_reference(case: Case) -> dict:
    n, m = len(case.objects), len(case.comparisons)
    positions = {name: index for index, name in enumerate(case.objects)}
    incidence = np.zeros((m, n), dtype=np.float64)
    observations = np.empty(m, dtype=np.float64)
    for row, edge in enumerate(case.comparisons):
        incidence[row, positions[edge["first"]]] = 1.0
        incidence[row, positions[edge["second"]]] = -1.0
        observations[row] = edge["strength"]

    # B has rank n-1. lstsq supplies its minimum-norm, zero-mean solution.
    # NumPy's returned residuals array is empty for rank-deficient B, so
    # calculate the edge residuals explicitly rather than using that array.
    scores, _, rank, _ = np.linalg.lstsq(incidence, observations, rcond=None)
    if rank != n - 1:
        raise VerificationError(f"{case.name}: generator produced disconnected data")
    scores -= scores.mean()
    consistent = incidence @ scores
    residuals = observations - consistent
    scale = float(np.max(np.abs(observations)))
    divisor = scale if scale else 1.0
    normalized_residuals = residuals / divisor
    normalized_observations = observations / divisor
    residual_energy = float(normalized_residuals @ normalized_residuals)
    observed_energy = float(normalized_observations @ normalized_observations)
    residual_norm = np.sqrt(residual_energy) * divisor
    error = float(residual_norm * residual_norm)
    energy = float(observations @ observations)
    return {
        "incidence": incidence,
        "laplacian": incidence.T @ incidence,
        "observations": observations,
        "scores": scores,
        "consistent": consistent,
        "residuals": residuals,
        "error": error,
        "energy": energy,
        "rmse": np.sqrt(residual_energy / m) * divisor,
        "inconsistency": residual_energy / observed_energy if scale else 0.0,
        "has_signal": bool(scale),
    }


def numeric_array(value: object, shape: tuple[int, ...], label: str) -> np.ndarray:
    try:
        array = np.asarray(value, dtype=np.float64)
    except (TypeError, ValueError) as error:
        raise VerificationError(f"{label}: expected numeric values") from error
    if array.shape != shape:
        raise VerificationError(f"{label}: expected shape {shape}, got {array.shape}")
    if not np.isfinite(array).all():
        raise VerificationError(f"{label}: contains a nonfinite value")
    return array


def check_close(label: str, actual: object, expected: object, scale: float) -> None:
    expected_array = np.asarray(expected, dtype=np.float64)
    actual_array = numeric_array(actual, expected_array.shape, label)
    difference = float(np.max(np.abs(actual_array - expected_array)))
    magnitude = max(scale, float(np.max(np.abs(expected_array))))
    if difference > TOLERANCE * magnitude:
        raise VerificationError(
            f"{label}: maximum difference {difference:.6g} exceeds "
            f"tolerance {TOLERANCE * magnitude:.6g}"
        )


def verify_result(case: Case, result: dict, reference: dict) -> None:
    n, m = len(case.objects), len(case.comparisons)
    try:
        score_rows, edge_rows, metrics = result["scores"], result["edges"], result["metrics"]
        if [row["object"] for row in score_rows] != case.objects:
            raise VerificationError("scores: object names or input order changed")
        if len(edge_rows) != m:
            raise VerificationError(f"edges: expected {m} rows, got {len(edge_rows)}")
        for expected, actual in zip(case.comparisons, edge_rows):
            if (actual["first"], actual["second"]) != (
                expected["first"], expected["second"]
            ):
                raise VerificationError("edges: comparison orientation or order changed")

        observation_scale = float(np.max(np.abs(reference["observations"])))
        check_close("scores", [row["score"] for row in score_rows], reference["scores"], observation_scale)
        for output_key, reference_key in (
            ("observed", "observations"),
            ("consistent", "consistent"),
            ("residual", "residuals"),
        ):
            check_close(
                output_key,
                [row[output_key] for row in edge_rows],
                reference[reference_key],
                observation_scale,
            )

        for output_key, reference_key in (
            ("incidenceMatrix", "incidence"), ("laplacian", "laplacian")
        ):
            expected_matrix = reference[reference_key]
            actual_matrix = numeric_array(result[output_key], expected_matrix.shape, output_key)
            if not np.array_equal(actual_matrix, expected_matrix):
                raise VerificationError(f"{output_key}: differs from independently built matrix")

        for key, expected in (
            ("objectCount", n),
            ("comparisonCount", m),
            ("independentCycleCount", m - n + 1),
            ("hasPreferenceSignal", reference["has_signal"]),
        ):
            if type(metrics[key]) is not type(expected) or metrics[key] != expected:
                raise VerificationError(f"{key}: expected {expected}, got {metrics[key]}")
        for key in ("leastSquaresError", "rootMeanSquaredError", "laplacianResidual"):
            if numeric_array(metrics[key], (), key).item() < 0:
                raise VerificationError(f"{key}: must be nonnegative")
        if not 0 <= numeric_array(metrics["cycleInconsistency"], (), "cycleInconsistency").item() <= 1:
            raise VerificationError("cycleInconsistency: must be between zero and one")
        check_close("leastSquaresError", metrics["leastSquaresError"], reference["error"], reference["energy"])
        check_close("rootMeanSquaredError", metrics["rootMeanSquaredError"], reference["rmse"], observation_scale)
        check_close("cycleInconsistency", metrics["cycleInconsistency"], reference["inconsistency"], 1.0)

        scores = numeric_array([row["score"] for row in score_rows], (n,), "scores")
        residuals = numeric_array([row["residual"] for row in edge_rows], (m,), "residuals")
        check_close("zero mean", scores.mean(), 0.0, observation_scale)
        divisor = observation_scale if observation_scale else 1.0
        normalized_scores = scores / divisor
        normalized_residuals = residuals / divisor
        normalized_observations = reference["observations"] / divisor
        normalized_consistent = reference["incidence"] @ normalized_scores
        normalized_energy = float(normalized_observations @ normalized_observations)
        check_close("normal-equation residual", reference["incidence"].T @ normalized_residuals, np.zeros(n), float(m))
        check_close("orthogonality", normalized_consistent @ normalized_residuals, 0.0, normalized_energy)
        check_close("energy decomposition", normalized_consistent @ normalized_consistent + normalized_residuals @ normalized_residuals, normalized_energy, normalized_energy)

        laplacian = reference["laplacian"]
        right_hand_side = reference["incidence"].T @ normalized_observations
        normal_residual = float(np.linalg.norm(laplacian @ normalized_scores - right_hand_side))
        check_close("laplacianResidual", metrics["laplacianResidual"], normal_residual * divisor, m * observation_scale)
        relative_residual = normal_residual / max(1.0, float(np.linalg.norm(right_hand_side)))
        check_close("relativeLaplacianResidual", metrics["relativeLaplacianResidual"], relative_residual, 1.0)
        for key in ("relativeLaplacianResidual", "runtimeMilliseconds"):
            value = numeric_array(metrics[key], (), key).item()
            if value < 0:
                raise VerificationError(f"{key}: must be nonnegative")
        if metrics["relativeLaplacianResidual"] > TOLERANCE:
            raise VerificationError("relativeLaplacianResidual: solver has not converged")
    except (KeyError, TypeError) as error:
        raise VerificationError(f"result is missing or has malformed fields: {error}") from error


def invoke_cli(command: list[str], case: Case, directory: Path) -> dict:
    input_path, output_path = directory / "input.json", directory / "result.json"
    input_path.write_text(json.dumps(case.as_input(), ensure_ascii=False, allow_nan=False), encoding="utf-8")
    if output_path.exists():
        output_path.unlink()
    try:
        completed = subprocess.run(
            [*command, "analyze", str(input_path), "--json", str(output_path)],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=30,
            check=False,
        )
    except subprocess.TimeoutExpired as error:
        raise VerificationError("CLI exceeded its 30-second timeout") from error
    except OSError as error:
        raise VerificationError(f"could not start CLI: {error}") from error
    if completed.returncode:
        raise VerificationError(
            f"CLI exited {completed.returncode}\n"
            f"stdout: {completed.stdout.strip()}\nstderr: {completed.stderr.strip()}"
        )
    try:
        result = json.loads(output_path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as error:
        raise VerificationError(f"CLI did not produce readable result JSON: {error}") from error
    if not isinstance(result, dict):
        raise VerificationError("CLI result must be a JSON object")
    return result


def nonnegative_int(value: str) -> int:
    parsed = int(value)
    if parsed < 0:
        raise argparse.ArgumentTypeError("must be nonnegative")
    return parsed


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, default=DEFAULT_CLI, help="built CLI DLL or executable")
    parser.add_argument("--cases", type=nonnegative_int, default=100, help="random cases in addition to fixed fixtures (default: 100)")
    parser.add_argument("--seed", type=nonnegative_int, default=20261113, help="nonnegative random seed (default: 20261113)")
    arguments = parser.parse_args()
    cli = arguments.cli.resolve()
    if not cli.is_file():
        print(f"ERROR: CLI not found: {cli}\nBuild first: dotnet build -c Release", file=sys.stderr)
        return 1
    command = ["dotnet", str(cli)] if cli.suffix.lower() == ".dll" else [str(cli)]
    fixed_cases = fixtures()
    rng = np.random.default_rng(arguments.seed)
    cases = fixed_cases + [random_case(index, rng) for index in range(arguments.cases)]
    with tempfile.TemporaryDirectory(prefix="hodgerank-numpy-") as temporary:
        for case in cases:
            try:
                reference = independent_reference(case)
                result = invoke_cli(command, case, Path(temporary))
                verify_result(case, result, reference)
            except VerificationError as error:
                print(f"FAIL {case.name}: {error}", file=sys.stderr)
                return 1
    print(
        f"PASS: {len(cases)} cases ({len(fixed_cases)} fixed fixtures + "
        f"{arguments.cases} random), seed={arguments.seed}, NumPy={np.__version__}."
    )
    print("Verified scores, edge flows, matrices, error metrics, zero mean, and orthogonality.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
