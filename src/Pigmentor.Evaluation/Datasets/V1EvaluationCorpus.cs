using Pigmentor.Core.Documents;

namespace Pigmentor.Evaluation.Datasets;

/// <summary>
/// Deterministic evaluation corpus containing realistic industrial technical documentation
/// with competing vocabulary across documents for testing retrieval, grounding, and filtering.
/// </summary>
public static class V1EvaluationCorpus
{
    public static IReadOnlyList<Document> GetDocuments() =>
    [
        new Document(
            Metadata: new DocumentMetadata(
                Id: "manual-overview",
                Silo: "technical-docs",
                Department: "Engineering",
                Office: "HQ",
                ConfidentialityLevel: "Internal",
                DocumentType: "Manual",
                FiscalYear: 2026,
                Author: "Lead Systems Engineer",
                LastModified: new DateOnly(2026, 3, 1),
                Title: "Общее руководство по промышленной системе Pigmentor"),
            Content: """
                # Общее руководство по промышленной системе Pigmentor

                Промышленная система Pigmentor предназначена для автоматизации технологических процессов и диспетчеризации.
                Устройство обеспечивает непрерывный сбор данных с первичных датчиков, используя аналоговый токовый сигнал 4-20 мА.
                Для электропитания контроллера требуется стабилизированное напряжение 24 В постоянного тока.
                Внешний обмен данными с верхним уровнем АСУТП осуществляется по последовательному интерфейсу RS-485 и протоколу Modbus RTU.
                Архитектура системы гарантирует детерминированную обработку телеметрии и надежную регистрацию событий.
                """,
            SourcePath: "manual-overview.txt"),

        new Document(
            Metadata: new DocumentMetadata(
                Id: "communication-settings",
                Silo: "technical-docs",
                Department: "Engineering",
                Office: "HQ",
                ConfidentialityLevel: "Internal",
                DocumentType: "Specification",
                FiscalYear: 2026,
                Author: "Communications Specialist",
                LastModified: new DateOnly(2026, 3, 5),
                Title: "Параметры и протоколы связи интерфейса RS-485"),
            Content: """
                # Параметры и протоколы связи интерфейса RS-485

                Интерфейс RS-485 поддерживает промышленный протокол связи Modbus RTU в режиме ведомого (Slave).
                По умолчанию скорость передачи данных установлена 115200 бит/с (baud rate 115200).
                Формат передачи данных: 8 бит данных, четность None (без контроля четности), 1 стоп-бит (8N1).
                Для предотвращения отражения высокочастотных сигналов на обоих концах физической линии RS-485 должен быть установлен согласующий резистор 120 Ω.
                Максимальная протяженность линии связи без повторителей составляет 1200 метров при кабеле витая пара.
                """,
            SourcePath: "communication-settings.txt"),

        new Document(
            Metadata: new DocumentMetadata(
                Id: "modbus-registers",
                Silo: "technical-docs",
                Department: "Engineering",
                Office: "HQ",
                ConfidentialityLevel: "Internal",
                DocumentType: "RegisterMap",
                FiscalYear: 2026,
                Author: "Firmware Team",
                LastModified: new DateOnly(2026, 3, 10),
                Title: "Карта регистров Modbus RTU"),
            Content: """
                # Карта регистров Modbus RTU

                Таблица: Карта регистров хранения Modbus RTU (Holding Registers)
                Address | Name | Type | Access | Description
                40001 | Temperature | INT16 | RO | Температура устройства, цена деления 0.1 °C, диапазон -40..+85 °C
                40002 | Pressure | UINT16 | RO | Давление процесса Pressure, цена деления 0.01 бар, диапазон 0..100 бар
                40003 | Humidity | INT16 | RO | Относительная влажность Humidity, цена деления 0.1 %, диапазон 0..100 %
                40010 | DeviceStatus | UINT16 | RO | Регистр состояния прибора DeviceStatus, битовая маска аварий
                40057 | AlarmThreshold | INT16 | RW | Предел аварийной сигнализации AlarmThreshold по температуре 80 °C

                Все регистры Holding Register передаются по протоколу Modbus RTU со старшим байтом вперед (Big-Endian).
                """,
            SourcePath: "modbus-registers.txt"),

        new Document(
            Metadata: new DocumentMetadata(
                Id: "installation-guide",
                Silo: "technical-docs",
                Department: "Engineering",
                Office: "Field Operations",
                ConfidentialityLevel: "Internal",
                DocumentType: "Guide",
                FiscalYear: 2026,
                Author: "Installation Lead",
                LastModified: new DateOnly(2026, 3, 12),
                Title: "Инструкция по монтажу и электрическому подключению"),
            Content: """
                # Инструкция по монтажу и электрическому подключению

                Перед проведением любых монтажных работ необходимо полностью обесточить оборудование.
                Подключение интерфейса RS-485 выполняется по двухпроводной схеме экранированной витой парой.
                Прямой сигнальный провод подключается к клемме A, инверсный провод подключается к клемме B.
                Экран кабеля подключается к клемме защитного заземления GND только с одной стороны линии связи.
                Электропитание 24 В постоянного тока подключается с соблюдением полярности к клеммам V+ и V-.
                """,
            SourcePath: "installation-guide.txt"),

        new Document(
            Metadata: new DocumentMetadata(
                Id: "maintenance-guide",
                Silo: "technical-docs",
                Department: "Engineering",
                Office: "Quality Assurance",
                ConfidentialityLevel: "Internal",
                DocumentType: "Guide",
                FiscalYear: 2026,
                Author: "QA Specialist",
                LastModified: new DateOnly(2026, 3, 15),
                Title: "Регламент технического обслуживания и условия эксплуатации"),
            Content: """
                # Регламент технического обслуживания и условия эксплуатации

                Эксплуатация оборудования допускается в рабочем диапазоне температур от -10 °C до +50 °C.
                Корпус промышленного прибора выполнен из алюминиевого сплава и обеспечивает степень защиты оболочки IP65.
                Периодичность метрологической поверки датчиков давления составляет 12 месяцев.
                При регулярном техническом обслуживании проверяется целостность заземления и надежность винтовых клеммных соединений.
                """,
            SourcePath: "maintenance-guide.txt"),

        new Document(
            Metadata: new DocumentMetadata(
                Id: "hr-policy",
                Silo: "corporate-docs",
                Department: "HR",
                Office: "HQ",
                ConfidentialityLevel: "Internal",
                DocumentType: "Policy",
                FiscalYear: 2026,
                Author: "Human Resources",
                LastModified: new DateOnly(2026, 1, 15),
                Title: "Политика управления персоналом и внутренний распорядок"),
            Content: """
                # Политика управления персоналом и внутренний распорядок

                Политика отдела кадров регламентирует трудовой распорядок, графики сменности и отпусков сотрудников.
                Сотрудники обязаны соблюдать правила внутреннего распорядка, дисциплины и проходить периодический инструктаж.
                Рабочий день инженерно-технического персонала составляет 8 часов с перерывом на обед.
                Заявки на повышение квалификации и обучение подаются за один месяц до начала учебного курса.
                """,
            SourcePath: "hr-policy.txt")
    ];
}
