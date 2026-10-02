namespace EquipmentDowntime.Core;

public enum MediaKind { Image, Video }

public sealed record JournalAttachment
{
    public Guid Id { get; init; }
    public Guid RecordId { get; init; }
    public MediaKind Kind { get; init; }
    public string OriginalFileName { get; init; } = "";
    public string StoredFileName { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public DateTimeOffset AddedAtUtc { get; init; }
}

public static class MediaRules
{
    public const long ImageLimit = 50_000_000;
    public const long VideoLimit = 250_000_000;
    public const int ImageSideLimit = 7680;

    public static MediaKind KindFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" => MediaKind.Image,
        ".mp4" or ".mov" or ".mkv" or ".webm" => MediaKind.Video,
        _ => throw new InvalidDataException("Выберите фото JPG, PNG, GIF, BMP, TIFF или видео MP4, MOV, MKV, WEBM. Фото HEIC предварительно сохраните как JPG.")
    };

    public static void ValidateSize(MediaKind kind, long size)
    {
        if (size <= 0 || size > (kind == MediaKind.Image ? ImageLimit : VideoLimit))
            throw new InvalidDataException(kind == MediaKind.Image
                ? "Фото должно быть непустым и не больше 50 МБ."
                : "Видео должно быть непустым и не больше 250 МБ.");
    }

    // Проверяем контейнер до постановки в очередь. Полную обработку видео выполняет MAX.
    public static void ValidateSignature(string name, ReadOnlySpan<byte> prefix)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        bool ok = ext switch
        {
            ".jpg" or ".jpeg" => prefix.StartsWith(new byte[] { 0xff, 0xd8, 0xff }),
            ".png" => prefix.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".gif" => prefix.StartsWith("GIF87a"u8) || prefix.StartsWith("GIF89a"u8),
            ".bmp" => prefix.StartsWith("BM"u8),
            ".tif" or ".tiff" => prefix.StartsWith(new byte[] { 73, 73, 42, 0 }) || prefix.StartsWith(new byte[] { 77, 77, 0, 42 }),
            ".mp4" or ".mov" => prefix.Length >= 12 && (prefix.Slice(4, 4).SequenceEqual("ftyp"u8) ||
                (ext == ".mov" && (prefix.Slice(4, 4).SequenceEqual("moov"u8) ||
                    prefix.Slice(4, 4).SequenceEqual("mdat"u8) || prefix.Slice(4, 4).SequenceEqual("wide"u8)))),
            ".mkv" or ".webm" => prefix.StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }),
            _ => false
        };
        if (!ok) throw new InvalidDataException("Содержимое файла не соответствует формату фото или видео. Выберите исходный медиафайл.");
    }

    public static void ValidateMetadata(JournalAttachment a)
    {
        if (a.Id == Guid.Empty || a.RecordId == Guid.Empty || !Enum.IsDefined(a.Kind) ||
            string.IsNullOrWhiteSpace(a.OriginalFileName) || a.OriginalFileName.Length > 255 ||
            a.OriginalFileName.IndexOfAny(['/', '\\', '\r', '\n']) >= 0 ||
            a.StoredFileName != a.Id.ToString("N") + Path.GetExtension(a.OriginalFileName).ToLowerInvariant() ||
            a.Sha256 is null || a.Sha256.Length != 64 || !a.Sha256.All(Uri.IsHexDigit) ||
            KindFor(a.OriginalFileName) != a.Kind)
            throw new InvalidDataException("Повреждены сведения о фото или видео.");
        ValidateSize(a.Kind, a.SizeBytes);
    }
}
