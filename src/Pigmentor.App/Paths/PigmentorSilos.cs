namespace Pigmentor.App.Paths;

/// <summary>
/// The six fixed document silos supported by Pigmentor.
/// Users cannot configure additional silos.
/// </summary>
public static class PigmentorSilos
{
    public const string HrPolicies = "hr-policies";
    public const string TechnicalDocs = "technical-docs";
    public const string FinancialReports = "financial-reports";
    public const string LegalContracts = "legal-contracts";
    public const string ProductCatalog = "product-catalog";
    public const string ReleaseNotesTickets = "release-notes-tickets";

    public static readonly IReadOnlyList<string> All =
    [
        HrPolicies,
        TechnicalDocs,
        FinancialReports,
        LegalContracts,
        ProductCatalog,
        ReleaseNotesTickets
    ];

    private static readonly HashSet<string> KnownSilos = new(All, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnownSilo(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && KnownSilos.Contains(name.Trim());
    }
}

