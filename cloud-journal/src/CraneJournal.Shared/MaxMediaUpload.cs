using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace EquipmentDowntime.Core;

public sealed record MaxUploadResult(string Token = "", string Error = "", bool Blocked = false, TimeSpan? RetryAfter = null);

public sealed partial class MaxApiClient
{
    public async Task<MaxUploadResult> UploadAsync(string token, JournalAttachment attachment, Stream content,
        CancellationToken cancellationToken = default)
    {
        ValidateToken(token);
        MediaRules.ValidateMetadata(attachment);
        try
        {
            string type = attachment.Kind == MediaKind.Image ? "image" : "video";
            using var prepare = Request(HttpMethod.Post, "uploads?type=" + type, token);
            using var response = await http.SendAsync(prepare, cancellationToken);
            if (!response.IsSuccessStatusCode) return UploadFailure(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || !AllowedUploadUri(uri))
                return new(Error: "MAX вернул неподдерживаемый адрес загрузки. Проверьте обновление программы.", Blocked: true);
            string videoToken = root.TryGetProperty("token", out var preparedToken) && preparedToken.ValueKind == JsonValueKind.String
                ? preparedToken.GetString() ?? "" : "";
            if (attachment.Kind == MediaKind.Video && !ValidMediaToken(videoToken))
                return new(Error: "MAX не выдал токен видео. Повторите загрузку позже.");

            using var upload = new HttpRequestMessage(HttpMethod.Post, uri);
            // Официальный адрес приёма фото требует Authorization; прочие адреса уже подписаны.
            // Токен бота не пересылается произвольному серверу и не следует за перенаправлением.
            if (attachment.Kind == MediaKind.Image && uri.Host.Equals("iu.oneme.ru", StringComparison.OrdinalIgnoreCase))
                upload.Headers.TryAddWithoutValidation("Authorization", token);
            using var multipart = new MultipartFormDataContent();
            var file = new StreamContent(content, 81920);
            file.Headers.ContentLength = attachment.SizeBytes;
            file.Headers.ContentType = new MediaTypeHeaderValue(ContentType(attachment.OriginalFileName));
            // На сервер уходит безопасное имя, оригинальное русское имя остаётся в подписи.
            multipart.Add(file, "data", attachment.StoredFileName);
            upload.Content = multipart;
            using var uploaded = await uploadHttp.SendAsync(upload, cancellationToken);
            if (!uploaded.IsSuccessStatusCode) return UploadFailure(uploaded);
            string body = await uploaded.Content.ReadAsStringAsync(cancellationToken);
            if (attachment.Kind == MediaKind.Video)
            {
                using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
                var acknowledgement = XElement.Load(reader);
                if (acknowledgement.Name.LocalName != "retval" || acknowledgement.Value.Trim() != "1")
                    return new(Error: "MAX не подтвердил загрузку видео. Повтор запланирован.");
                return new(Token: videoToken);
            }
            using var image = JsonDocument.Parse(body);
            if (image.RootElement.ValueKind == JsonValueKind.Object && image.RootElement.TryGetProperty("photos", out var photos) &&
                photos.ValueKind == JsonValueKind.Object)
            {
                var entries = photos.EnumerateObject().ToList();
                if (entries.Count == 1 && entries[0].Value.ValueKind == JsonValueKind.Object &&
                    entries[0].Value.TryGetProperty("token", out var value) && value.ValueKind == JsonValueKind.String &&
                    ValidMediaToken(value.GetString())) return new(Token: value.GetString()!);
            }
            return new(Error: "MAX не вернул токен изображения. Повторите загрузку позже.");
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return new(Error: "Не удалось проверить защищённое соединение загрузки MAX. Проверьте время сервера и доверенные сертификаты.", Blocked: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException or XmlException or InvalidOperationException)
        {
            // Этот метод только загружает файл: сообщения в группе ещё нет, повтор безопасен.
            return new(Error: "Загрузка файла не подтверждена. Программа повторит её автоматически.");
        }
    }

    private static bool AllowedUploadUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        new[] { "oneme.ru", "okcdn.ru", "max.ru" }.Any(domain =>
            uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    private static bool ValidMediaToken(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 16384 && !value.Any(char.IsControl);

    private static MaxUploadResult UploadFailure(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter?.Delta ??
                (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMinutes(1);
            retry = TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 1, 604800));
            return new(Error: "MAX ограничил частоту загрузки. Повтор запланирован.", RetryAfter: retry);
        }
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
            return new(Error: "MAX временно не принимает файлы. Повтор запланирован.");
        return new(Error: response.StatusCode == HttpStatusCode.RequestEntityTooLarge
            ? "MAX отклонил размер файла. Уменьшите фото/видео."
            : $"Загрузка отклонена MAX (HTTP {(int)response.StatusCode}). Проверьте формат, токен и соединение.", Blocked: true);
    }

    private static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff", ".mp4" => "video/mp4", ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska", ".webm" => "video/webm", _ => "application/octet-stream"
    };
}
