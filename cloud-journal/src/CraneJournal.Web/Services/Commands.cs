using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CraneJournal.Web.Data;
using EquipmentDowntime.Core;
using Microsoft.EntityFrameworkCore;

namespace CraneJournal.Web.Services;

public sealed record CreateRequest(Guid CommandId, string Shop, string Crane, string Fault, string? EventAt);
public sealed record StageRequest(Guid CommandId, long ExpectedRevision, string Kind, string? EventAt,
    string? Responsible, string? ShiftMaster, string? WorkDescription, bool? WorksWithRestrictions);
public sealed record RecordResult(Guid Id, long Revision, DowntimeRecord Data);

public sealed class CommandRunner(AppDb db, BusinessClock clock)
{
    public static long LockKey(Guid id) => BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(id.ToByteArray()));
    public static string Fingerprint(string kind, object request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\n" + JsonSerializer.Serialize(request, Json.Default))));

    public async Task<string> Run(Actor actor, Guid id, string kind, object request, Guid? recordId, Func<Task<object>> action)
    {
        if (id == Guid.Empty) throw new ApiError(400, "Отсутствует идентификатор команды. Обновите страницу.");
        string hash = Fingerprint(kind, request);
        await using var tx = await db.Database.BeginTransactionAsync();
        long key = LockKey(id);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})");
        var previous = await db.Commands.FindAsync(id);
        if (previous is not null)
        {
            if (previous.ActorId != actor.Id || previous.Fingerprint != hash) throw new ApiError(409, "Этот идентификатор команды уже использован для другого действия.");
            if (previous.RecordId is Guid oldId)
            {
                var old = await db.Records.AsNoTracking().SingleAsync(r => r.Id == oldId);
                AccessService.Shop(actor, old.Shop);
            }
            await tx.CommitAsync();
            return previous.ResultJson;
        }
        object result = await action();
        string json = JsonSerializer.Serialize(result, Json.Default);
        db.Commands.Add(new() { Id = id, ActorId = actor.Id, RecordId = recordId, Fingerprint = hash,
            ResultJson = json, AcceptedAtUtc = clock.UtcNow });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return json;
    }
}

