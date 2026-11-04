using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Jarvis.Services;

namespace Jarvis.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable {
    [ObservableProperty] private string _state = "SLEEP";
    [ObservableProperty] private bool _isSpeaking = false; // используется для речевого синтезатора

    private readonly SpeechToTextService _speechToTextService;
    private readonly CommunicationAiService _communicationAiService;
    private readonly TextToSpeechService _textToSpeechService;
    private readonly ConversationAiService _conversationAiService;
    private readonly TrayService _trayService;
    private readonly ILogger<MainViewModel> _logger;

    public MainViewModel(
        SpeechToTextService speechToTextService,
        CommunicationAiService communicationAiService,
        TextToSpeechService textToSpeechService,
        ConversationAiService conversationAiService,
        TrayService trayService,
        ILogger<MainViewModel> logger) {
        _speechToTextService = speechToTextService;
        _communicationAiService = communicationAiService;
        _textToSpeechService = textToSpeechService;
        _conversationAiService = conversationAiService;
        _trayService = trayService;
        _logger = logger;

        _textToSpeechService.OnStartedSpeaking += () => IsSpeaking = true; // начал говорить
        _textToSpeechService.OnFinishedSpeaking += () => IsSpeaking = false; // закончил говорить 

        _speechToTextService.OnSpeechRecognized += async (text) => {
            if (string.IsNullOrWhiteSpace(text)) return;

            try {
                // Режим разговора: всё сказанное идёт собеседнику, а не в командный агент
                if (_speechToTextService.IsInConversation) {
                    await ContinueConversationAsync(text);
                    return;
                }

                if (_conversationAiService.IsStartPhrase(text)) {
                    await StartConversationAsync(text);
                    return;
                }

                var response = await _communicationAiService.GetRequestUser(text);

                if (!string.IsNullOrEmpty(response)) {
                    await _textToSpeechService.SpeakAsync(response);
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Ошибка обработки фразы: {Text}", text);
            }
        }; // обрабатывает/отправляет/озвучивает

        _speechToTextService.OnConversationTimeout += async () => {
            try {
                await _textToSpeechService.SpeakAsync("Что-то ты затих. Если захочешь продолжить, просто позови меня.");
                _trayService.HideOverlayAfterCommand();
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Ошибка при завершении разговора по таймауту");
            }
        }; // разговор закончился по тишине

        _speechToTextService.OnWakeUp += (text) => State = text; // проснулся
        _speechToTextService.OnProcessingText += (text) => State = text; // слушает
        _speechToTextService.OnTimeout += async (text) => {
            State = text;
            await Task.Delay(2000);
            State = "SLEEP";
        }; // засыпает

        _communicationAiService.OnExecute += (text) => State = text; // выполняет
        _communicationAiService.OnResult += (text) => State = text; // выполнил

        _speechToTextService.Start();
    }

    #region Режим разговора

    private async Task StartConversationAsync(string text) {
        // Важно включить режим до первого await: распознаватель сразу после этого события решает, засыпать ли ему
        _speechToTextService.EnterConversationMode(_conversationAiService.SilenceTimeout);
        _conversationAiService.Reset();
        _trayService.ShowConversationOverlay("Jarvis - Режим разговора");
        State = "TALK";
        _logger.LogInformation("Разговор начат фразой: {Text}", text);

        // «Давай обсудим космос» — сразу отвечаем по теме, «давай поговорим» — просто здороваемся
        if (HasTopicBeyondStartPhrase(text)) {
            await ReplyInConversationAsync(text);
            return;
        }

        var greeting = _conversationAiService.UsesClaude
            ? "Давай поговорим. О чём хочешь?"
            : "Давай поговорим. Ключ для Claude не найден, поэтому отвечать буду локальной моделью. О чём хочешь?";
        await _textToSpeechService.SpeakAsync(greeting);
        _speechToTextService.ResumeListening();
    }

    private async Task ContinueConversationAsync(string text) {
        if (_conversationAiService.IsStopPhrase(text)) {
            _speechToTextService.ExitConversationMode();
            await _textToSpeechService.SpeakAsync("Хорошо, было интересно поговорить. Если что, зови.");
            _trayService.HideOverlayAfterCommand();
            return;
        }

        await ReplyInConversationAsync(text);
    }

    private async Task ReplyInConversationAsync(string text) {
        State = "THINKING";
        try {
            await _conversationAiService.ReplyAsync(text, sentence => {
                State = "TALK";
                return _textToSpeechService.SpeakAsync(sentence);
            });
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Разговор: ошибка запроса к нейросети");
            await _textToSpeechService.SpeakAsync("Не получилось связаться с нейросетью. Проверь интернет и ключ API и попробуй ещё раз.");
        }
        finally {
            // Ответ озвучен — снова слушаем без слова «Джарвис»
            _speechToTextService.ResumeListening();
        }
    }

    private static bool HasTopicBeyondStartPhrase(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3;

    #endregion

    private void OnRestartRequired(object? sender, EventArgs e) {
        Application.Current.Dispatcher.Invoke(() => {
            var result = MessageBox.Show(
                "Для улучшения качества голоса был установлен дополнительный компонент.\n\n" +
                "Пожалуйста, перезапустите приложение, чтобы изменения вступили в силу.",
                "Требуется перезапуск",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        });
    }

    public void Dispose() {
        _speechToTextService?.Dispose();
        _communicationAiService?.Dispose();
        _textToSpeechService?.Dispose();
    }
}