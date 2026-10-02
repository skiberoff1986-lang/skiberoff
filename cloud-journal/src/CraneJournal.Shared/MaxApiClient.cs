using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace EquipmentDowntime.Core;

/// <summary>Только исходящий HTTPS. Webhook и постоянный опрос не используются.</summary>
public sealed partial class MaxApiClient
{
    private readonly HttpClient http;
    private readonly HttpClient uploadHttp;

    public MaxApiClient(HttpClient http, HttpClient? uploadHttp = null)
    {
        this.http = http;
        this.uploadHttp = uploadHttp ?? http;
    }

    private const string Endpoint = "https://platform-api2.max.ru/";
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private DateTimeOffset nextSendAt;

    public static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Введите токен бота MAX без пробелов и переносов строк.");
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        ValidateToken(token);
        var request = new HttpRequestMessage(method, Endpoint + path);
        // MAX принимает непрозрачный токен без схемы Bearer. Он может содержать
        // символы, которые парсер стандартной схемы Authorization не принимает.
        // Переносы строк и пробелы уже отклонены ValidateToken.
        request.Headers.TryAddWithoutValidation("Authorization", token);
        return request;
    }

    public async Task<MaxSendResult> SendAsync(string token, long chatId, string text, CancellationToken cancellationToken = default,
        MediaKind? mediaKind = null, string? mediaToken = null)
    {
        ValidateToken(token);
        if (chatId == 0 || string.IsNullOrWhiteSpace(text) || text.Length > 4000)
            throw new ArgumentException("Проверьте ID группы и длину сообщения MAX.");
        if (mediaKind.HasValue && (!Enum.IsDefined(mediaKind.Value) || string.IsNullOrWhiteSpace(mediaToken) || mediaToken.Length > 16384))
            throw new ArgumentException("Отсутствует токен загруженного вложения.");
        await sendGate.WaitAsync(cancellationToken);
        bool attempted = false;
        try
        {
            var wait = nextSendAt - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            using var request = Request(HttpMethod.Post, "messages?chat_id=" + chatId.ToString(CultureInfo.InvariantCulture), token);
            // Обычный текст: введённая сотрудником разметка не выполняется.
            request.Content = mediaKind.HasValue
                ? JsonContent.Create(new { text, notify = true, attachments = new[]
                    { new { type = mediaKind == MediaKind.Image ? "image" : "video", payload = new { token = mediaToken } } } })
                : JsonContent.Create(new { text, notify = true });
            attempted = true;
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(60);
                if (retry < TimeSpan.FromSeconds(1)) retry = TimeSpan.FromSeconds(1);
                if (retry > TimeSpan.FromDays(7)) retry = TimeSpan.FromDays(7);
                return new(MaxDeliveryState.Pending, Error: "MAX ограничил частоту отправки. Повтор запланирован.", RetryAfter: retry);
            }
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                return Uncertain("MAX вернул временную ошибку; приём сообщения не подтверждён.");
            if (!response.IsSuccessStatusCode)
            {
                if (mediaKind.HasValue && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
                {
                    try
                    {
                        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                        if (error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty("code", out var code) &&
                            code.ValueKind == JsonValueKind.String && code.GetString() == "attachment.not.ready")
                            return new(MaxDeliveryState.Pending, Error: "MAX ещё обрабатывает вложение. Повтор запланирован.");
                    }
                    catch (JsonException) { /* Отклонённый запрос не был принят; сохраняем понятную ошибку HTTP. */ }
                }
                return new(MaxDeliveryState.Blocked, Error: ErrorFor(response.StatusCode));
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.TryGetProperty("message", out var message) &&
                message.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object &&
                body.TryGetProperty("mid", out var mid) && mid.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(mid.GetString()))
                return new(MaxDeliveryState.Sent, MessageId: mid.GetString()!);
            return Uncertain("MAX ответил, но не вернул идентификатор отправленного сообщения.");
        }
        catch (HttpRequestException ex) when (DefinitelyNotConnected(ex))
        {
            return new(MaxDeliveryState.Pending, Error: "Нет соединения с MAX. Программа повторит отправку автоматически.");
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return new(MaxDeliveryState.Blocked, Error: "Не удалось проверить защищённое соединение MAX. Проверьте время сервера и доверенные сертификаты.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        {
            return attempted ? Uncertain("Ответ MAX не получен. Сообщение могло быть доставлено.")
                : new(MaxDeliveryState.Pending, Error: "Отправка прервана до начала запроса.");
        }
        finally
        {
            nextSendAt = DateTimeOffset.UtcNow.AddMilliseconds(600);
            sendGate.Release();
        }
    }

    public async Task<MaxGroup> GetGroupAsync(string token, long chatId, CancellationToken cancellationToken = default)
    {
        using var json = await GetAsync(token, "chats/" + chatId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        var root = json.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "chat")
            throw new InvalidOperationException("Выберите общую группу MAX. Личный диалог и канал здесь не поддерживаются.");
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "active")
            throw new InvalidOperationException("Бот не является активным участником этой группы.");
        if (!root.TryGetProperty("chat_id", out var id) || !id.TryGetInt64(out var actualId) || actualId != chatId)
            throw new InvalidOperationException("MAX вернул другую группу. Проверьте ID.");
        string title = root.TryGetProperty("title", out var name) ? name.GetString() ?? "Группа MAX" : "Группа MAX";
        return new(chatId, title);
    }

    // Однократное получение события добавления бота только при первоначальной настройке.
    public async Task<IReadOnlyList<MaxGroup>> FindSetupGroupsAsync(string token, CancellationToken cancellationToken = default)
    {
        using var json = await GetAsync(token, "updates?types=bot_added&timeout=0&limit=100", cancellationToken);
        var groups = new List<MaxGroup>();
        if (!json.RootElement.TryGetProperty("updates", out var updates) || updates.ValueKind != JsonValueKind.Array)
            return groups;
        var ids = new HashSet<long>();
        foreach (var update in updates.EnumerateArray())
        {
            if (!update.TryGetProperty("update_type", out var kind) || kind.GetString() != "bot_added" ||
                !update.TryGetProperty("chat_id", out var id) || !id.TryGetInt64(out var chatId) || chatId == 0 ||
                (update.TryGetProperty("is_channel", out var channel) && channel.ValueKind == JsonValueKind.True) || !ids.Add(chatId)) continue;
            // Не выбираем первую найденную группу автоматически: пользователь сверяет название.
            try { groups.Add(await GetGroupAsync(token, chatId, cancellationToken)); }
            catch (InvalidOperationException) { /* Старая/удалённая группа не предлагается. */ }
            if (groups.Count == 20) break;
        }
        return groups;
    }

    private async Task<JsonDocument> GetAsync(string token, string path, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, path, token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ErrorFor(response.StatusCode));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static bool DefinitelyNotConnected(HttpRequestException ex) =>
        ex.HttpRequestError == HttpRequestError.NameResolutionError ||
        (ex.InnerException is SocketException socket && socket.SocketErrorCode is
            SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.HostUnreachable or
            SocketError.HostNotFound or SocketError.ConnectionRefused);

    private static MaxSendResult Uncertain(string error) => new(MaxDeliveryState.Uncertain,
        Error: error + " Проверьте группу перед повторной отправкой.");

    private static string ErrorFor(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "MAX отклонил токен. Проверьте токен бота.",
        HttpStatusCode.Forbidden => "Нет права отправлять в группу MAX. Проверьте участие и права бота.",
        HttpStatusCode.NotFound => "Группа MAX не найдена. Проверьте её ID и участие бота.",
        HttpStatusCode.TooManyRequests => "Слишком много запросов MAX. Повторите проверку позже.",
        HttpStatusCode.Conflict or HttpStatusCode.MethodNotAllowed => "MAX отклонил операцию. Для поиска группы используйте отдельного бота без подписки Webhook или введите известный ID вручную.",
        _ => $"MAX отклонил запрос (HTTP {(int)status}). Проверьте настройки и права бота."
    };
}
