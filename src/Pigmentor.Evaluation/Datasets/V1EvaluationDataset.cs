using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;
using Pigmentor.Evaluation.Cases;

namespace Pigmentor.Evaluation.Datasets;

/// <summary>
/// Canonical deterministic evaluation dataset for Pigmentor v1 containing 20 focused,
/// realistic industrial automation test cases covering technical prose, tables, registers,
/// numeric exactness, metadata filtering, and insufficient-context questions.
/// </summary>
public static class V1EvaluationDataset
{
    public static IReadOnlyList<RagEvaluationCase> GetCases() =>
    [
        // 1. RS-485 Communication
        new RagEvaluationCase(
            Id: "rs485-speed-001",
            Question: "Какая скорость передачи данных установлена для интерфейса RS-485?",
            RelevantDocumentIds: ["communication-settings"],
            ExpectedFacts: ["115200", "RS-485"],
            ExpectedTechnicalTerms: ["RS-485", "115200"],
            Category: "Communication"),

        new RagEvaluationCase(
            Id: "rs485-parity-002",
            Question: "Какие параметры четности и стоп-битов используются для связи?",
            RelevantDocumentIds: ["communication-settings"],
            ExpectedFacts: ["None", "8", "1"],
            ExpectedTechnicalTerms: ["None", "RS-485"],
            Category: "Communication"),

        new RagEvaluationCase(
            Id: "rs485-resistor-003",
            Question: "Какой номинал согласующего резистора необходим на концах линии RS-485?",
            RelevantDocumentIds: ["communication-settings"],
            ExpectedFacts: ["120 Ω", "согласующий резистор"],
            ExpectedTechnicalTerms: ["120 Ω", "RS-485"],
            Category: "Communication"),

        // 2. Modbus Register Tables & Numeric Exactness
        new RagEvaluationCase(
            Id: "modbus-temp-register-004",
            Question: "В каком регистре хранится температура устройства и каков её тип?",
            RelevantDocumentIds: ["modbus-registers"],
            ExpectedFacts: ["40001", "Temperature", "INT16"],
            ExpectedTechnicalTerms: ["40001", "Temperature", "INT16"],
            Category: "Modbus"),

        new RagEvaluationCase(
            Id: "modbus-pressure-register-005",
            Question: "Какой Holding Register отвечает за Pressure и каков его тип?",
            RelevantDocumentIds: ["modbus-registers"],
            ExpectedFacts: ["40002", "Pressure", "UINT16"],
            ExpectedTechnicalTerms: ["Holding Register", "40002", "Pressure", "UINT16"],
            Category: "Modbus"),

        new RagEvaluationCase(
            Id: "modbus-humidity-register-006",
            Question: "Покажи параметры регистра влажности Humidity.",
            RelevantDocumentIds: ["modbus-registers"],
            ExpectedFacts: ["40003", "Humidity", "INT16"],
            ExpectedTechnicalTerms: ["40003", "Humidity", "INT16"],
            Category: "Modbus"),

        new RagEvaluationCase(
            Id: "modbus-alarm-register-007",
            Question: "Какой регистр задает предел тревоги AlarmThreshold и каково значение температуры?",
            RelevantDocumentIds: ["modbus-registers"],
            ExpectedFacts: ["40057", "AlarmThreshold", "80 °C"],
            ExpectedTechnicalTerms: ["40057", "AlarmThreshold", "80 °C"],
            Category: "Modbus"),

        new RagEvaluationCase(
            Id: "modbus-status-register-008",
            Question: "Какой номер у регистра состояния прибора DeviceStatus?",
            RelevantDocumentIds: ["modbus-registers"],
            ExpectedFacts: ["40010", "DeviceStatus", "UINT16"],
            ExpectedTechnicalTerms: ["40010", "DeviceStatus", "UINT16"],
            Category: "Modbus"),

        // 3. System Hardware & Electrical Specs
        new RagEvaluationCase(
            Id: "system-voltage-009",
            Question: "Какое напряжение питания требуется для работы контроллера?",
            RelevantDocumentIds: ["manual-overview"],
            ExpectedFacts: ["24 В", "постоянного тока"],
            ExpectedTechnicalTerms: ["24 В"],
            Category: "Hardware"),

        new RagEvaluationCase(
            Id: "system-analog-signal-010",
            Question: "Какой аналоговый токовый сигнал поддерживается для датчиков?",
            RelevantDocumentIds: ["manual-overview"],
            ExpectedFacts: ["4-20 мА", "токовый сигнал"],
            ExpectedTechnicalTerms: ["4-20 мА"],
            Category: "Hardware"),

        // 4. Wiring & Installation
        new RagEvaluationCase(
            Id: "wiring-lines-011",
            Question: "К каким клеммам подключаются сигнальные линии A и B интерфейса RS-485?",
            RelevantDocumentIds: ["installation-guide"],
            ExpectedFacts: ["A", "B", "двухпроводная"],
            ExpectedTechnicalTerms: ["A", "B", "RS-485"],
            Category: "Installation"),

        new RagEvaluationCase(
            Id: "wiring-grounding-012",
            Question: "Как подключается защитное заземление экрана GND?",
            RelevantDocumentIds: ["installation-guide"],
            ExpectedFacts: ["GND", "экран кабеля"],
            ExpectedTechnicalTerms: ["GND", "RS-485"],
            Category: "Installation"),

        // 5. Maintenance & Operating Environment
        new RagEvaluationCase(
            Id: "maintenance-calibration-013",
            Question: "Какова периодичность метрологической поверки датчиков давления?",
            RelevantDocumentIds: ["maintenance-guide"],
            ExpectedFacts: ["12 месяцев", "поверка"],
            ExpectedTechnicalTerms: ["12 месяцев"],
            Category: "Maintenance"),

        new RagEvaluationCase(
            Id: "maintenance-temp-range-014",
            Question: "В каком диапазоне температур допускается эксплуатация оборудования?",
            RelevantDocumentIds: ["maintenance-guide"],
            ExpectedFacts: ["-10 °C", "+50 °C"],
            ExpectedTechnicalTerms: ["-10 °C", "+50 °C"],
            Category: "Maintenance"),

        new RagEvaluationCase(
            Id: "maintenance-protection-015",
            Question: "Какую степень защиты оболочки имеет корпус промышленного прибора?",
            RelevantDocumentIds: ["maintenance-guide"],
            ExpectedFacts: ["IP65"],
            ExpectedTechnicalTerms: ["IP65"],
            Category: "Maintenance"),

        // 6. Metadata Filtering (Engineering vs HR)
        new RagEvaluationCase(
            Id: "metadata-filter-eng-016",
            Question: "Какие основные правила безопасности регламентированы при монтаже?",
            RelevantDocumentIds: ["installation-guide"],
            ExpectedFacts: ["монтаж", "обесточить"],
            ExpectedTechnicalTerms: ["обесточить"],
            MetadataFilter: MetadataFilter.Where(m => m.Department == "Engineering"),
            Category: "Filtering"),

        // 7. Multi-document Retrieval
        new RagEvaluationCase(
            Id: "multi-doc-modbus-rs485-017",
            Question: "Как опрашивается температура регистра 40001 через протокол Modbus RTU по RS-485?",
            RelevantDocumentIds: ["communication-settings", "modbus-registers"],
            ExpectedFacts: ["40001", "115200", "RS-485"],
            ExpectedTechnicalTerms: ["40001", "RS-485", "Modbus RTU"],
            Category: "MultiDocument"),

        // 8. Numeric Precision
        new RagEvaluationCase(
            Id: "numeric-precision-40001-018",
            Question: "Укажи точный номер регистра температуры, цену деления и диапазон измерения.",
            RelevantDocumentIds: ["modbus-registers"],
            ExpectedFacts: ["40001", "0.1 °C", "-40..+85 °C"],
            ExpectedTechnicalTerms: ["40001", "0.1 °C", "-40..+85 °C"],
            Category: "NumericPrecision"),

        // 9. Insufficient Context
        new RagEvaluationCase(
            Id: "insufficient-context-wifi-019",
            Question: "Какой пароль от беспроводной сети Wi-Fi установлен на приборе?",
            RelevantDocumentIds: [],
            ExpectedFacts: [],
            ExpectInsufficientContext: true,
            Category: "InsufficientContext"),

        new RagEvaluationCase(
            Id: "insufficient-context-ip-020",
            Question: "Какой IP-адрес назначен Ethernet-порту устройства по умолчанию?",
            RelevantDocumentIds: [],
            ExpectedFacts: [],
            ExpectInsufficientContext: true,
            Category: "InsufficientContext")
    ];
}
