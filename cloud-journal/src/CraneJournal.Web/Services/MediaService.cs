using System.Buffers.Binary;
using System.Security.Cryptography;
using CraneJournal.Web.Data;
using EquipmentDowntime.Core;
using Microsoft.EntityFrameworkCore;

namespace CraneJournal.Web.Services;

public sealed record StartUpload(Guid CommandId, string Name, long Size, bool NotifyMax = true, string? ExpectedSha256 = null);
public sealed record CompleteUpload(Guid CommandId);
public sealed record MediaResult(Guid Id, Guid RecordId, string Name, long Size, string State, int ChunkSize);

public sealed class MediaService(AppDb db, CommandRunner commands, RecordService records, BusinessClock clock,
    MaxOptions max, IConfiguration config)
{
    public const int ChunkSize = 2 * 1024 * 1024;
    public Task<string> Start(Guid recordId, Actor actor, StartUpload request) =>
        commands.Run(actor, request.CommandId, "media-start:" + recordId, request, recordId, async () =>
        {
            await records.Locked(recordId, actor);
            string name = request.Name?.Trim() ?? "";
            if (name.Length is 0 or > 255 || name.Any(char.IsControl) || name.IndexOfAny(['/', '\\', ':']) >= 0)
                throw new ApiError(400, "Недопустимое имя файла.");
            if (Path.GetExtension(name).Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(name).Equals(".tiff", StringComparison.OrdinalIgnoreCase))
                throw new ApiError(400, "Для веб-версии сохраните TIFF как JPG или PNG.");
            if (!request.NotifyMax && !actor.Admin) throw new ApiError(403, "Загрузку без уведомления при переносе выполняет администратор.");
            if (!string.IsNullOrEmpty(request.ExpectedSha256) && (request.ExpectedSha256.Length != 64 || !request.ExpectedSha256.All(Uri.IsHexDigit)))
                throw new ApiError(400, "Неверная контрольная сумма исходного файла.");
            var kind = MediaRules.KindFor(name); MediaRules.ValidateSize(kind, request.Size);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(831614073266)");
            long quota = long.TryParse(config["MEDIA_QUOTA_BYTES"], out var value) ? value : 500_000_000;
            long reserved = await db.Media.Where(m => m.State != UploadState.Duplicate).SumAsync(m => (long?)m.Size) ?? 0;
            if (request.Size > quota - reserved) throw new ApiError(413, "В хранилище недостаточно свободного места. Обратитесь к администратору.");
            var media = new MediaFile { Id = request.CommandId, RecordId = recordId, OwnerId = actor.Id, OriginalName = name,
                Kind = kind, ContentType = MimeType(name), Size = request.Size, CreatedAtUtc = clock.UtcNow,
                NotifyMax = request.NotifyMax, ExpectedSha256 = request.ExpectedSha256 ?? "" };
            db.Media.Add(media);
            return Result(media);
        });

    public async Task<MediaFile> Get(Guid id, Actor actor, bool write = false, bool owned = false)
    {
        var media = await db.Media.SingleOrDefaultAsync(m => m.Id == id) ?? throw new ApiError(404, "Файл не найден или незавершённая загрузка истекла.");
        var record = await db.Records.AsNoTracking().SingleAsync(r => r.Id == media.RecordId);
        AccessService.Shop(actor, record.Shop, write);
        if (owned && media.OwnerId != actor.Id) throw new ApiError(403, "Завершить загрузку может только добавивший её сотрудник.");
        return media;
    }
    public async Task PutChunk(Guid id, int index, byte[] data, Actor actor)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Media\" WHERE \"Id\"={id} FOR UPDATE");
        var media = await Get(id, actor, write: true, owned: true);
        int count = checked((int)((media.Size + ChunkSize - 1) / ChunkSize));
        if (index < 0 || index >= count || data.Length != Math.Min(ChunkSize, media.Size - (long)index * ChunkSize))
            throw new ApiError(400, "Неверный размер или номер части файла.");
        string hash = Convert.ToHexString(SHA256.HashData(data));
        var saved = await db.Chunks.FindAsync(id, index);
        if (saved is not null)
        {
            if (saved.Sha256 != hash) throw new ApiError(409, "Эта часть файла уже сохранена с другим содержимым.");
            await tx.CommitAsync(); return;
        }
        if (media.State != UploadState.Uploading) throw new ApiError(409, "Файл уже зарегистрирован и не может быть заменён.");
        db.Chunks.Add(new() { MediaId = id, Index = index, Data = data, Sha256 = hash });
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task<string> Complete(Guid id, Actor actor, CompleteUpload request)
    {
        var scope = await Get(id, actor, write: true, owned: true);
        return await commands.Run(actor, request.CommandId, "media-complete:" + id, request, scope.RecordId, async () =>
        {
            var record = await records.Locked(scope.RecordId, actor);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Media\" WHERE \"Id\"={id} FOR UPDATE");
            await db.Entry(scope).ReloadAsync();
            if (scope.State == UploadState.Ready) return Result(scope);
            if (scope.State == UploadState.Duplicate)
                return Result(await db.Media.AsNoTracking().SingleAsync(m => m.Id == scope.DuplicateOfId));
            int expected = checked((int)((scope.Size + ChunkSize - 1) / ChunkSize));
            var parts = await db.Chunks.AsNoTracking().Where(c => c.MediaId == id).OrderBy(c => c.Index)
                .Select(c => new { c.Index, c.Sha256 }).ToListAsync();
            if (parts.Count != expected || !parts.Select(c => c.Index).SequenceEqual(Enumerable.Range(0, expected)))
                throw new ApiError(409, "Файл загружен не полностью. Повторите отправку недостающих частей.");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int i = 0; i < expected; i++)
            {
                byte[] data = await db.Chunks.AsNoTracking().Where(c => c.MediaId == id && c.Index == i).Select(c => c.Data).SingleAsync();
                if (i == 0)
                {
                    MediaRules.ValidateSignature(scope.OriginalName, data.AsSpan(0, Math.Min(data.Length, 32)));
                    if (scope.Kind == MediaKind.Image) ImageDimensions.Validate(scope.OriginalName, data);
                }
                sha.AppendData(data);
            }
            scope.Sha256 = Convert.ToHexString(sha.GetHashAndReset());
            if (scope.ExpectedSha256.Length > 0 && !string.Equals(scope.ExpectedSha256, scope.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ApiError(409, "Контрольная сумма не совпадает с локальным журналом. Проверьте исходный файл.");
            var same = await db.Media.AsNoTracking().SingleOrDefaultAsync(m => m.RecordId == scope.RecordId &&
                m.State == UploadState.Ready && m.Sha256 == scope.Sha256);
            if (same is not null)
            {
                scope.State = UploadState.Duplicate; scope.DuplicateOfId = same.Id; scope.CompletedAtUtc = clock.UtcNow;
                await db.Chunks.Where(c => c.MediaId == scope.Id).ExecuteDeleteAsync();
                return Result(same);
            }
            scope.State = UploadState.Ready; scope.CompletedAtUtc = clock.UtcNow;
            records.AddEvent(record, actor, request.CommandId, "MediaAdded");
            if (max.Enabled && scope.NotifyMax) db.Outbox.Add(OutboxItem.From(MaxNotification.ForMedia(record.Read(), scope.AsAttachment(), max.ChatId, clock.UtcNow)));
            return Result(scope);
        });
    }

    public async Task<string> Queue(Guid id, Actor actor, CompleteUpload request)
    {
        var media = await Get(id, actor, write: true);
        return await commands.Run(actor, request.CommandId, "media-queue:" + id, request, media.RecordId, async () =>
        {
            var record = await records.Locked(media.RecordId, actor);
            if (media.State != UploadState.Ready) throw new ApiError(409, "Файл ещё не зарегистрирован.");
            if (!max.Enabled) throw new ApiError(409, "MAX пока не подключён администратором.");
            var previous = await db.Outbox.SingleOrDefaultAsync(o => o.MediaId == id);
            if (previous is not null) return new { notificationId = previous.Id, state = previous.State.ToString() };
            var item = OutboxItem.From(MaxNotification.ForMedia(record.Read(), media.AsAttachment(), max.ChatId, clock.UtcNow));
            db.Outbox.Add(item);
            records.AddEvent(record, actor, request.CommandId, "MaxQueued");
            return new { notificationId = item.Id, state = item.State.ToString() };
        });
    }
    public static MediaResult Result(MediaFile m) => new(m.Id, m.RecordId, m.OriginalName, m.Size, m.State.ToString(), ChunkSize);
    public static string MimeType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".bmp" => "image/bmp",
        ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".mkv" => "video/x-matroska", ".webm" => "video/webm",
        _ => throw new ApiError(400, "Неподдерживаемый формат файла.")
    };
}

