using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Pigmentor.Tests.Fixtures;

/// <summary>
/// Deterministic test implementation of <see cref="IChatClient"/> for testing generation,
/// citation extraction, streaming parity, and evaluation quality gates without external LLM calls.
/// </summary>
public sealed class DeterministicRagChatClient : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("deterministic-chat-client");

    public bool SimulateHallucinatedCitation { get; set; }
    public Func<string, string>? CustomResponder { get; set; }
    public int CallCount { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;

        var fullPrompt = string.Join("\n", messages.Select(m => m.Text));
        var answer = GenerateAnswer(fullPrompt);

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;

        var fullPrompt = string.Join("\n", messages.Select(m => m.Text));
        var answer = GenerateAnswer(fullPrompt);

        // Stream tokens in chunks to test streaming parity
        var words = answer.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var piece = i == 0 ? words[i] : " " + words[i];
            yield return new ChatResponseUpdate(ChatRole.Assistant, piece);
            await Task.Yield();
        }
    }

    public string GenerateAnswer(string prompt)
    {
        if (CustomResponder != null)
        {
            return CustomResponder(prompt);
        }

        var questionMatch = Regex.Match(prompt, @"ВОПРОС:\s*(.+)$", RegexOptions.Singleline);
        var question = questionMatch.Success ? questionMatch.Groups[1].Value.Trim() : prompt;

        var contextMatch = Regex.Match(prompt, @"КОНТЕКСТ:\s*(.+?)\s*КОНЕЦ КОНТЕКСТА", RegexOptions.Singleline);
        var context = contextMatch.Success ? contextMatch.Groups[1].Value : string.Empty;

        string answer;

        if (string.IsNullOrWhiteSpace(context) || context.Contains("(Релевантные источники не найдены.)", StringComparison.Ordinal))
        {
            answer = "В предоставленном контексте недостаточно информации для ответа на этот вопрос.";
        }
        else
        {
            answer = SynthesizeFromQuestionAndContext(question, context);
        }

        if (SimulateHallucinatedCitation)
        {
            answer += " [S99]";
        }

        return answer;
    }

    private static string SynthesizeFromQuestionAndContext(string question, string context)
    {
        var q = question.ToLowerInvariant();

        // 1. Unanswerable / Insufficient Context Questions
        if (q.Contains("wi-fi") || q.Contains("пароль"))
        {
            return "В предоставленном контексте недостаточно информации о пароле беспроводной сети Wi-Fi.";
        }

        if (q.Contains("ip-адрес") || q.Contains("ethernet"))
        {
            return "В предоставленном контексте недостаточно информации об IP-адресе Ethernet.";
        }

        // 2. Multi-document query (case 17)
        if (q.Contains("40001") && q.Contains("modbus") && q.Contains("rs-485"))
        {
            var cite2 = context.Contains("[S2]", StringComparison.Ordinal) ? " [S2]" : "";
            return $"Опрос температуры из регистра 40001 выполняется по протоколу Modbus RTU со скоростью 115200 по интерфейсу RS-485 [S1]{cite2}.";
        }

        // 3. Numeric Precision 40001 (case 18)
        if (q.Contains("цену деления") || (q.Contains("диапазон") && q.Contains("измерения")) || (q.Contains("40001") && q.Contains("диапазон")))
        {
            return "Для температуры используется регистр 40001, цена деления составляет 0.1 °C, диапазон измерения -40..+85 °C [S1].";
        }

        // 4. Register 40057 AlarmThreshold (case 7)
        if (q.Contains("alarmthreshold") || q.Contains("тревог"))
        {
            return "Предел аварийной сигнализации AlarmThreshold задает регистр 40057 со значением температуры 80 °C [S1].";
        }

        // 5. Register 40001 single doc (case 4)
        if (q.Contains("40001") || (q.Contains("температур") && q.Contains("регистр")))
        {
            return "Температура устройства Temperature хранится в регистре 40001, тип данных INT16 [S1].";
        }

        // 5. RS-485 speed (case 1)
        if (q.Contains("rs-485") && (q.Contains("скорост") || q.Contains("115200")))
        {
            return "Для интерфейса RS-485 установлена скорость передачи данных 115200 бит/с [S1].";
        }

        // 6. RS-485 parity & stop bits (case 2)
        if (q.Contains("четност") || q.Contains("стоп-бит"))
        {
            return "Используются параметры связи по RS-485: четность None, 8 бит данных и 1 стоп-бит [S1].";
        }

        // 7. RS-485 resistor (case 3)
        if (q.Contains("согласующ") || q.Contains("резистор"))
        {
            return "На концах физической линии RS-485 необходим согласующий резистор 120 Ω [S1].";
        }

        // 8. Register 40002 Pressure (case 5)
        if (q.Contains("pressure") || (q.Contains("давлен") && q.Contains("holding")))
        {
            return "За Pressure отвечает Holding Register 40002 с типом данных UINT16 [S1].";
        }

        // 9. Register 40003 Humidity (case 6)
        if (q.Contains("humidity") || q.Contains("влажност"))
        {
            return "Параметры регистра влажности Humidity: адрес 40003, тип данных INT16 [S1].";
        }


        // 11. Register 40010 DeviceStatus (case 8)
        if (q.Contains("devicestatus") || q.Contains("состояния прибора"))
        {
            return "Номер регистра состояния прибора DeviceStatus: 40010, тип данных UINT16 [S1].";
        }

        // 12. Power Voltage 24V (case 9)
        if (q.Contains("напряжение") || q.Contains("питани"))
        {
            return "Для питания контроллера требуется стабилизированное напряжение 24 В постоянного тока [S1].";
        }

        // 13. Current Signal 4-20mA (case 10)
        if (q.Contains("токовый сигнал") || q.Contains("аналогов") || q.Contains("4-20"))
        {
            return "Для первичных датчиков поддерживается аналоговый токовый сигнал 4-20 мА [S1].";
        }

        // 14. Wiring A and B (case 11)
        if (q.Contains("клемм") || (q.Contains("линии") && q.Contains("подключа")))
        {
            return "Сигнальные линии RS-485 подключаются по двухпроводной схеме: прямой провод к клемме A, инверсный к клемме B [S1].";
        }

        // 15. Grounding GND (case 12)
        if (q.Contains("заземлен") || q.Contains("gnd") || q.Contains("экран"))
        {
            return "Защитное заземление экрана кабеля подключается к клемме GND интерфейса RS-485 только с одной стороны [S1].";
        }

        // 16. Calibration interval (case 13)
        if (q.Contains("поверк") || q.Contains("калибровк") || q.Contains("12 месяцев"))
        {
            return "Периодичность метрологической поверки датчиков давления составляет 12 месяцев [S1].";
        }

        // 17. Operating Temperature Range (case 14)
        if (q.Contains("диапазон") && q.Contains("температур"))
        {
            return "Эксплуатация оборудования допускается в диапазоне температур от -10 °C до +50 °C [S1].";
        }

        // 18. Protection IP65 (case 15)
        if (q.Contains("степень защиты") || q.Contains("оболочк") || q.Contains("ip65"))
        {
            return "Корпус прибора обеспечивает степень защиты оболочки IP65 [S1].";
        }

        // 19. Safety in installation (case 16)
        if (q.Contains("безопасност") || q.Contains("монтаж") || q.Contains("обесточ"))
        {
            return "Перед проведением любых монтажных работ регламентировано полностью обесточить оборудование [S1].";
        }

        // General fallback: if context has source lines, summarize first sentence
        return "Согласно предоставленной технической документации, система обеспечивает штатную работу [S1].";
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
