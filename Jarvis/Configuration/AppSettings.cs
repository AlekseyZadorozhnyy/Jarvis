namespace Jarvis.Configuration;

public class AISettings {
    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string ModelId { get; set; } = "qwen2.5:7b";
    public string ApiKey { get; set; } = "dummy";
    public string EmbeddingEndpoint { get; set; } = "http://localhost:11434";
    public string EmbeddingModelId { get; set; } = "qwen3-embedding:4b";
}

public class STTSettings {
    public string SttModelPath { get; set; } = "Models\\vosk-model-ru-0.42";
    public int SttSampleRate { get; set; } = 16000;
}

public class TTSSettings {
    public string VoiceName { get; set; } = "Pavel";
    public string InstallerFileName { get; set; } = "RHVoice-voice-Russian-Pavel-v4.0.2017.22-setup.exe";
    public string InstallerSubPath { get; set; } = "Resources";
    public int TtsRate { get; set; } = 1;
    public int TtsVolume { get; set; } = 75;
}

public class ConversationSettings {
    // Ключ Anthropic API. Лучше задать через переменную окружения ANTHROPIC_API_KEY,
    // чтобы случайно не закоммитить его в репозиторий.
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = "claude-opus-5-5";
    // low / medium / high — чем выше, тем глубже думает, но дольше отвечает.
    public string Effort { get; set; } = "low";
    public int MaxTokens { get; set; } = 2048;
    // Сколько последних сообщений (реплик пользователя и Джарвиса) помнить в разговоре.
    public int MaxHistoryMessages { get; set; } = 40;
    // Сколько секунд ждать следующую фразу, прежде чем выйти из режима разговора.
    public int SilenceTimeoutSeconds { get; set; } = 25;
    public List<string> StartPhrases { get; set; } = ["давай поговорим", "поговори со мной", "режим разговора", "давай обсудим"];
    public List<string> StopPhrases { get; set; } = ["хватит", "закончим разговор", "конец разговора", "выйди из режима разговора", "стоп"];
}
