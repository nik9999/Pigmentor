namespace Pigmentor.Core.Documents;

/// <summary>
/// Structured metadata that travels with every <see cref="Document"/> and
/// <see cref="DocumentChunk"/> through the pipeline. The schema matches the
/// YAML front-matter emitted by <c>tools/generate-dataset</c> and is the
/// foundation for Chapter 11 (Metadata Filtering and Query Construction).
/// </summary>
/// <param name="Id">Stable, dataset-unique identifier (e.g. <c>hr-001</c>).</param>
/// <param name="Silo">One of: hr-policies, technical-docs, financial-reports, legal-contracts, product-catalog, release-notes-tickets.</param>
/// <param name="Department">HR, Engineering, Finance, Legal, Product.</param>
/// <param name="Office">Montreal, Paris, Casablanca.</param>
/// <param name="ConfidentialityLevel">Public, Internal, Restricted, Confidential.</param>
/// <param name="DocumentType">Policy, ADR, Runbook, Reference, Report, Contract, Specification, ReleaseNote, Ticket.</param>
/// <param name="FiscalYear">Year of last revision; matches the document's content.</param>
/// <param name="Author">Free-text author name as it appears in front-matter.</param>
/// <param name="LastModified">Date of the last revision.</param>
/// <param name="Title">Human-readable title (matches the H1 in the document body).</param>
public sealed record DocumentMetadata(
    string Id,
    string Silo,
    string Department = "",
    string Office = "",
    string ConfidentialityLevel = "",
    string DocumentType = "",
    int FiscalYear = 0,
    string Author = "",
    DateOnly LastModified = default,
    string Title = "");