public static class ImageDimensions
{
    public static void Validate(string name, ReadOnlySpan<byte> b)
    {
        int width = 0, height = 0;
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext == ".png" && b.Length >= 24)
        { width = BinaryPrimitives.ReadInt32BigEndian(b[16..]); height = BinaryPrimitives.ReadInt32BigEndian(b[20..]); }
        else if (ext == ".gif" && b.Length >= 10)
        { width = BinaryPrimitives.ReadUInt16LittleEndian(b[6..]); height = BinaryPrimitives.ReadUInt16LittleEndian(b[8..]); }
        else if (ext == ".bmp" && b.Length >= 26)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(b[18..]);
            long h = Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(b[22..]));
            height = h > int.MaxValue ? 0 : (int)h;
        }
        else if (ext is ".jpg" or ".jpeg")
        {
            int pos = 2;
            while (pos + 4 < b.Length)
            {
                if (b[pos++] != 0xff) break;
                while (pos < b.Length && b[pos] == 0xff) pos++;
                if (pos + 2 >= b.Length) break;
                byte marker = b[pos++];
                if (marker is 0xd9 or 0xda) break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
                int length = BinaryPrimitives.ReadUInt16BigEndian(b[pos..]);
                if (length < 2 || pos + length > b.Length) break;
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                {
                    if (length < 7) break;
                    height = BinaryPrimitives.ReadUInt16BigEndian(b[(pos + 3)..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(b[(pos + 5)..]); break;
                }
                pos += length;
            }
        }
        if (width <= 0 || height <= 0) throw new ApiError(400, "Не удалось прочитать размеры фото. Сохраните его как JPG или PNG.");
        if (width > MediaRules.ImageSideLimit || height > MediaRules.ImageSideLimit)
            throw new ApiError(413, "Фото больше 7680×7680 пикселей. Уменьшите его перед загрузкой.");
    }
}
