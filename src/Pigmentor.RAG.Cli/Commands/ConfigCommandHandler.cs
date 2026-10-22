using Pigmentor.App.Configuration;
using Pigmentor.App.Paths;
using Pigmentor.RAG.Cli.Console;

namespace Pigmentor.RAG.Cli.Commands;

public sealed class ConfigCommandHandler
{
    private readonly IPigmentorAppPaths _paths;
    private readonly PigmentorConfigManager _configManager;
    private readonly IConsoleIO _console;

    public ConfigCommandHandler(IPigmentorAppPaths paths, PigmentorConfigManager configManager, IConsoleIO console)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public async Task<int> ShowAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configManager.LoadAsync(cancellationToken).ConfigureAwait(false);
        var effectiveRoot = _paths.ResolveEffectiveRootPath(config.RootPath);
        var dbPath = _paths.GetDatabasePath(effectiveRoot);

        _console.WriteLine("Pigmentor Configuration:");
        _console.WriteLine();
        _console.WriteLine("Workspace:");
        var configuredRoot = config.RootPath ?? "data";
        _console.WriteLine($"  Root Path:           {configuredRoot} (resolved: {effectiveRoot})");
        _console.WriteLine($"  Database:            {dbPath}");
        _console.WriteLine();
        _console.WriteLine("Chat:");
        _console.WriteLine($"  Provider:            {config.Chat?.Provider ?? "(none)"}");
        _console.WriteLine($"  Model:               {config.Chat?.Model ?? "(none)"}");
        _console.WriteLine($"  Endpoint:            {config.Chat?.Endpoint ?? "(none)"}");
        _console.WriteLine();
        _console.WriteLine("Embeddings:");
        _console.WriteLine($"  Provider:            {config.Embeddings?.Provider ?? "(none)"}");
        _console.WriteLine($"  Model:               {config.Embeddings?.Model ?? "(none)"}");
        _console.WriteLine($"  Endpoint:            {config.Embeddings?.Endpoint ?? "(none)"}");
        if (string.Equals(config.Embeddings?.Provider, "Onnx", StringComparison.OrdinalIgnoreCase))
        {
            var resolvedModel = !string.IsNullOrWhiteSpace(config.Embeddings?.ModelPath)
                ? _paths.ResolveApplicationPath(config.Embeddings.ModelPath)
                : "(none)";
            var resolvedTok = !string.IsNullOrWhiteSpace(config.Embeddings?.TokenizerPath)
                ? _paths.ResolveApplicationPath(config.Embeddings.TokenizerPath)
                : "(none)";
            _console.WriteLine($"  Model Path:          {config.Embeddings?.ModelPath ?? "(none)"} (resolved: {resolvedModel})");
            _console.WriteLine($"  Tokenizer Path:      {config.Embeddings?.TokenizerPath ?? "(none)"} (resolved: {resolvedTok})");
        }
        else
        {
            _console.WriteLine($"  Model Path:          {config.Embeddings?.ModelPath ?? "(none)"}");
            _console.WriteLine($"  Tokenizer Path:      {config.Embeddings?.TokenizerPath ?? "(none)"}");
        }
        _console.WriteLine($"  Dimensions:          {config.Embeddings?.Dimensions ?? 0}");
        _console.WriteLine();
        _console.WriteLine("GigaChat:");
        _console.WriteLine($"  Scope:               {config.GigaChat?.Scope ?? "(none)"}");
        _console.WriteLine($"  Verify SSL:          {config.GigaChat?.VerifySsl ?? true}");
        _console.WriteLine();
        _console.WriteLine("Reranker:");
        _console.WriteLine($"  Enabled:             {config.Reranker?.Enabled ?? false}");
        _console.WriteLine($"  Provider:            {config.Reranker?.Provider ?? "(none)"}");
        if (config.Reranker != null)
        {
            var resolvedModel = !string.IsNullOrWhiteSpace(config.Reranker.ModelPath)
                ? _paths.ResolveApplicationPath(config.Reranker.ModelPath)
                : "(none)";
            var resolvedTok = !string.IsNullOrWhiteSpace(config.Reranker.TokenizerPath)
                ? _paths.ResolveApplicationPath(config.Reranker.TokenizerPath)
                : "(none)";
            _console.WriteLine($"  Model Path:          {config.Reranker.ModelPath ?? "(none)"} (resolved: {resolvedModel})");
            _console.WriteLine($"  Tokenizer Path:      {config.Reranker.TokenizerPath ?? "(none)"} (resolved: {resolvedTok})");
            _console.WriteLine($"  Max Pairs/Batch:     {config.Reranker.MaxPairsPerBatch}");
            _console.WriteLine($"  Max Sequence Length: {config.Reranker.MaxSequenceLength}");
            _console.WriteLine($"  Min Score:           {config.Reranker.MinScore?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"}");
        }

        return 0;
    }

    public async Task<int> SetAsync(string setting, string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(setting))
        {
            _console.WriteError("Error: Configuration setting name is required.");
            return 2;
        }

        if (value == null)
        {
            _console.WriteError("Error: Configuration value is required.");
            return 2;
        }

        try
        {
            await _configManager.SetSettingAsync(setting, value, cancellationToken).ConfigureAwait(false);
            _console.WriteLine($"Configuration updated: {setting} = {value}");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            _console.WriteError($"Error: {ex.Message}");
            return 2;
        }
        catch (ArgumentException ex)
        {
            _console.WriteError($"Error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            _console.WriteError($"Error updating configuration: {ex.Message}");
            return 1;
        }
    }
}
