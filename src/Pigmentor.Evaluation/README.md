# Pigmentor.Evaluation

The evaluation, regression testing, and quality gating subsystem for the **Pigmentor** enterprise RAG platform.

`Pigmentor.Evaluation` provides deterministic, reproducible offline and online quality measurement across the entire RAG lifecycle:

```text
Source document
      ↓
Extraction & Normalization
      ↓
Persistent Ingestion (CAS)
      ↓
SQLite + sqlite-vec
      ↓
Hybrid Retrieval & Re-ranking
      ↓
Context Construction & Budget Guard
      ↓
Grounded Generation
      ↓
Citation Resolution
      ↓
Pigmentor.Evaluation & Quality Gates
```

---

## Key Metrics

### 1. Retrieval Metrics
- **HitRate@K**: Fraction of evaluation queries where at least one ground-truth relevant document was retrieved within top-$K$ candidates ($K=5$).
- **Recall@K**: Proportion of all relevant documents identified within the top-$K$ retrieved pool.
- **MRR (Mean Reciprocal Rank)**: Average reciprocal rank of the first relevant document retrieved.

### 2. Generation & Grounding Metrics
- **Expected Fact Coverage**: Fraction of required factual assertions present in the synthesized answer (matched using whitespace-normalized tokenization).
- **Technical Term Preservation**: Strict, case-sensitive verification of critical technical terms (e.g. `RS-485`, `115200`, `120 Ω`, `40001`, `INT16`, `IP65`). Casing or spelling errors (e.g. `rs-485 != RS-485`) fail this metric.
- **Faithfulness Score**: Measures alignment between generated claims and retrieved source context, heavily penalizing unsupported assertions.
- **Insufficient Context Accuracy**: Ensures queries lacking supporting documents are acknowledged with an explicit insufficient-context response rather than hallucinated answers.

### 3. Citation Metrics
- **Citation Validity**: Proportion of citations (`[S1]`, `[S2]`) that resolve to real sources in the retrieved context. Hallucinated or dangling citations (e.g. `[S99]`) fail validity.
- **Citation Coverage**: Proportion of factual answers supported by valid citation tags.

---

## Centralized Quality Gate & Default Thresholds

Quality gates enforce non-negotiable production readiness criteria:

| Metric | Target Threshold | Rationale |
| :--- | :--- | :--- |
| **HitRate@5** | $\ge 0.90$ | High retrieval recall for industrial queries |
| **Recall@5** | $\ge 0.85$ | Multi-document coverage |
| **MRR** | $\ge 0.75$ | Most relevant context placed in top-1 or top-2 |
| **Expected Fact Coverage** | $\ge 0.90$ | Complete answers to technical questions |
| **Technical Term Preservation** | $= 1.00$ | Zero tolerance for mutilated register names or specs |
| **Citation Validity** | $= 1.00$ | Zero tolerance for hallucinated citation references |
| **Citation Coverage** | $\ge 0.80$ | High accountability and provenance |

---

## Usage Example

```csharp
using Pigmentor.Evaluation.Datasets;
using Pigmentor.Evaluation.Quality;
using Pigmentor.Evaluation.Runner;

// Evaluate RAG pipeline against deterministic test cases
var cases = V1EvaluationDataset.GetCases();
var result = await RagEvaluationRunner.EvaluateAsync(
    pipeline: ragPipeline,
    cases: cases,
    k: 5,
    thresholds: EvaluationThresholds.Default);

// Print human-readable summary
Console.WriteLine(result.ToSummaryString());

// Export machine-readable JSON for CI/CD gates
File.WriteAllText("eval-report.json", result.ToJson());

if (!result.QualityGatePassed)
{
    foreach (var failure in result.QualityGateFailures!)
    {
        Console.Error.WriteLine($"[GATE FAILURE] {failure}");
    }
    Environment.Exit(1);
}
```

---

## Deterministic vs. LLM-as-a-Judge

- **Deterministic Evaluation (Default in CI/CD)**: Zero network calls, microsecond runtime, 100% reproducible test suite using exact term matching, lexical fact coverage, and citation graph validation.
- **LLM-as-a-Judge (`GenerationEvaluator`)**: Optional semantic judge for open-ended QA assessment via `IChatClient` (Ollama, GigaChat, etc.), suitable for pre-release exploratory evaluations.
