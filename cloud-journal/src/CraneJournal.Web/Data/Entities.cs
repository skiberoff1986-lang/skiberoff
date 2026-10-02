using EquipmentDowntime.Core;
using Microsoft.AspNetCore.Identity;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CraneJournal.Web.Data;

public sealed class AppUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; } = true;
}
public sealed class UserShop
{
    public Guid UserId { get; set; }
    public string Shop { get; set; } = "";
}
public sealed class RepairRecord
{
    public Guid Id { get; set; }
    public long Revision { get; set; }
    public string Shop { get; set; } = "";
    public string Crane { get; set; } = "";
    public bool IsClosed { get; set; }
    public string SearchText { get; set; } = "";
    public DateTime RegisteredLocal { get; set; }
    public string DataJson { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DowntimeRecord Read() => JsonSerializer.Deserialize<DowntimeRecord>(DataJson, Json.Default)!;
    public void Set(DowntimeRecord data)
    {
        Id = data.Id; Shop = data.ShopNumber; Crane = data.CraneNumber; RegisteredLocal = data.ReportedAt;
        IsClosed = data.RestoredAt.HasValue;
        SearchText = string.Join(' ', data.ShopNumber, data.CraneNumber, data.ReporterName, data.FaultDescription,
            data.ResponsiblePerson, data.ShiftMaster, data.WorkDescription).ToLowerInvariant();
        DataJson = JsonSerializer.Serialize(data, Json.Default);
    }
}
public sealed class JournalEvent
{
    public long Sequence { get; set; }
    public Guid Id { get; set; }
    public Guid RecordId { get; set; }
    public Guid CommandId { get; set; }
    public Guid ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTimeOffset AcceptedAtUtc { get; set; }
    public string SnapshotJson { get; set; } = "";
}
public sealed class CommandReceipt
{
    public Guid Id { get; set; }
    public Guid? RecordId { get; set; }
    public Guid ActorId { get; set; }
    public string Fingerprint { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public DateTimeOffset AcceptedAtUtc { get; set; }
}
public enum UploadState { Uploading, Ready, Duplicate }
public sealed class MediaFile
{
    public bool NotifyMax { get; set; } = true;
    public string ExpectedSha256 { get; set; } = "";
    public Guid? DuplicateOfId { get; set; }
    public Guid Id { get; set; }
    public Guid RecordId { get; set; }
    public Guid OwnerId { get; set; }
    public string OriginalName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public MediaKind Kind { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public UploadState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public JournalAttachment AsAttachment() => new()
    {
        Id = Id, RecordId = RecordId, Kind = Kind, OriginalFileName = OriginalName,
        StoredFileName = Id.ToString("N") + Path.GetExtension(OriginalName).ToLowerInvariant(),
        SizeBytes = Size, Sha256 = Sha256, AddedAtUtc = CompletedAtUtc ?? CreatedAtUtc
    };
}
public sealed class MediaChunk
{
    public Guid MediaId { get; set; }
    public int Index { get; set; }
    public string Sha256 { get; set; } = "";
    public byte[] Data { get; set; } = [];
}
public sealed class OutboxItem
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public Guid RecordId { get; set; }
    public Guid? MediaId { get; set; }
    public MaxNotificationStage Stage { get; set; }
    public long ChatId { get; set; }
    public string Text { get; set; } = "";
    public string MediaToken { get; set; } = "";
    public MaxDeliveryState State { get; set; }
    public int Attempts { get; set; }
    public string Error { get; set; } = "";
    public string MessageId { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public Guid? LeaseId { get; set; }
    public static OutboxItem From(MaxNotification n) => new()
    {
        Id = n.NotificationId, RecordId = n.RecordId, MediaId = n.AttachmentId,
        Stage = n.Stage, ChatId = n.ChatId, Text = n.Text,
        CreatedAtUtc = n.CreatedAtUtc, NextAttemptAtUtc = n.NextAttemptAtUtc
    };
}
public sealed class SchemaState { public int Id { get; set; } public int Version { get; set; } }
public static class Json
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
}
