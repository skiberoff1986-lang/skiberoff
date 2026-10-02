using CraneJournal.Web.Data;
using EquipmentDowntime.Core;
using Microsoft.EntityFrameworkCore;

namespace CraneJournal.Web.Services;

public sealed class OutboxWorker(IServiceScopeFactory scopes, MaxOptions options, MaxConnectionStatus connection,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1_048_576 };
        using var uploadHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromMinutes(10), MaxResponseContentBufferSize = 1_048_576 };
        var api = new MaxApiClient(http, uploadHttp);
        bool groupVerified = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            int delaySeconds = 2;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                await Recover(db, stoppingToken);
                if (options.Enabled)
                {
                    if (!groupVerified)
                    {
                        await api.GetGroupAsync(options.Token, options.ChatId, stoppingToken);
                        groupVerified = true;
                        connection.Set(true, "Доступ к группе MAX проверен.");
                    }
                    var item = await Claim(db, options.ChatId, stoppingToken);
                    if (item is not null) await Deliver(db, api, item, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                delaySeconds = 60;
                if (!groupVerified) connection.Set(false, ex switch
                {
                    InvalidOperationException or ArgumentException => ex.Message,
                    HttpRequestException => "Нет соединения с MAX. Проверьте сеть, сертификат TLS и доступность API на сервере.",
                    OperationCanceledException => "MAX не ответил вовремя. Проверка будет повторена.",
                    _ => "Не удалось проверить группу MAX. Проверьте токен и ID группы в настройках сервера."
                });
                // Never log a signed upload URL, a token or a request body.
                logger.LogWarning("MAX worker paused after {ErrorType}; saved queue will be retried.", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
    public static async Task Recover(AppDb db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Outbox.Where(o => o.State == MaxDeliveryState.Uploading && o.LeaseUntilUtc < now)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.State, MaxDeliveryState.Pending)
                .SetProperty(o => o.Error, "Загрузка прервалась; файл будет загружен повторно.")
                .SetProperty(o => o.NextAttemptAtUtc, now).SetProperty(o => o.LeaseId, (Guid?)null)
                .SetProperty(o => o.LeaseUntilUtc, (DateTimeOffset?)null), ct);
        await db.Outbox.Where(o => o.State == MaxDeliveryState.Sending && o.LeaseUntilUtc < now)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.State, MaxDeliveryState.Uncertain)
                .SetProperty(o => o.Error, "Сервер перезапустился во время отправки. Проверьте группу перед повтором.")
                .SetProperty(o => o.LeaseId, (Guid?)null).SetProperty(o => o.LeaseUntilUtc, (DateTimeOffset?)null), ct);
        // Only incomplete uploads expire. Registered photos/videos are retained.
        var cutoff = now.AddDays(-1);
        await db.Media.Where(m => m.State == UploadState.Uploading && m.CreatedAtUtc < cutoff).ExecuteDeleteAsync(ct);
    }
    public static async Task<OutboxItem?> Claim(AppDb db, long chatId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var rows = await db.Outbox.FromSqlInterpolated($"""
            SELECT o.* FROM "Outbox" o
            WHERE o."ChatId"={chatId} AND o."State"=0 AND o."NextAttemptAtUtc"<={now}
              AND NOT EXISTS (SELECT 1 FROM "Outbox" p WHERE p."RecordId"=o."RecordId" AND p."ChatId"=o."ChatId"
                AND p."Sequence"<o."Sequence" AND p."State" NOT IN (2,5))
            ORDER BY o."Sequence" LIMIT 1 FOR UPDATE OF o SKIP LOCKED
            """).ToListAsync(ct);
        var item = rows.SingleOrDefault();
        if (item is not null)
        {
            item.State = item.MediaId.HasValue && item.MediaToken.Length == 0 ? MaxDeliveryState.Uploading : MaxDeliveryState.Sending;
            item.Attempts++; item.Error = ""; item.LeaseId = Guid.NewGuid(); item.LeaseUntilUtc = now.AddMinutes(12);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return item;
    }
    private async Task Deliver(AppDb db, MaxApiClient api, OutboxItem item, CancellationToken ct)
    {
        var media = item.MediaId is Guid mediaId ? await db.Media.AsNoTracking().SingleAsync(m => m.Id == mediaId, ct) : null;
        var now = DateTimeOffset.UtcNow;
        double backoff = Math.Min(900, 15 * Math.Pow(2, Math.Min(item.Attempts, 6)));
        string token = item.MediaToken, error = "", mid = "";
        DateTimeOffset? sent = null;
        MaxDeliveryState state;
        TimeSpan delay;
        if (item.State == MaxDeliveryState.Uploading && media is not null)
        {
            MaxUploadResult upload;
            try
            {
                await using var stream = await ChunkStream.Open(db.Database.GetConnectionString()!, media.Id, media.Size, ct);
                upload = await api.UploadAsync(options.Token, media.AsAttachment(), stream, ct);
            }
            catch (OperationCanceledException) { upload = new(Error: "Загрузка прервана; повтор запланирован."); }
            catch (IOException) { upload = new(Error: "Не удалось прочитать вложение. Проверьте хранилище.", Blocked: true); }
            token = upload.Token; error = upload.Error;
            state = upload.Blocked ? MaxDeliveryState.Blocked : MaxDeliveryState.Pending;
            delay = token.Length > 0 ? TimeSpan.FromSeconds(5) : upload.RetryAfter ?? TimeSpan.FromSeconds(backoff);
        }
        else
        {
            MaxSendResult result;
            try { result = await api.SendAsync(options.Token, item.ChatId, item.Text, ct, media?.Kind, media is null ? null : token); }
            catch (OperationCanceledException) { result = new(MaxDeliveryState.Pending, Error: "Отправка остановлена до начала запроса."); }
            state = result.State; error = result.Error; mid = result.MessageId;
            sent = state == MaxDeliveryState.Sent ? DateTimeOffset.UtcNow : null;
            delay = result.RetryAfter ?? TimeSpan.FromSeconds(backoff);
        }
        now = DateTimeOffset.UtcNow;
        var next = now + delay;
        // A late worker cannot overwrite another worker's result or a manual decision.
        int updated = await db.Outbox.Where(o => o.Id == item.Id && o.LeaseId == item.LeaseId && o.State == item.State)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.State, state).SetProperty(o => o.MediaToken, token)
                .SetProperty(o => o.Error, error).SetProperty(o => o.MessageId, mid).SetProperty(o => o.SentAtUtc, sent)
                .SetProperty(o => o.NextAttemptAtUtc, next).SetProperty(o => o.LeaseId, (Guid?)null)
                .SetProperty(o => o.LeaseUntilUtc, (DateTimeOffset?)null), CancellationToken.None);
        if (updated == 0) logger.LogWarning("MAX lease expired before its result could be stored. Manual review is required.");
    }
}
