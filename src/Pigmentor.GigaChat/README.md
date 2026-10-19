# Pigmentor.GigaChat

Провайдер интеграции со **Сбер GigaChat REST API** для .NET 10, реализующий стандартные абстракции **Microsoft.Extensions.AI**.

## Возможности

* **Абстракции `Microsoft.Extensions.AI`**:
  * `GigaChatChatClient : IChatClient` — синхронные ответы (`GetResponseAsync`) и потоковая передача токенов Server-Sent Events (`GetStreamingResponseAsync`).
  * `GigaChatEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>` — получение плотных векторных представлений (модели `Embeddings`, `Embeddings-2`, `EmbeddingsGigaR`).
* **OAuth 2.0 и управление сессиями**:
  * Поддержка авторизационных ключей GigaChat Studio (Base64 `Client_ID:Client_Secret`) и прямых JWT токенов.
  * Автоматическое кэширование и бесшовная ротация токенов доступа до истечения срока действия (`expires_at`).
  * Генерация уникальных идентификаторов запросов `RqUID` (UUID v4) на каждый вызов авторизации.
  * Поддержка всех областей доступа: `GIGACHAT_API_PERS` (физические лица), `GIGACHAT_API_CORP` (юридические лица), `GIGACHAT_API_B2B`.
* **Сертификаты Минцифры**:
  * По умолчанию проверка TLS-сертификатов включена. Опцию `IgnoreSslValidation = true` следует использовать только явно в контролируемой среде, если корневой сертификат не установлен в хранилище ОС.
* **Вызов функций (Function Calling)**:
  * Полная двусторонняя трансляция между `AIFunction` / `FunctionCallContent` и структурой `functions` / `function_call` в API GigaChat.
* **Расширения Dependency Injection**:
  * `services.AddGigaChatClient(...)`
  * `services.AddGigaChatChatClient(...)`
  * `services.AddGigaChatEmbeddingGenerator(...)`

## Пример использования

```csharp
using Microsoft.Extensions.AI;
using Pigmentor.GigaChat;
using Pigmentor.GigaChat.Extensions;

// 1. Создание клиента GigaChat
using var gigaClient = new GigaChatClient(
    authorizationKey: Environment.GetEnvironmentVariable("GIGACHAT_AUTH_KEY")!,
    scope: GigaChatScope.Personal);

// 2. Использование IChatClient
IChatClient chatClient = gigaClient.AsChatClient(GigaChatModels.GigaChat);
var response = await chatClient.GetResponseAsync("Объясни принцип работы RAG простыми словами.");
Console.WriteLine(response.Text);

// 3. Потоковая генерация ответа
await foreach (var chunk in chatClient.GetStreamingResponseAsync("Напиши краткое приветствие."))
{
    Console.Write(chunk.Text);
}

// 4. Генерация эмбеддингов
var embedder = gigaClient.AsEmbeddingGenerator(GigaChatModels.Embeddings);
var embeddings = await embedder.GenerateAsync(["Текст для векторизации"]);
ReadOnlyMemory<float> vector = embeddings[0].Vector;
```
