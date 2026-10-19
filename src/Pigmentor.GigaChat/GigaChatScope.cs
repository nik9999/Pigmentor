namespace Pigmentor.GigaChat;

/// <summary>
/// OAuth scope constants for Sber GigaChat API authentication.
/// </summary>
public static class GigaChatScope
{
    /// <summary>
    /// Personal scope for individual developers (free tier &amp; pay-as-you-go for individuals).
    /// </summary>
    public const string Personal = "GIGACHAT_API_PERS";

    /// <summary>
    /// Corporate scope for organizations and legal entities (pay-as-you-go model).
    /// </summary>
    public const string Corporate = "GIGACHAT_API_CORP";

    /// <summary>
    /// B2B scope for individual entrepreneurs and legal entities using prepaid query packages.
    /// </summary>
    public const string B2B = "GIGACHAT_API_B2B";
}
