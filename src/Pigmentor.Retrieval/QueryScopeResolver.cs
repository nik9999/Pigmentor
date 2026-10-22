using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;

namespace Pigmentor.Retrieval;

/// <summary>
/// Deterministic query-scope resolver. Maps explicit user intent to storage-neutral <see cref="MetadataFilter"/>
/// across existing silos (e.g. product-catalog, technical-docs) using externalized or configured <see cref="ScopeRulesConfig"/>.
/// </summary>
public static class QueryScopeResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static ScopeRulesConfig? _currentConfig;
    private static IReadOnlyList<CompiledRule>? _compiledRules;
    private static readonly object ConfigLock = new();

    /// <summary>
    /// Gets or sets the active scope configuration.
    /// Defaults to loading from <c>data/scope-rules.json</c> or built-in defaults.
    /// </summary>
    public static ScopeRulesConfig CurrentConfig
    {
        get
        {
            lock (ConfigLock)
            {
                if (_currentConfig is null)
                {
                    SetConfig(LoadConfig());
                }
                return _currentConfig!;
            }
        }
        set
        {
            SetConfig(value);
        }
    }

    /// <summary>
    /// Sets the active scope rules configuration and compiles its pattern matchers.
    /// </summary>
    public static void SetConfig(ScopeRulesConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (ConfigLock)
        {
            _currentConfig = config;
            _compiledRules = config.Scopes.Select(s => new CompiledRule(s)).ToList();
        }
    }

    /// <summary>
    /// Resets the current configuration cache so the next access reloads from disk or defaults.
    /// </summary>
    public static void ResetConfig()
    {
        lock (ConfigLock)
        {
            _currentConfig = null;
            _compiledRules = null;
        }
    }

    /// <summary>
    /// Searches known filesystem locations for <c>scope-rules.json</c>.
    /// </summary>
    public static string? FindScopeRulesFile(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        // 1. AppContext.BaseDirectory / data / scope-rules.json
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            var p = Path.Combine(baseDir, "data", "scope-rules.json");
            if (File.Exists(p)) return p;
        }

        // 2. Directory.GetCurrentDirectory() / data / scope-rules.json
        var curDir = Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(curDir))
        {
            var p = Path.Combine(curDir, "data", "scope-rules.json");
            if (File.Exists(p)) return p;
        }

        // 3. Walk up from baseDir
        var probeDir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 5 && probeDir != null; i++)
        {
            var p = Path.Combine(probeDir.FullName, "data", "scope-rules.json");
            if (File.Exists(p)) return p;
            probeDir = probeDir.Parent;
        }

        // 4. Walk up from curDir
        probeDir = new DirectoryInfo(curDir);
        for (int i = 0; i < 5 && probeDir != null; i++)
        {
            var p = Path.Combine(probeDir.FullName, "data", "scope-rules.json");
            if (File.Exists(p)) return p;
            probeDir = probeDir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Loads the scope configuration from disk or returns built-in default rules.
    /// </summary>
    public static ScopeRulesConfig LoadConfig(string? explicitPath = null)
    {
        try
        {
            var filePath = FindScopeRulesFile(explicitPath);
            if (filePath is not null && File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var config = JsonSerializer.Deserialize<ScopeRulesConfig>(json, JsonOptions);
                if (config is not null && config.Scopes.Count > 0)
                {
                    return config;
                }
            }
        }
        catch
        {
            // Fall back to built-in defaults
        }

        return GetDefaultConfig();
    }

    /// <summary>
    /// Returns default out-of-the-box scope rules.
    /// </summary>
    public static ScopeRulesConfig GetDefaultConfig() => new()
    {
        Scopes =
        [
            new ScopeRule
            {
                Silo = "product-catalog",
                TriggerPhrases =
                [
                    "из каталога",
                    "в каталоге",
                    "по каталогу",
                    "товары из каталога",
                    "товар из каталога"
                ],
                TriggerTerms =
                [
                    "товар",
                    "гарантия",
                    "прайс",
                    "артикул",
                    "остаток",
                    "склад",
                    "скидка",
                    "цена",
                    "стоимость",
                    "дороже",
                    "дешевле"
                ],
                EntityPatterns =
                [
                    @"TM-\d+",
                    @"PR-\d+",
                    @"LV-\d+",
                    @"AI-\d+X?"
                ]
            }
        ]
    };

    /// <summary>
    /// Resolves explicit silo scope from the query text, or returns null if the query is generic.
    /// </summary>
    public static MetadataFilter? ResolveScope(string? query, ScopeRulesConfig? config = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        IReadOnlyList<CompiledRule> rules;
        if (config is not null)
        {
            rules = config.Scopes.Select(s => new CompiledRule(s)).ToList();
        }
        else
        {
            lock (ConfigLock)
            {
                if (_compiledRules is null)
                {
                    SetConfig(LoadConfig());
                }
                rules = _compiledRules!;
            }
        }

        foreach (var rule in rules)
        {
            if (rule.Matches(query))
            {
                return rule.Filter;
            }
        }

        return null;
    }

    private static MetadataFilter CreateSiloFilter(string silo)
    {
        var p = Expression.Parameter(typeof(DocumentMetadata), "m");
        var prop = Expression.Property(p, nameof(DocumentMetadata.Silo));
        var constVal = Expression.Constant(silo, typeof(string));
        var eq = Expression.Equal(prop, constVal);
        var lambda = Expression.Lambda<Func<DocumentMetadata, bool>>(eq, p);
        return new MetadataFilter(lambda, new ComparisonFilterNode(nameof(DocumentMetadata.Silo), ComparisonOperator.Equal, silo));
    }

    private sealed class CompiledRule
    {
        public string Silo { get; }
        public MetadataFilter Filter { get; }
        private readonly List<Regex> _triggerPhraseRegexes = [];
        private readonly Regex? _entityRegex;
        private readonly Regex? _triggerTermsRegex;
        private readonly Regex _productWordRegex;
        private readonly Regex? _standaloneScopeRegex;

        public CompiledRule(ScopeRule rule)
        {
            ArgumentNullException.ThrowIfNull(rule);
            Silo = rule.Silo;
            Filter = CreateSiloFilter(rule.Silo);

            // 1. Trigger phrases (e.g. "из каталога", "по каталогу")
            foreach (var phrase in rule.TriggerPhrases)
            {
                if (string.IsNullOrWhiteSpace(phrase)) continue;
                var words = phrase.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var pattern = @"\b" + string.Join(@"\s+", words.Select(w => Regex.Escape(StemWord(w)) + @"\w*")) + @"\b";
                _triggerPhraseRegexes.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled));
            }

            // 2. Entity patterns (e.g. TM-\d+, PR-\d+, LV-\d+, AI-\d+X?)
            if (rule.EntityPatterns.Count > 0)
            {
                var validPatterns = rule.EntityPatterns.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                if (validPatterns.Count > 0)
                {
                    var combined = string.Join("|", validPatterns);
                    _entityRegex = new Regex(@"\b(?:" + combined + @")\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                }
            }

            // 3. Trigger terms (e.g. "товар", "гарантия", "прайс", "артикул", "остаток", "склад", "скидка", "цена", etc.)
            var stems = rule.TriggerTerms
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => StemWord(t))
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Also include commercial action terms in trigger terms
            var commercialActionTerms = new[] { "купи", "заказ", "наличи", "стои" };
            foreach (var cat in commercialActionTerms)
            {
                if (!stems.Contains(cat, StringComparer.OrdinalIgnoreCase))
                {
                    stems.Add(cat);
                }
            }

            if (stems.Count > 0)
            {
                var stemsCombined = string.Join("|", stems.Select(Regex.Escape));
                _triggerTermsRegex = new Regex(@"\b(?:" + stemsCombined + @")\w*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }

            // 4. Product words
            _productWordRegex = new Regex(@"\b(?:товар\w*|модел\w*|оборудовани\w*|устройств\w*)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            // 5. Standalone terms: warehouse stock and discounts
            var standaloneList = new List<string>();
            if (rule.TriggerTerms.Any(t => t.Contains("склад", StringComparison.OrdinalIgnoreCase)))
            {
                standaloneList.Add(@"склад\w*");
            }
            if (rule.TriggerTerms.Any(t => t.Contains("скидк", StringComparison.OrdinalIgnoreCase)))
            {
                standaloneList.Add(@"скидк\w*");
            }

            if (standaloneList.Count > 0)
            {
                _standaloneScopeRegex = new Regex(@"\b(?:" + string.Join("|", standaloneList) + @")\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
        }

        public bool Matches(string query)
        {
            // 1. Explicit trigger phrases (e.g. "из каталога", "по каталогу", "в каталоге")
            foreach (var phraseRegex in _triggerPhraseRegexes)
            {
                if (phraseRegex.IsMatch(query))
                {
                    return true;
                }
            }

            // 2. Standalone silo triggers (e.g. "на складе", "скидка")
            if (_standaloneScopeRegex is not null && _standaloneScopeRegex.IsMatch(query))
            {
                return true;
            }

            // 3. Product word + catalog attribute/trigger term (e.g. "товар ... гарантия", "модель ... цена")
            if (_productWordRegex.IsMatch(query) && _triggerTermsRegex is not null && _triggerTermsRegex.IsMatch(query))
            {
                return true;
            }

            // 4. Entity pattern + trigger term (e.g. "TM-200 ... цена", "AI-8X ... гарантия", "PR-25 ... цена")
            if (_entityRegex is not null && _entityRegex.IsMatch(query) &&
                _triggerTermsRegex is not null && _triggerTermsRegex.IsMatch(query))
            {
                return true;
            }

            return false;
        }

        private static string StemWord(string w)
        {
            var trimmed = w.Trim().ToLowerInvariant();
            if (trimmed.Length <= 3) return trimmed;
            while (trimmed.Length > 3 && (
                trimmed.EndsWith('а') || trimmed.EndsWith('е') || trimmed.EndsWith('и') ||
                trimmed.EndsWith('о') || trimmed.EndsWith('у') || trimmed.EndsWith('ы') ||
                trimmed.EndsWith('я') || trimmed.EndsWith('й') || trimmed.EndsWith('ь')))
            {
                trimmed = trimmed[..^1];
            }
            return trimmed;
        }
    }
}
