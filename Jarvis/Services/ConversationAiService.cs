using System.Text;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Jarvis.Configuration;

namespace Jarvis.Services;

/// <summary>
/// Свободный голосовой разговор с Джарвисом: обсуждение любых тем с памятью о предыдущих репликах.
/// Основной «мозг» — Claude через Anthropic API. Если ключ API не задан, используется локальная модель Ollama.
/// Ответ приходит потоком и отдаётся наружу по предложениям, чтобы Джарвис начинал говорить сразу,
/// не дожидаясь конца всего ответа.
/// </summary>
public partial class ConversationAiService {
    private readonly ILogger<ConversationAiService> _logger;
    private readonly ConversationSettings _settings;
    private readonly Kernel _kernel;
    private readonly AnthropicClient? _claude;
    private readonly List<(bool IsUser, string Text)> _history = [];
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private const string SYSTEM_PROMPT = """
        Ты — Джарвис, личный голосовой ассистент и собеседник. Сейчас ты в режиме живого разговора:
        пользователь говорит с тобой голосом, а твой ответ зачитывает синтезатор речи.

        Как вести разговор:
        - Говори по-русски, естественно и по-дружески, как умный собеседник, а не как справочник.
        - Отвечай коротко: обычно 2–5 предложений. Развёрнуто — только если пользователь просит рассказать подробно.
        - Делись своим мнением и аргументами, задавай встречные вопросы, чтобы обсуждение продолжалось.
        - Если не расслышал или фраза бессмысленна (речь распознаётся автоматически и бывает с ошибками),
          попробуй догадаться по смыслу или переспроси.
        - Будь честен: если чего-то не знаешь или не уверен, так и скажи.

        Формат (ответ озвучивается):
        - Никакого Markdown, списков, заголовков, эмодзи, ссылок и кода.
        - Избегай длинных чисел, формул и аббревиатур, которые трудно произнести; пиши так, как это звучит вслух.
        """;

    public ConversationAiService(Kernel kernel, IOptions<ConversationSettings> settings, ILogger<ConversationAiService> logger) {
        _logger = logger;
        _settings = settings.Value;
        _kernel = kernel;

        var apiKey = !string.IsNullOrWhiteSpace(_settings.ApiKey)
            ? _settings.ApiKey
            : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        if (!string.IsNullOrWhiteSpace(apiKey)) {
            _claude = new AnthropicClient { ApiKey = apiKey };
            _logger.LogInformation($"Разговор: используется Claude ({_settings.ModelId})");
        }
        else {
            _logger.LogWarning("Разговор: ключ ANTHROPIC_API_KEY не найден, используется локальная модель Ollama");
        }
    }

    public bool UsesClaude => _claude != null;

    public bool IsStartPhrase(string text) => ContainsAny(text, _settings.StartPhrases, maxExtraWords: int.MaxValue);

    // Для выхода фраза должна быть короткой: «хватит» — выход, а «хватит ли денег на отпуск» — вопрос собеседнику.
    public bool IsStopPhrase(string text) => ContainsAny(text, _settings.StopPhrases, maxExtraWords: 2);

    public TimeSpan SilenceTimeout => TimeSpan.FromSeconds(Math.Max(5, _settings.SilenceTimeoutSeconds));

    /// <summary>Начать новый разговор с чистой историей.</summary>
    public void Reset() {
        lock (_history) _history.Clear();
    }

    /// <summary>
    /// Отправляет реплику пользователя и вызывает <paramref name="onSentence"/> для каждого готового предложения ответа.
    /// Возвращает полный текст ответа.
    /// </summary>
    public async Task<string> ReplyAsync(string userText, Func<string, Task> onSentence, CancellationToken cancellationToken = default) {
        await _semaphore.WaitAsync(cancellationToken);
        try {
            lock (_history) _history.Add((true, userText));

            var splitter = new SentenceSplitter(onSentence);
            string reply;
            try {
                reply = _claude != null
                    ? await StreamFromClaudeAsync(splitter, cancellationToken)
                    : await StreamFromOllamaAsync(splitter, cancellationToken);
                await splitter.FlushAsync();
            }
            catch {
                // Убираем реплику без ответа, чтобы роли в истории чередовались.
                lock (_history) _history.RemoveAt(_history.Count - 1);
                throw;
            }

            reply = CleanForSpeech(reply);
            if (string.IsNullOrWhiteSpace(reply)) {
                reply = "Извини, мне нечего на это ответить. Давай сменим тему?";
                await onSentence(reply);
            }

            lock (_history) {
                _history.Add((false, reply));
                TrimHistory();
            }

            _logger.LogInformation($"Разговор: ответ — {reply}");
            return reply;
        }
        finally {
            _semaphore.Release();
        }
    }

