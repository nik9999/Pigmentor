using System.Text.RegularExpressions;
using Pigmentor.Evaluation.Datasets;
using Xunit;

namespace Pigmentor.Tests;

public sealed class EvaluationDatasetTests
{
    [Fact]
    public void V1EvaluationDataset_ContainsExactly20Cases()
    {
        var cases = V1EvaluationDataset.GetCases();
        Assert.Equal(20, cases.Count);
    }

    [Fact]
    public void V1EvaluationDataset_CaseIdsAreUniqueAndNonEmpty()
    {
        var cases = V1EvaluationDataset.GetCases();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Id));
            Assert.True(ids.Add(c.Id), $"Duplicate CaseId found: {c.Id}");
        }
    }

    [Fact]
    public void V1EvaluationDataset_QuestionsAreInRussianAndNonEmpty()
    {
        var cases = V1EvaluationDataset.GetCases();
        var cyrillicRegex = new Regex(@"\p{IsCyrillic}");

        foreach (var c in cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Question));
            Assert.Matches(cyrillicRegex, c.Question);
        }
    }

    [Fact]
    public void V1EvaluationDataset_AllReferencedDocumentsExistInCorpusOrAreEmpty()
    {
        var cases = V1EvaluationDataset.GetCases();
        var corpus = V1EvaluationCorpus.GetDocuments();
        var corpusDocIds = corpus.Select(d => d.Metadata.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var c in cases)
        {
            if (c.ExpectInsufficientContext)
            {
                Assert.Empty(c.RelevantDocumentIds);
            }
            else
            {
                Assert.NotEmpty(c.RelevantDocumentIds);
                foreach (var docId in c.RelevantDocumentIds)
                {
                    Assert.Contains(docId, corpusDocIds);
                }
            }
        }
    }

    [Fact]
    public void V1EvaluationDataset_InsufficientContextCases_ProperlyConfigured()
    {
        var cases = V1EvaluationDataset.GetCases();
        var unanswerable = cases.Where(c => c.ExpectInsufficientContext).ToList();

        Assert.Equal(2, unanswerable.Count);
        foreach (var c in unanswerable)
        {
            Assert.Empty(c.RelevantDocumentIds);
            Assert.Empty(c.ExpectedFacts ?? []);
        }
    }

    [Fact]
    public void V1EvaluationCorpus_ContainsValidDocumentsWithNonEmptyContent()
    {
        var docs = V1EvaluationCorpus.GetDocuments();
        Assert.Equal(6, docs.Count);

        var docIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in docs)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Metadata.Id));
            Assert.True(docIds.Add(d.Metadata.Id), $"Duplicate DocumentId: {d.Metadata.Id}");
            Assert.False(string.IsNullOrWhiteSpace(d.Content));
            Assert.False(string.IsNullOrWhiteSpace(d.Metadata.Department));
            Assert.False(string.IsNullOrWhiteSpace(d.Metadata.Title));
        }

        // Verify engineering vs HR segregation for metadata filtering tests
        var engineeringDocs = docs.Where(d => d.Metadata.Department == "Engineering").ToList();
        var hrDocs = docs.Where(d => d.Metadata.Department == "HR").ToList();

        Assert.Equal(5, engineeringDocs.Count);
        Assert.Single(hrDocs);
        Assert.Equal("hr-policy", hrDocs[0].Metadata.Id);
    }
}
