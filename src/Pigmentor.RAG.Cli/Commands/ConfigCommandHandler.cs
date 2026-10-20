using Pigmentor.App.Configuration;
using Pigmentor.RAG.Cli.Console;

namespace Pigmentor.RAG.Cli.Commands;

public sealed class ConfigCommandHandler
{
    private readonly PigmentorConfigManager _configManager;
    private readonly IConsoleIO _console;

    public ConfigCommandHandler(PigmentorConfigManager configManager, IConsoleIO console)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public async Task<int> ShowAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configManager.LoadAsync(cancellationToken).ConfigureAwait(false);

        _console.WriteLine("Pigmentor Configuration:");
        _console.WriteLine();
        _console.WriteLine("Chat:");
        _console.WriteLine($"  Provider:            {config.Chat.Provider}");
        _console.WriteLine($"  Model:               {config.Chat.Model}");
        _console.WriteLine($"  Endpoint:            {config.Chat.Endpoint}");
        _console.WriteLine();
        _console.WriteLine("Embeddings:");
        _console.WriteLine($"  Provider:            {config.Embeddings.Provider}");
        _console.WriteLine($"  Model:               {config.Embeddings.Model}");
        _console.WriteLine($"  Endpoint:            {config.Embeddings.Endpoint}");
        _console.WriteLine($"  ModelPath:           {config.Embeddings.ModelPath ?? "(none)"}");
        _console.WriteLine($"  Dimensions:          {config.Embeddings.Dimensions}");
        _console.WriteLine();
        _console.WriteLine("GigaChat:");
        _console.WriteLine($"  Scope:               {config.GigaChat.Scope}");
        _console.WriteLine($"  Verify SSL:          {config.GigaChat.VerifySsl}");
        _console.WriteLine();
        _console.WriteLine("Storage:");
        _console.WriteLine($"  Database:            {config.Storage.DatabasePath}");

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
            // Secret setting rejection
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