    private async Task<string> StreamFromClaudeAsync(SentenceSplitter splitter, CancellationToken cancellationToken) {
        var parameters = new MessageCreateParams {
            Model = _settings.ModelId,
            MaxTokens = _settings.MaxTokens,
            System = $"{SYSTEM_PROMPT}\nТекущая дата: {DateTime.Now:D}.",
            OutputConfig = new OutputConfig { Effort = ParseEffort(_settings.Effort) },
            Messages = BuildClaudeMessages(),
        };

        var full = new StringBuilder();
        bool refused = false;

        await foreach (var streamEvent in _claude!.Messages.CreateStreaming(parameters, cancellationToken)) {
            if (streamEvent.TryPickContentBlockDelta(out var blockDelta) && blockDelta.Delta.TryPickText(out var text)) {
                full.Append(text.Text);
                await splitter.AppendAsync(text.Text);
            }
            else if (streamEvent.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason == StopReason.Refusal) {
                refused = true;
            }
        }

        if (refused && full.Length == 0) {
            const string refusal = "Извини, эту тему я обсуждать не могу. Давай поговорим о чём-нибудь другом.";
            await splitter.AppendAsync(refusal);
            return refusal;
        }

        return full.ToString();
    }

    private async Task<string> StreamFromOllamaAsync(SentenceSplitter splitter, CancellationToken cancellationToken) {
        var chat = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory($"{SYSTEM_PROMPT}\nТекущая дата: {DateTime.Now:D}.");
        lock (_history) {
            foreach (var (isUser, text) in _history) {
                if (isUser) history.AddUserMessage(text);
                else history.AddAssistantMessage(text);
            }
        }

        var full = new StringBuilder();
        await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, kernel: _kernel, cancellationToken: cancellationToken)) {
            if (string.IsNullOrEmpty(chunk.Content)) continue;
            full.Append(chunk.Content);
            await splitter.AppendAsync(chunk.Content);
        }

        // У «думающих» локальных моделей вырезаем рассуждения.
        return ThinkTagRegex().Replace(full.ToString(), string.Empty);
    }

    private List<MessageParam> BuildClaudeMessages() {
        lock (_history) {
            return [.. _history.Select(m => m.IsUser
                ? (MessageParam)new UserMessageParam { Content = m.Text }
                : new AssistantMessageParam { Content = m.Text })];
        }
    }

    private void TrimHistory() {
        int max = Math.Max(2, _settings.MaxHistoryMessages);
        // Удаляем парами (реплика + ответ), чтобы история всегда начиналась с пользователя.
        while (_history.Count > max)
            _history.RemoveRange(0, Math.Min(2, _history.Count));
    }

    private static Effort ParseEffort(string value) => value?.Trim().ToLowerInvariant() switch {
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Low,
    };

    private static bool ContainsAny(string text, IEnumerable<string> phrases, int maxExtraWords) {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return false;
        int words = normalized.Split(' ').Length;
        return phrases.Any(p => {
            var phrase = Normalize(p);
            return phrase.Length > 0
                && $" {normalized} ".Contains($" {phrase} ")
                && words - phrase.Split(' ').Length <= maxExtraWords;
        });
    }

    private static string Normalize(string text) =>
        WhitespaceRegex().Replace((text ?? string.Empty).ToLowerInvariant().Replace('ё', 'е'), " ").Trim();

    /// <summary>Убирает разметку, которую синтезатор прочитал бы вслух.</summary>
    public static string CleanForSpeech(string text) {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        text = ThinkTagRegex().Replace(text, string.Empty);
        text = MarkdownRegex().Replace(text, string.Empty);
        text = BulletRegex().Replace(text, string.Empty);
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline)]
    private static partial Regex ThinkTagRegex();

    [GeneratedRegex(@"[*_#`~>|]")]
    private static partial Regex MarkdownRegex();

    [GeneratedRegex(@"(?m)^\s*(\d+[.)]|[-•])\s+")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>Накапливает поток текста и отдаёт его законченными предложениями.</summary>
    private sealed class SentenceSplitter(Func<string, Task> onSentence) {
        private const int MIN_CHUNK_LENGTH = 40;
        private readonly StringBuilder _buffer = new();
        private bool _insideThink;

        public async Task AppendAsync(string text) {
            _buffer.Append(text);

            // Не озвучиваем рассуждения локальных моделей (<think>...</think>).
            var current = _buffer.ToString();
            if (!_insideThink && current.Contains("<think>")) _insideThink = true;
            if (_insideThink) {
                int end = current.IndexOf("</think>", StringComparison.Ordinal);
                if (end < 0) return;
                _buffer.Remove(0, end + "</think>".Length);
                _insideThink = false;
            }

            while (true) {
                int cut = FindSentenceEnd(_buffer.ToString());
                if (cut < 0) break;
                var sentence = _buffer.ToString(0, cut + 1);
                _buffer.Remove(0, cut + 1);
                await EmitAsync(sentence);
            }
        }

        public async Task FlushAsync() {
            if (_insideThink) { _buffer.Clear(); return; }
            await EmitAsync(_buffer.ToString());
            _buffer.Clear();
        }

        private async Task EmitAsync(string sentence) {
            var clean = CleanForSpeech(sentence);
            if (clean.Any(char.IsLetterOrDigit))
                await onSentence(clean);
        }

        // Ищем конец предложения (. ! ? … или перевод строки), за которым идёт пробел,
        // и только если накопилось достаточно текста — так меньше пауз между кусками.
        private static int FindSentenceEnd(string text) {
            for (int i = MIN_CHUNK_LENGTH; i < text.Length - 1; i++) {
                char c = text[i];
                if (c == '\n' || ((c is '.' or '!' or '?' or '…' or ';') && char.IsWhiteSpace(text[i + 1])))
                    return i;
            }
            return -1;
        }
    }
}
