namespace Pigmentor.Retrieval;

/// <summary>
/// Configuration options for hybrid retrieval combining dense and sparse retriever legs.
/// </summary>
public sealed record HybridRetrievalOptions
{
    private int _candidatePoolPerLeg = 50;
    private int _rrfK = 60;
    private double _denseWeight = 1.0;
    private double _sparseWeight = 1.0;
    private TimeSpan _legTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Candidate pool size requested from each leg before reciprocal rank fusion.
    /// Default is 50. Standard industry practice is 50–100 per leg for high-consensus fusion.
    /// </summary>
    public int CandidatePoolPerLeg
    {
        get => _candidatePoolPerLeg;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "CandidatePoolPerLeg must be positive.");
            }
            _candidatePoolPerLeg = value;
        }
    }

    /// <summary>
    /// Smoothing constant (k) in RRF formula: 1 / (k + rank). Default is 60.
    /// </summary>
    public int RrfK
    {
        get => _rrfK;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "RrfK must be positive.");
            }
            _rrfK = value;
        }
    }

    /// <summary>
    /// Weight applied to dense retrieval ranks in weighted RRF. Default is 1.0.
    /// </summary>
    public double DenseWeight
    {
        get => _denseWeight;
        init
        {
            if (value < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "DenseWeight must be non-negative.");
            }
            _denseWeight = value;
        }
    }

    /// <summary>
    /// Weight applied to sparse retrieval ranks in weighted RRF. Default is 1.0.
    /// </summary>
    public double SparseWeight
    {
        get => _sparseWeight;
        init
        {
            if (value < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "SparseWeight must be non-negative.");
            }
            _sparseWeight = value;
        }
    }

    /// <summary>
    /// Maximum time allowed for retrieval legs to complete before timing out.
    /// Default is 10 seconds.
    /// </summary>
    public TimeSpan LegTimeout
    {
        get => _legTimeout;
        init
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "LegTimeout must be positive.");
            }
            _legTimeout = value;
        }
    }
}
