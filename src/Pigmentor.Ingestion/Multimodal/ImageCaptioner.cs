using Microsoft.Extensions.AI;

namespace Pigmentor.Ingestion.Multimodal;

/// <summary>
/// Generates a one- to two-sentence caption for each
/// <see cref="ExtractedImage"/> via a vision-capable
/// <see cref="IChatClient"/> (e.g. GPT-4o, Claude Sonnet vision, llava
/// via Ollama).
/// </summary>
public sealed class ImageCaptioner
{
    private readonly IChatClient _chat;
    private const string Prompt =
        "Describe this image in one or two sentences. Focus on the subject, " +
        "any data shown (axes, labels, trend), and any text visible. Be concrete and concise.";

    public ImageCaptioner(IChatClient chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
    }

    /// <summary>
    /// Caption a single image. The image is loaded from
    /// <see cref="ExtractedImage.ImagePath"/> and sent to the chat client
    /// as a multimodal user message; the model's response replaces
    /// <see cref="ExtractedImage.Caption"/>.
    /// </summary>
    public async Task<ExtractedImage> CaptionAsync(ExtractedImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (string.IsNullOrEmpty(image.ImagePath))
        {
            return image;
        }

        var bytes = await File.ReadAllBytesAsync(image.ImagePath, cancellationToken).ConfigureAwait(false);
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent(Prompt),
            new DataContent(bytes, mediaType: "image/png"),
        ]);
        var response = await _chat.GetResponseAsync([message], cancellationToken: cancellationToken).ConfigureAwait(false);
        return image with { Caption = (response.Text ?? string.Empty).Trim() };
    }

    /// <summary>Caption a batch of images sequentially.</summary>
    public async Task<IReadOnlyList<ExtractedImage>> CaptionAllAsync(
        IEnumerable<ExtractedImage> images,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        var results = new List<ExtractedImage>();
        foreach (var img in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await CaptionAsync(img, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }
}
