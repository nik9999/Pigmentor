using Pigmentor.Core.Abstractions;

namespace Pigmentor.Ingestion.Quality;

/// <summary>
/// Deterministic quality assessment for OCR-derived text before admission to chunks and embeddings.
/// </summary>
public static class OcrQualityGate
{
    private const double MinAlphanumericRatio = 0.35;
    private const int MinMeaningfulCharacters = 3;

    public static TextQualityAssessment Assess(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TextQualityAssessment.Rejected(0.0, "Текст пуст или содержит только пробельные символы.");
        }

        var trimmed = text.Trim();
        var nonWhitespaceChars = 0;
        var alphanumericChars = 0;
        var garbageChars = 0;
        var maxConsecutiveRepeats = 0;
        var currentRepeatCount = 1;
        char prevChar = '\0';

        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            nonWhitespaceChars++;

            if (char.IsLetterOrDigit(ch))
            {
                alphanumericChars++;
            }
            else if (ch == '\uFFFD' || ch == '|' || ch == '~' || ch == '^' || ch == '`')
            {
                garbageChars++;
            }

            if (ch == prevChar)
            {
                currentRepeatCount++;
                if (currentRepeatCount > maxConsecutiveRepeats)
                {
                    maxConsecutiveRepeats = currentRepeatCount;
                }
            }
            else
            {
                currentRepeatCount = 1;
                prevChar = ch;
            }
        }

        if (nonWhitespaceChars < MinMeaningfulCharacters)
        {
            return TextQualityAssessment.Rejected(0.1, "Слишком короткий текст без значимого содержания.");
        }

        var alphaRatio = (double)alphanumericChars / nonWhitespaceChars;
        var garbageRatio = (double)garbageChars / nonWhitespaceChars;

        var reasons = new List<string>();

        if (alphaRatio < MinAlphanumericRatio)
        {
            reasons.Add(FormattableString.Invariant(
                $"Низкая доля буквенно-цифровых символов: {alphaRatio:P1} (минимум {MinAlphanumericRatio:P1})."));
        }

        if (garbageRatio > 0.30)
        {
            reasons.Add(FormattableString.Invariant(
                $"Высокая доля мусорных символов: {garbageRatio:P1}."));
        }

        if (maxConsecutiveRepeats >= 6 && !char.IsDigit(prevChar))
        {
            reasons.Add("Обнаружена аномальная серия повторяющихся символов (шум сканирования/OCR).");
        }

        var words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 5 && words.Distinct().Count() <= 2)
        {
            reasons.Add("Обнаружена аномальная серия повторяющихся слов или символов (шум сканирования/OCR).");
        }

        var score = Math.Clamp(alphaRatio * (1.0 - garbageRatio), 0.0, 1.0);
        if (reasons.Count > 0)
        {
            score = Math.Min(score, 0.4);
        }

        return reasons.Count == 0
            ? TextQualityAssessment.Acceptable(score)
            : TextQualityAssessment.Rejected(score, [.. reasons]);
    }
}
