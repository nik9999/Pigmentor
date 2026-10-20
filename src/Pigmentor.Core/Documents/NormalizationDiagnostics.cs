namespace Pigmentor.Core.Documents;

/// <summary>
/// Diagnostic telemetry and counters gathered during the document normalization stage.
/// </summary>
public sealed class NormalizationDiagnostics
{
    /// <summary>Total number of physical or logical pages extracted from the document.</summary>
    public int ExtractedPages { get; set; }

    /// <summary>Number of pages dropped because they contained solely whitespace, noise, or decorative elements.</summary>
    public int DroppedEmptyPages { get; set; }

    /// <summary>Number of blocks dropped due to zero length or pure whitespace/noise.</summary>
    public int DroppedEmptyBlocks { get; set; }

    /// <summary>Number of repeated running headers detected and removed document-wide.</summary>
    public int RemovedHeaders { get; set; }

    /// <summary>Number of repeated running footers detected and removed document-wide.</summary>
    public int RemovedFooters { get; set; }

    /// <summary>Number of layout page numbers stripped from searchable prose.</summary>
    public int RemovedPageNumbers { get; set; }

    /// <summary>Number of line-break word splits repaired by conservative dehyphenation.</summary>
    public int DehyphenationRepairs { get; set; }

    /// <summary>Number of exact or near-duplicate blocks removed.</summary>
    public int DuplicateBlocksRemoved { get; set; }

    /// <summary>Number of images or figures submitted to OCR processing.</summary>
    public int OcrProcessedImages { get; set; }

    /// <summary>Number of OCR-processed images rejected by the quality gate.</summary>
    public int OcrRejectedImages { get; set; }

    /// <summary>Number of structured tables extracted.</summary>
    public int TablesExtracted { get; set; }

    /// <summary>Number of non-decorative figures extracted.</summary>
    public int FiguresExtracted { get; set; }
}

