using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CraneJournal.Web.Api;
using CraneJournal.Web.Data;
using CraneJournal.Web.Services;
using EquipmentDowntime.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CraneJournal.Checks;

internal static class Program
{
    private static int count;
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception("FAILED: " + name); Console.WriteLine("PASS: " + name); count++; }
    private static void Reject<T>(Action action, string name) where T : Exception
    { try { action(); } catch (T) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
    private static async Task RejectApi(Func<Task> action, int code, string name)
    { try { await action(); } catch (ApiError e) when (e.Status == code) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json.Default)!;
    private static ClaimsPrincipal Principal(Guid id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "check"));
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Unit();
            if (args.Contains("--integration")) await Integration();
            Console.WriteLine($"{count} checks passed; integration={(args.Contains("--integration") ? "executed" : "not requested")}");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void Unit()
    {
        var start = new DateTime(2026, 10, 1, 8, 0, 0);
        var r = new DowntimeRecord { Id = Guid.NewGuid(), ReportedAt = start, ShopNumber = "01", CraneNumber = "04А",
            ReporterName = "Иванов И.И.", FaultDescription = "Не включается", RespondedAt = start.AddHours(1),
            RepairStartedAt = start.AddHours(3), RestoredAt = start.AddHours(5), WorkDescription = "Заменён контакт" };
        Check(r.DowntimeAt(start.AddDays(1)) == TimeSpan.FromHours(3), "downtime subtracts response-to-repair interval");
        Check((r with { RepairStartedAt = null, RestoredAt = null }).DowntimeAt(start.AddHours(4)) == TimeSpan.FromHours(1), "open waiting period is excluded");
        Check((r with { RespondedAt = null, RepairStartedAt = null }).DowntimeAt(start.AddDays(1)) == TimeSpan.FromHours(5), "legacy missing stages retain full duration");
        Check((r with { WorksWithRestrictions = true }).CountedDowntimeAt(start.AddDays(1)) == TimeSpan.Zero, "SDU excludes total");
        Check((r with { WorksWithRestrictions = true }).DowntimeAt(start.AddDays(1)) == TimeSpan.FromHours(3), "SDU preserves raw duration");
        Check(RecordRules.ValidateUpdate(r, r with { WorksWithRestrictions = true }).Count == 0, "closed record allows SDU toggle");
        Check(RecordRules.ValidateUpdate(r, r with { WorkDescription = "Changed" }).Count > 0, "closed work description is immutable");
        Check(RecordRules.Validate(r with { RepairStartedAt = start.AddMinutes(30) }).Count > 0, "time chronology enforced");
        Check(RecordRules.ValidateNewStages(r with { RestoredAt = null }, r with { WorkDescription = "" }).Count > 0, "closing requires work description");
        Check(TimeText.Duration(TimeSpan.FromHours(27.5)) == "27:30", "duration does not wrap after a day");
        Check(MaxNotification.Compose(r, MaxNotificationStage.Restored).Contains("Заменён контакт"), "MAX closure includes work description");
        Check(MaxNotification.Compose(r, MaxNotificationStage.Restored).Contains("3 ч 00 мин"), "MAX closure includes corrected duration");
        Check(MaxNotification.NewStages(r with { RestoredAt = null }, r).SequenceEqual([MaxNotificationStage.Restored]), "only newly completed stage is notified");
        var clock = new BusinessClock(Config(new() { ["BUSINESS_TIMEZONE"] = "Europe/Moscow" }), new FixedTime());
        Check(clock.LocalNow == new DateTime(2026, 10, 1, 12, 0, 0), "business timezone is independent of server timezone");
        Reject<ApiError>(() => clock.EventTime("2026-10-01T12:01"), "future stage rejected");
        Check(MaxNotification.ForAccounting(r with { RespondedAt = null, RepairStartedAt = null, RestoredAt = null }, -1, clock.UtcNow, clock.LocalNow).Text.Contains("4 ч 00 мин"), "accounting notification uses business timezone");
        var uri = new NpgsqlConnectionStringBuilder(ConnectionStrings.Parse("postgresql://user:p%40ss@db.example.com:5433/journal"));
        Check(uri.Password == "p@ss" && uri.Port == 5433 && uri.SslMode == SslMode.VerifyFull, "external database validates TLS and decodes credentials");
        Check(new NpgsqlConnectionStringBuilder(ConnectionStrings.Parse("postgresql://user:pass@db/journal")).Port == 5432, "database URI default port");
        Reject<InvalidOperationException>(() => ConnectionStrings.Parse("postgres://u:p@db.example.com/journal?sslmode=require"), "external unverifiable TLS mode rejected");
        var operatorA = new Actor(Guid.NewGuid(), "А", "a", false, true, false, ["01"]);
        Reject<ApiError>(() => AccessService.Shop(operatorA, "02", true), "shop access cannot be widened by request");
        Reject<ApiError>(() => AccessService.Shop(operatorA with { CanWrite = false }, "01", true), "view-only role cannot write");
        var request = new StageRequest(Guid.NewGuid(), 1, "Restored", null, null, null, "Выполнено", null);
        Reject<ApiError>(() => RecordService.Apply(r with { RestoredAt = null, RepairStartedAt = null }, request, operatorA, clock), "repair cannot be closed before issue");
        Reject<InvalidDataException>(() => MediaRules.ValidateSignature("bad.png", "not an image"u8), "fake media extension rejected");
        Reject<InvalidDataException>(() => MediaRules.ValidateSize(MediaKind.Video, 250_000_001), "oversized video rejected");
        Reject<ApiError>(() => ImageDimensions.Validate("bad.png", new byte[24]), "invalid photo dimensions rejected");
        var command = new CreateRequest(Guid.NewGuid(), "01", "04А", "Fault", null);
        Check(CommandRunner.Fingerprint("create", command) != CommandRunner.Fingerprint("create", command with { Fault = "Other" }), "command reuse cannot change its contents");
        var recordJson = JsonSerializer.Serialize(r, Json.Default);
        Check(Read<DowntimeRecord>(recordJson) == r, "record JSON roundtrip preserves local timestamps");
    }

    private static async Task Integration()
    {
        string raw = Environment.GetEnvironmentVariable("CHECK_DATABASE_URL") ?? throw new Exception("CHECK_DATABASE_URL is required for integration checks.");
        var cs = new NpgsqlConnectionStringBuilder(ConnectionStrings.Parse(raw));
        if (cs.Database != "crane_journal_checks") throw new Exception("Integration checks only accept the disposable database crane_journal_checks.");
        cs.Timeout = 3;
        for (int attempt = 0; ; attempt++)
        {
            try { await using var probe = new NpgsqlConnection(cs.ConnectionString); await probe.OpenAsync(); break; }
            catch (NpgsqlException) when (attempt < 29) { await Task.Delay(1000); }
        }
        var config = Config(new() { ["DATABASE_URL"] = cs.ConnectionString, ["BOOTSTRAP_PASSWORD"] = "Ephemeral-check-password-2026!",
            ["BUSINESS_TIMEZONE"] = "Europe/Moscow", ["MAX_BOT_TOKEN"] = "local-test-token-only", ["MAX_CHAT_ID"] = "-123", ["MEDIA_QUOTA_BYTES"] = "50000000" });
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton(config); services.AddSingleton(TimeProvider.System);
        services.AddDbContext<AppDb>(o => o.UseNpgsql(cs.ConnectionString));
        services.AddIdentity<AppUser, IdentityRole<Guid>>().AddEntityFrameworkStores<AppDb>().AddDefaultTokenProviders();
        services.AddDataProtection().PersistKeysToDbContext<AppDb>();
        services.AddSingleton<BusinessClock>(); services.AddSingleton<MaxOptions>();
        services.AddScoped<CommandRunner>(); services.AddScoped<RecordService>(); services.AddScoped<MediaService>();
        services.AddScoped<AccessService>(); services.AddScoped<ImportService>();
        // No hosted worker is registered: tests never contact MAX or send messages.
        await using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope()) await scope.ServiceProvider.GetRequiredService<AppDb>().Database.EnsureDeletedAsync();
        await Bootstrap.Initialize(provider, config);
        await Bootstrap.Initialize(provider, config); // repeat startup must not replace schema or admin
        Actor actor, second, other, viewer, admin;
        using (var scope = provider.CreateScope())
        {
            var sp = scope.ServiceProvider; var db = sp.GetRequiredService<AppDb>(); var users = sp.GetRequiredService<UserManager<AppUser>>();
            async Task<Actor> Add(string login, string role, string shop)
            {
                var user = new AppUser { Id = Guid.NewGuid(), UserName = login, FullName = login, MustChangePassword = false };
                Bootstrap.Check(await users.CreateAsync(user, "Local-testing-password-2026!")); Bootstrap.Check(await users.AddToRoleAsync(user, role));
                db.UserShops.Add(new() { UserId = user.Id, Shop = shop }); await db.SaveChangesAsync();
                return await sp.GetRequiredService<AccessService>().Actor(Principal(user.Id));
            }
            actor = await Add("first", Roles.Operator, "01"); second = await Add("second", Roles.Operator, "01");
            other = await Add("other", Roles.Operator, "02"); viewer = await Add("viewer", Roles.Viewer, "01");
            var firstAdmin = (await users.FindByNameAsync("admin"))!; firstAdmin.MustChangePassword = false; Bootstrap.Check(await users.UpdateAsync(firstAdmin));
            admin = await sp.GetRequiredService<AccessService>().Actor(Principal(firstAdmin.Id));
            Check(await db.Users.CountAsync() == 5, "repeat bootstrap preserves accounts");
        }
        async Task<T> In<T>(Func<IServiceProvider, Task<T>> action)
        { using var scope = provider.CreateScope(); return await action(scope.ServiceProvider); }
        var now = provider.GetRequiredService<BusinessClock>().LocalNow;
        string at = now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm");
        var create = new CreateRequest(Guid.NewGuid(), "01", "04А", "Test fault", at);
        var first = Read<RecordResult>(await In(sp => sp.GetRequiredService<RecordService>().Create(actor, create)));
        var same = Read<RecordResult>(await In(sp => sp.GetRequiredService<RecordService>().Create(actor, create)));
        Check(first == same, "identical command returns original result");
        await RejectApi(() => In(sp => sp.GetRequiredService<RecordService>().Create(actor, create with { Fault = "Other" })), 409, "same command ID with changed payload rejected");
        await RejectApi(() => In(sp => sp.GetRequiredService<RecordService>().Create(second, create)), 409, "another actor cannot reuse command ID");
        Check(await In(sp => sp.GetRequiredService<AppDb>().Events.CountAsync()) == 1, "retry creates no duplicate event");
        Check(await In(sp => sp.GetRequiredService<AppDb>().Outbox.CountAsync()) == 1, "retry creates no duplicate MAX notification");
        var stageA = new StageRequest(Guid.NewGuid(), 1, "Responded", at, null, null, null, null);
        var stageB = stageA with { CommandId = Guid.NewGuid() };
        async Task<bool> Compete(Actor who, StageRequest request)
        { try { await In(sp => sp.GetRequiredService<RecordService>().Stage(first.Id, who, request)); return true; } catch (ApiError e) when (e.Status == 409) { return false; } }
        var accepted = await Task.WhenAll(Compete(actor, stageA), Compete(second, stageB));
        Check(accepted.Count(x => x) == 1, "simultaneous stage registration accepts exactly one actor");
        var winningRequest = accepted[0] ? stageA : stageB; var winner = accepted[0] ? actor : second;
        Check(Read<RecordResult>(await In(sp => sp.GetRequiredService<RecordService>().Stage(first.Id, winner, winningRequest))).Revision == 2, "retry after concurrent win returns saved result");
        await RejectApi(() => In(sp => sp.GetRequiredService<RecordService>().Stage(first.Id, other, stageA with { CommandId = Guid.NewGuid(), ExpectedRevision = 2 })), 403, "other shop cannot mutate record");
        await RejectApi(() => In(sp => sp.GetRequiredService<RecordService>().Stage(first.Id, viewer, stageA with { CommandId = Guid.NewGuid(), ExpectedRevision = 2 })), 403, "viewer cannot mutate record");
        Check(await In(sp => AccessService.Visible(sp.GetRequiredService<AppDb>().Records, other).CountAsync()) == 0, "other shop list excludes record");
        var repaired = Read<RecordResult>(await In(sp => sp.GetRequiredService<RecordService>().Stage(first.Id, actor,
            new(Guid.NewGuid(), 2, "RepairStarted", at, null, null, null, null))));
        var restored = Read<RecordResult>(await In(sp => sp.GetRequiredService<RecordService>().Stage(first.Id, actor,
            new(Guid.NewGuid(), repaired.Revision, "Restored", at, null, null, "Контакт заменён", null))));
        var sdu = Read<RecordResult>(await In(sp => sp.GetRequiredService<RecordService>().Stage(first.Id, actor,
            new(Guid.NewGuid(), restored.Revision, "AccountingChanged", null, null, null, null, true))));
        Check(sdu.Data.WorksWithRestrictions && sdu.Data.WorkDescription == "Контакт заменён", "closed SDU toggle preserves repair description");
        await RejectApi(() => In(sp => sp.GetRequiredService<RecordService>().Stage(first.Id, actor,
            new(Guid.NewGuid(), sdu.Revision, "Restored", at, null, null, "Changed", null))), 409, "completed stage cannot be overwritten");
        async Task Immutable(string table)
        {
            try { await In(async sp => await sp.GetRequiredService<AppDb>().Database.ExecuteSqlRawAsync($"DELETE FROM \"{table}\"")); }
            catch (PostgresException e) when (e.SqlState == "P0001") { Check(true, table + " protected by immutable DB trigger"); return; }
            throw new Exception("Immutable trigger did not reject DELETE");
        }
        await Immutable("Events"); await Immutable("Commands");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j7XcAAAAASUVORK5CYII=");
        var content = new byte[MediaService.ChunkSize + png.Length]; png.CopyTo(content, 0);
        string hash = Convert.ToHexString(SHA256.HashData(content));
        var upload = new StartUpload(Guid.NewGuid(), "test.png", content.Length, true, hash);
        var media = Read<MediaResult>(await In(sp => sp.GetRequiredService<MediaService>().Start(first.Id, actor, upload)));
        async Task Part(Guid id, int index, byte[] bytes) => await In(async sp => { await sp.GetRequiredService<MediaService>().PutChunk(id, index, bytes, actor); return 0; });
        await Part(media.Id, 0, content[..MediaService.ChunkSize]); await Part(media.Id, 0, content[..MediaService.ChunkSize]);
        Check(await In(sp => sp.GetRequiredService<AppDb>().Chunks.CountAsync()) == 1, "chunk retry does not duplicate bytes");
        var finish = new CompleteUpload(Guid.NewGuid());
        await RejectApi(() => In(sp => sp.GetRequiredService<MediaService>().Complete(media.Id, actor, finish)), 409, "incomplete upload cannot be registered");
        await Part(media.Id, 1, content[MediaService.ChunkSize..]);
        var ready = Read<MediaResult>(await In(sp => sp.GetRequiredService<MediaService>().Complete(media.Id, actor, finish)));
        Check(ready.State == "Ready", "resumed upload completes");
        Check(Read<MediaResult>(await In(sp => sp.GetRequiredService<MediaService>().Complete(media.Id, actor, finish))).Id == ready.Id, "completion is idempotent");
        await using (var stream = await ChunkStream.Open(cs.ConnectionString, ready.Id, content.Length))
        { using var output = new MemoryStream(); await stream.CopyToAsync(output); Check(output.ToArray().SequenceEqual(content), "chunk stream returns original bytes in order"); }
        var different = content[..MediaService.ChunkSize]; different[0] ^= 1;
        await RejectApi(() => Part(media.Id, 0, different), 409, "registered media part cannot be replaced");
        await RejectApi(() => In(sp => sp.GetRequiredService<MediaService>().Get(media.Id, other)), 403, "other shop cannot download attachment");
        var duplicate = Read<MediaResult>(await In(sp => sp.GetRequiredService<MediaService>().Start(first.Id, actor, upload with { CommandId = Guid.NewGuid() })));
        await Part(duplicate.Id, 0, content[..MediaService.ChunkSize]); await Part(duplicate.Id, 1, content[MediaService.ChunkSize..]);
        var duplicateCommand = new CompleteUpload(Guid.NewGuid());
        var dedup = Read<MediaResult>(await In(sp => sp.GetRequiredService<MediaService>().Complete(duplicate.Id, actor, duplicateCommand)));
        Check(dedup.Id == ready.Id, "same media content deduplicates inside a record");
        Check(Read<MediaResult>(await In(sp => sp.GetRequiredService<MediaService>().Complete(duplicate.Id, actor, duplicateCommand))).Id == ready.Id, "duplicate completion receipt remains replayable");
        Check(await In(sp => sp.GetRequiredService<AppDb>().Outbox.CountAsync(o => o.MediaId != null)) == 1, "deduplicated media queues only one MAX message");
        var legacy = sdu.Data with { Id = Guid.NewGuid(), ShopNumber = "02" };
        int before = await In(sp => sp.GetRequiredService<AppDb>().Outbox.CountAsync());
        var incoming = new ImportRequest(Guid.NewGuid(), [legacy]);
        await In(sp => sp.GetRequiredService<ImportService>().Commit(admin, incoming));
        await In(sp => sp.GetRequiredService<ImportService>().Commit(admin, incoming));
        Check(await In(sp => sp.GetRequiredService<AppDb>().Outbox.CountAsync()) == before, "historical import sends no old MAX notifications");
        await RejectApi(() => In(sp => sp.GetRequiredService<ImportService>().Commit(admin,
            new(Guid.NewGuid(), [legacy with { WorkDescription = "Overwritten" }]))), 409, "conflicting import cannot overwrite a record");
        await In(async sp => { var db = sp.GetRequiredService<AppDb>(); await db.Outbox.ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1))); return 0; });
        var claims = await Task.WhenAll(In(sp => OutboxWorker.Claim(sp.GetRequiredService<AppDb>(), -123, default)), In(sp => OutboxWorker.Claim(sp.GetRequiredService<AppDb>(), -123, default)));
        Check(claims.Count(x => x is not null) == 1, "parallel workers preserve order within record");
        var claimed = claims.Single(x => x is not null)!;
        await In(async sp => { var db = sp.GetRequiredService<AppDb>(); await db.Outbox.Where(o => o.Id == claimed.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseUntilUtc, (DateTimeOffset?)DateTimeOffset.UtcNow.AddSeconds(-1))); await OutboxWorker.Recover(db, default); return 0; });
        Check(await In(sp => sp.GetRequiredService<AppDb>().Outbox.Where(o => o.Id == claimed.Id).Select(o => o.State).SingleAsync()) == MaxDeliveryState.Uncertain, "interrupted send requires manual review");
        Check(await In(sp => OutboxWorker.Claim(sp.GetRequiredService<AppDb>(), -123, default)) is null, "uncertain send prevents later events overtaking it");
        await In(async sp => { var db = sp.GetRequiredService<AppDb>(); await db.Users.Where(u => u.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false)); return 0; });
        await RejectApi(() => In(sp => sp.GetRequiredService<AccessService>().Actor(Principal(actor.Id))), 401, "disabled user is rejected without waiting for cookie expiry");
        await HttpChecks.Run(cs.ConnectionString, Check);
    }
    private sealed class FixedTime : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero); }
}
