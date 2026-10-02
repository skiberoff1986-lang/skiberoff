using CraneJournal.Web.Api;
using CraneJournal.Web.Data;
using EquipmentDowntime.Core;
using Microsoft.EntityFrameworkCore;

namespace CraneJournal.Web.Services;

public sealed record ImportPreview(int Total, int Added, int Unchanged, Guid[] Conflicts, string[] MissingShops);
public sealed class ImportService(AppDb db, CommandRunner commands, RecordService records, BusinessClock clock)
{
    private static void Validate(List<DowntimeRecord>? incoming)
    {
        if (incoming is null || incoming.Count is 0 or > 3000) throw new ApiError(400, "За один раз можно перенести от 1 до 3000 заявок.");
        var ids = new HashSet<Guid>();
        foreach (var r in incoming)
        {
            if (r is null) throw new ApiError(400, "Журнал содержит пустую запись.");
            var errors = RecordRules.Validate(r); // legacy closed records can have no work description
            if (errors.Count > 0) throw new ApiError(400, $"Заявка {r.Id}: " + string.Join(" ", errors));
            if (!ids.Add(r.Id)) throw new ApiError(400, "В журнале повторяются ID заявок.");
        }
    }
    public async Task<ImportPreview> Preview(List<DowntimeRecord> incoming)
    {
        Validate(incoming);
        var ids = incoming.Select(r => r.Id).ToArray();
        var old = await db.Records.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        var conflicts = incoming.Where(r => old.TryGetValue(r.Id, out var previous) && previous.Read() != r).Select(r => r.Id).ToArray();
        return new(incoming.Count, incoming.Count - old.Count, old.Count - conflicts.Length, conflicts,
            incoming.Where(r => string.IsNullOrWhiteSpace(r.ShopNumber)).Select(r => r.Id.ToString()).ToArray());
    }
    public Task<string> Commit(Actor actor, ImportRequest input)
    {
        AccessService.Admin(actor); Validate(input.Records);
        return commands.Run(actor, input.CommandId, "import", input, null, async () =>
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(831614073267)");
            int added = 0, unchanged = 0;
            foreach (var r in input.Records.OrderBy(r => r.Id))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Records\" WHERE \"Id\"={r.Id} FOR UPDATE");
                var existing = await db.Records.SingleOrDefaultAsync(x => x.Id == r.Id);
                if (existing is not null)
                {
                    if (existing.Read() != r) throw new ApiError(409, $"Заявка {r.Id} уже есть с другими данными. Импорт отменён, ничего не заменено.");
                    unchanged++; continue;
                }
                var entity = new RepairRecord { Revision = 1, CreatedAtUtc = clock.UtcNow, UpdatedAtUtc = clock.UtcNow };
                entity.Set(r); db.Records.Add(entity); records.AddEvent(entity, actor, input.CommandId, "Imported"); added++;
            }
            // Historical stages never generate new MAX notifications; the old queue is not imported.
            return new { added, unchanged, total = input.Records.Count };
        });
    }
}
