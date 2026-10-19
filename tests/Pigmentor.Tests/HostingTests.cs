using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Conversations;
using Pigmentor.Core.DependencyInjection;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Tokens;
using Pigmentor.Generation;
using Pigmentor.GigaChat;
using Pigmentor.Hosting;
using Pigmentor.Retrieval;

namespace Pigmentor.Tests;

public sealed class HostingTests
{
    [Fact]
    public void CoreAssemblyDoesNotReferenceConcreteProviders()
    {
        var coreAssembly = typeof(Document).Assembly;
        var referencedAssemblies = coreAssembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain("Pigmentor.GigaChat", referencedAssemblies);
        Assert.DoesNotContain("OllamaSharp", referencedAssemblies);
    }

    [Fact]
    public void AddPigmentorCoreRegistersOnlyProviderIndependentServices()
    {
        var services = new ServiceCollection();
        services.AddPigmentorCore();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<ITokenCounter>());
        Assert.IsType<TokenCounter>(provider.GetService<ITokenCounter>());

        Assert.NotNull(provider.GetService<IConversationStateStore>());
        Assert.IsType<InMemoryConversationStateStore>(provider.GetService<IConversationStateStore>());

        // Provider-dependent AI clients must NOT be registered by Core alone
        Assert.Null(provider.GetService<IChatClient>());
        Assert.Null(provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Fact]
    public void AddPigmentorWithUseOllamaRegistersChatAndEmbeddingServices()
    {
        var services = new ServiceCollection();
        services.AddPigmentor()
            .UseOllama(new Uri("http://localhost:11434"), chatModel: "llama3.2", embeddingModel: "nomic-embed-text");

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<ITokenCounter>());
        Assert.NotNull(provider.GetService<IChatClient>());
        Assert.NotNull(provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Fact]
    public void AddPigmentorWithUseGigaChatRegistersClientAndAiAdapters()
    {
        var services = new ServiceCollection();
        services.AddPigmentor()
            .UseGigaChat(authorizationKey: "dGVzdC1hdXRoLWtleQ==", chatModel: "GigaChat-2-Pro", embeddingModel: "Embeddings");

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<GigaChatClient>());
        Assert.NotNull(provider.GetService<IChatClient>());
        Assert.NotNull(provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Fact]
    public void AddPigmentorWithUseConfiguredProvidersSelectsFromConfiguration()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Pigmentor:Llm:Provider"] = "Ollama",
            ["Pigmentor:Llm:Endpoint"] = "http://localhost:11434",
            ["Pigmentor:Llm:ChatModel"] = "llama3.2",
            ["Pigmentor:Embedding:Provider"] = "Ollama",
            ["Pigmentor:Embedding:Endpoint"] = "http://localhost:11434",
            ["Pigmentor:Embedding:Model"] = "nomic-embed-text",
            ["Pigmentor:Embedding:Dimensions"] = "768"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddPigmentor(configuration)
            .UseConfiguredProviders();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IChatClient>());
        Assert.NotNull(provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Fact]
    public void AddPigmentorRagPipelineReturnsBuilderAndWiresCore()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        var builder = services.AddPigmentorRagPipeline(configuration, options =>
        {
            options.UseQdrant("localhost:6334");
            options.EnableReranking();
        });

        Assert.NotNull(builder);
        Assert.Same(services, builder.Services);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<ITokenCounter>());
    }

    [Fact]
    public void AddPigmentorRegistersPipelineComponents()
    {
        var services = new ServiceCollection();
        services.AddPigmentor();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IReranker>());
        Assert.IsType<NoOpReranker>(provider.GetService<IReranker>());

        Assert.NotNull(provider.GetService<IContextBuilder>());
        Assert.IsType<ContextBuilder>(provider.GetService<IContextBuilder>());
    }
}