public sealed class RecordService(AppDb db, CommandRunner commands, BusinessClock clock, MaxOptions max)
{
    public async Task<RepairRecord> Locked(Guid id, Actor actor, bool write = true)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Records\" WHERE \"Id\"={id} FOR UPDATE");
        var entity = await db.Records.SingleOrDefaultAsync(r => r.Id == id) ?? throw new ApiError(404, "Заявка не найдена.");
        AccessService.Shop(actor, entity.Shop, write);
        return entity;
    }
    public Task<string> Create(Actor actor, CreateRequest request)
    {
        string shop = request.Shop?.Trim() ?? "";
        AccessService.Shop(actor, shop, true);
        if (string.IsNullOrWhiteSpace(shop) || string.IsNullOrWhiteSpace(request.Crane)) throw new ApiError(400, "Укажите цех и кран.");
        return commands.Run(actor, request.CommandId, "create", request, request.CommandId, async () =>
        {
            if (await db.Records.AnyAsync(r => r.Id == request.CommandId)) throw new ApiError(409, "Заявка с таким идентификатором уже существует.");
            var record = new DowntimeRecord { Id = request.CommandId, ShopNumber = shop, CraneNumber = request.Crane.Trim(),
                ReporterName = actor.Name, FaultDescription = request.Fault?.Trim() ?? "", ReportedAt = clock.EventTime(request.EventAt) };
            Validate(record);
            var entity = new RepairRecord { Revision = 1, CreatedAtUtc = clock.UtcNow, UpdatedAtUtc = clock.UtcNow };
            entity.Set(record); db.Records.Add(entity);
            AddEvent(entity, actor, request.CommandId, "Registered");
            if (max.Enabled) db.Outbox.Add(OutboxItem.From(MaxNotification.Create(record, max.ChatId, clock.UtcNow)));
            return new RecordResult(entity.Id, entity.Revision, record);
        });
    }
    public Task<string> Stage(Guid id, Actor actor, StageRequest request) =>
        commands.Run(actor, request.CommandId, "stage:" + id, request, id, async () =>
        {
            var entity = await Locked(id, actor);
            if (entity.Revision != request.ExpectedRevision) throw new ApiError(409, "Заявку уже изменил другой сотрудник. Обновите её и проверьте этапы.");
            var old = entity.Read();
            var updated = Apply(old, request, actor, clock);
            Validate(updated);
            var violations = RecordRules.ValidateUpdate(old, updated).Concat(RecordRules.ValidateNewStages(old, updated)).ToList();
            if (violations.Count > 0) throw new ApiError(409, string.Join(" ", violations));
            if (updated != old)
            {
                entity.Revision++; entity.UpdatedAtUtc = clock.UtcNow; entity.Set(updated);
                AddEvent(entity, actor, request.CommandId, request.Kind);
                if (max.Enabled)
                {
                    foreach (var stage in MaxNotification.NewStages(old, updated))
                        db.Outbox.Add(OutboxItem.From(MaxNotification.Create(updated, max.ChatId, clock.UtcNow, stage)));
                    if (old.WorksWithRestrictions != updated.WorksWithRestrictions)
                        db.Outbox.Add(OutboxItem.From(MaxNotification.ForAccounting(updated, max.ChatId, clock.UtcNow, clock.LocalNow)));
                }
            }
            return new RecordResult(entity.Id, entity.Revision, updated);
        });

    public static DowntimeRecord Apply(DowntimeRecord old, StageRequest request, Actor actor, BusinessClock clock)
    {
        if (request.Kind == "AccountingChanged")
            return old with { WorksWithRestrictions = request.WorksWithRestrictions ?? throw new ApiError(400, "Укажите состояние отметки СДУ.") };
        if (old.RestoredAt.HasValue) throw new ApiError(409, "Ремонт уже завершён. Даты и описание работ изменять нельзя.");
        DateTime at = clock.EventTime(request.EventAt);
        string responsible = string.IsNullOrWhiteSpace(request.Responsible)
            ? (old.ResponsiblePerson.Length > 0 ? old.ResponsiblePerson : actor.Name) : request.Responsible!.Trim();
        string master = request.ShiftMaster?.Trim() ?? old.ShiftMaster;
        return request.Kind switch
        {
            "Responded" when old.RespondedAt is null => old with { RespondedAt = at, ResponsiblePerson = responsible, ShiftMaster = master },
            "RepairStarted" when old.RepairStartedAt is null && old.RespondedAt.HasValue => old with
                { RepairStartedAt = at, ResponsiblePerson = responsible, ShiftMaster = master },
            "Restored" when old.RestoredAt is null && old.RepairStartedAt.HasValue => old with
                { RestoredAt = at, WorkDescription = request.WorkDescription?.Trim() ?? old.WorkDescription,
                  ResponsiblePerson = responsible, ShiftMaster = master },
            "Responded" or "RepairStarted" or "Restored" => throw new ApiError(409, "Этот этап уже зарегистрирован либо сначала требуется предыдущий этап."),
            _ => throw new ApiError(400, "Неизвестное действие.")
        };
    }
    public void Validate(DowntimeRecord record)
    {
        var errors = RecordRules.Validate(record, clock.LocalNow);
        if (errors.Count > 0) throw new ApiError(400, string.Join(" ", errors));
    }
    public void AddEvent(RepairRecord entity, Actor actor, Guid command, string kind) => db.Events.Add(new()
    {
        Id = Guid.NewGuid(), RecordId = entity.Id, ActorId = actor.Id, ActorName = actor.Name,
        CommandId = command, Kind = kind, AcceptedAtUtc = clock.UtcNow, SnapshotJson = entity.DataJson
    });
}
