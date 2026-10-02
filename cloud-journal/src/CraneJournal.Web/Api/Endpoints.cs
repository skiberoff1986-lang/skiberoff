using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CraneJournal.Web.Data;
using CraneJournal.Web.Services;
using EquipmentDowntime.Core;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CraneJournal.Web.Api;

public sealed record LoginRequest(string Login, string Password);
public sealed record PasswordRequest(string OldPassword, string NewPassword);
public sealed record UserRequest(string Login, string FullName, string Role, string[] Shops, string TemporaryPassword);
public sealed record UserAccessRequest(string[] Shops, bool IsActive);
public sealed record ResetPasswordRequest(string TemporaryPassword);
public sealed record ResolveRequest(Guid CommandId, bool Retry, bool GroupChecked);
public sealed record ImportRequest(Guid CommandId, List<DowntimeRecord> Records);

public static class Endpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/session", async (HttpContext ctx, IAntiforgery csrf, AccessService access, BusinessClock clock, MaxOptions max) =>
        {
            Actor? actor = null;
            if (ctx.User.Identity?.IsAuthenticated == true)
            {
                try { actor = await access.Actor(ctx.User, true); }
                catch (ApiError ex) when (ex.Status == 401) { }
            }
            return Results.Ok(new { user = actor, csrf = csrf.GetAndStoreTokens(ctx).RequestToken,
                nowLocal = clock.LocalNow, timeZone = clock.ZoneId, maxEnabled = max.Enabled });
        }).AllowAnonymous();
        api.MapPost("/login", async (LoginRequest input, SignInManager<AppUser> signIn, UserManager<AppUser> users) =>
        {
            string login = input.Login?.Trim() ?? "";
            if (login.Length is 0 or > 256 || string.IsNullOrEmpty(input.Password) || input.Password.Length > 1024)
                throw new ApiError(400, "Введите логин и пароль.");
            var user = await users.FindByNameAsync(login);
            if (user is null || !user.IsActive) throw new ApiError(401, "Неверный логин или пароль либо учётная запись недоступна.");
            var result = await signIn.PasswordSignInAsync(user, input.Password, isPersistent: false, lockoutOnFailure: true);
            if (!result.Succeeded) throw new ApiError(401, "Неверный логин или пароль либо временная блокировка входа.");
            return Results.Ok(new { signedIn = true });
        }).AllowAnonymous().RequireRateLimiting("login");
        api.MapPost("/logout", async (SignInManager<AppUser> signIn) => { await signIn.SignOutAsync(); return Results.Ok(new { signedOut = true }); });
        api.MapPost("/password", async (PasswordRequest input, HttpContext ctx, AccessService access, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            await access.Actor(ctx.User, true);
            if (input.NewPassword?.Length > 1024 || input.OldPassword?.Length > 1024) throw new ApiError(400, "Слишком длинный пароль.");
            var user = (await users.GetUserAsync(ctx.User))!;
            Bootstrap.Check(await users.ChangePasswordAsync(user, input.OldPassword ?? "", input.NewPassword ?? ""));
            user.MustChangePassword = false; Bootstrap.Check(await users.UpdateAsync(user));
            await signIn.RefreshSignInAsync(user);
            return Results.Ok(new { changed = true });
        });
        api.MapGet("/shops", async (HttpContext ctx, AccessService access, AppDb db) =>
        {
            var actor = await access.Actor(ctx.User);
            return Results.Ok(actor.Admin
                ? await db.Records.Select(r => r.Shop).Union(db.UserShops.Select(s => s.Shop)).Where(s => s != "").Distinct().Order().ToArrayAsync()
                : actor.Shops);
        });
        api.MapGet("/records", List);
        api.MapGet("/records/export", Export);
        api.MapGet("/records/{id:guid}", async (Guid id, HttpContext ctx, AccessService access, AppDb db, BusinessClock clock) =>
        {
            var actor = await access.Actor(ctx.User);
            var entity = await AccessService.Visible(db.Records.AsNoTracking(), actor).SingleOrDefaultAsync(r => r.Id == id)
                ?? throw new ApiError(404, "Заявка не найдена или недоступна.");
            var events = await db.Events.AsNoTracking().Where(e => e.RecordId == id).OrderBy(e => e.Sequence)
                .Select(e => new { e.Id, e.Kind, e.ActorName, e.AcceptedAtUtc }).ToArrayAsync();
            var media = await db.Media.AsNoTracking().Where(m => m.RecordId == id && m.State == UploadState.Ready).OrderBy(m => m.CompletedAtUtc).ToListAsync();
            var queue = await db.Outbox.AsNoTracking().Where(o => o.RecordId == id).OrderBy(o => o.Sequence)
                .Select(o => new { o.Id, o.MediaId, o.Stage, o.State, o.CreatedAtUtc, o.SentAtUtc, o.Error }).ToArrayAsync();
            return Results.Ok(new { record = View(entity, clock.LocalNow), events, media = media.Select(MediaService.Result), queue });
        });
        api.MapPost("/records", async (CreateRequest request, HttpContext ctx, AccessService access, RecordService records) =>
            JsonResult(await records.Create(await access.Actor(ctx.User), request)));
        api.MapPost("/records/{id:guid}/commands", async (Guid id, StageRequest request, HttpContext ctx, AccessService access, RecordService records) =>
            JsonResult(await records.Stage(id, await access.Actor(ctx.User), request)));
        api.MapPost("/records/{id:guid}/media", async (Guid id, StartUpload input, HttpContext ctx, AccessService access, MediaService media) =>
            JsonResult(await media.Start(id, await access.Actor(ctx.User), input)));
        api.MapGet("/uploads/{id:guid}", async (Guid id, HttpContext ctx, AccessService access, MediaService service, AppDb db) =>
        {
            var file = await service.Get(id, await access.Actor(ctx.User), owned: true);
            if (file.State == UploadState.Duplicate) file = await db.Media.AsNoTracking().SingleAsync(m => m.Id == file.DuplicateOfId);
            var chunks = await db.Chunks.Where(c => c.MediaId == file.Id).OrderBy(c => c.Index).Select(c => c.Index).ToArrayAsync();
            return Results.Ok(new { file = MediaService.Result(file), chunks });
        });
        api.MapPut("/uploads/{id:guid}/chunks/{index:int}", async (Guid id, int index, HttpContext ctx, AccessService access, MediaService media) =>
        {
            var actor = await access.Actor(ctx.User);
            byte[] bytes = await ReadLimited(ctx.Request.Body, MediaService.ChunkSize, ctx.RequestAborted);
            await media.PutChunk(id, index, bytes, actor);
            return Results.Ok(new { stored = index });
        });
        api.MapPost("/uploads/{id:guid}/complete", async (Guid id, CompleteUpload input, HttpContext ctx, AccessService access, MediaService media) =>
            JsonResult(await media.Complete(id, await access.Actor(ctx.User), input)));
        api.MapPost("/media/{id:guid}/queue", async (Guid id, CompleteUpload input, HttpContext ctx, AccessService access, MediaService media) =>
            JsonResult(await media.Queue(id, await access.Actor(ctx.User), input)));
        api.MapGet("/media/{id:guid}/file", async (Guid id, HttpContext ctx, AccessService access, MediaService media, AppDb db) =>
        {
            var file = await media.Get(id, await access.Actor(ctx.User));
            if (file.State != UploadState.Ready) throw new ApiError(409, "Файл ещё не зарегистрирован.");
            var stream = await ChunkStream.Open(db.Database.GetConnectionString()!, file.Id, file.Size, ctx.RequestAborted);
            return Results.Stream(stream, file.ContentType, file.OriginalName, enableRangeProcessing: false);
        });
        MapAdmin(api);
    }
    private static IResult JsonResult(string json) => Results.Text(json, "application/json", Encoding.UTF8);
    public static async Task<byte[]> ReadLimited(Stream body, int maximum, CancellationToken ct)
    {
        using var output = new MemoryStream(); var buffer = new byte[81920]; int count;
        while ((count = await body.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + count > maximum) throw new ApiError(413, "Превышен допустимый размер запроса.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return output.ToArray();
    }
    private static IQueryable<RepairRecord> Filter(AppDb db, Actor actor, HttpRequest request)
    {
        var query = AccessService.Visible(db.Records.AsNoTracking(), actor);
        string term = request.Query["search"].ToString().Trim().ToLowerInvariant();
        if (term.Length > 200) throw new ApiError(400, "Поисковая строка слишком длинная.");
        if (term.Length > 0) query = query.Where(r => r.SearchText.Contains(term));
        string shop = request.Query["shop"].ToString();
        if (shop.Length > 0) query = query.Where(r => r.Shop == shop);
        string state = request.Query["state"].ToString();
        if (state == "open") query = query.Where(r => !r.IsClosed);
        if (state == "closed") query = query.Where(r => r.IsClosed);
        foreach (string key in new[] { "from", "through" })
        {
            string value = request.Query[key].ToString();
            if (value.Length == 0) continue;
            if (!DateTime.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date) || date.Year is < 1900 or > 9998)
                throw new ApiError(400, "Проверьте даты фильтра.");
            date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            if (key == "from") query = query.Where(r => r.RegisteredLocal >= date);
            else { var end = date.AddDays(1); query = query.Where(r => r.RegisteredLocal < end); }
        }
        return query;
    }
    private static object View(RepairRecord r, DateTime now)
    {
        var data = r.Read();
        return new { r.Id, r.Revision, data, rawMinutes = (long)data.DowntimeAt(now).TotalMinutes,
            countedMinutes = (long)data.CountedDowntimeAt(now).TotalMinutes, r.UpdatedAtUtc };
    }
    private static async Task<IResult> List(HttpContext ctx, AccessService access, AppDb db, BusinessClock clock)
    {
        var actor = await access.Actor(ctx.User); var query = Filter(db, actor, ctx.Request);
        int page = int.TryParse(ctx.Request.Query["page"], out var p) ? Math.Clamp(p, 1, 100000) : 1;
        var now = clock.LocalNow;
        int count = 0, open = 0, excluded = 0; long total = 0;
        await foreach (var r in query.AsAsyncEnumerable().WithCancellation(ctx.RequestAborted))
        {
            var data = r.Read(); count++; if (!r.IsClosed) open++; if (data.WorksWithRestrictions) excluded++;
            total += (long)data.CountedDowntimeAt(now).TotalMinutes;
        }
        var entities = await query.OrderByDescending(r => r.RegisteredLocal).ThenBy(r => r.Id).Skip((page - 1) * 40).Take(40).ToListAsync(ctx.RequestAborted);
        return Results.Ok(new { records = entities.Select(r => View(r, now)), totalCount = count, openCount = open,
            excludedCount = excluded, countedMinutes = total, page, pages = Math.Max(1, (count + 39) / 40), nowLocal = now });
    }
    private static async Task<IResult> Export(HttpContext ctx, AccessService access, AppDb db, BusinessClock clock)
    {
        var actor = await access.Actor(ctx.User); var query = Filter(db, actor, ctx.Request);
        var rows = await query.OrderByDescending(r => r.RegisteredLocal).ToListAsync(ctx.RequestAborted);
        var files = await db.Media.AsNoTracking().Where(m => m.State == UploadState.Ready && query.Any(r => r.Id == m.RecordId)).ToListAsync(ctx.RequestAborted);
        string csv = CsvExporter.Create(rows.Select(r => r.Read()), clock.LocalNow, files.Select(f => f.AsAttachment()));
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)];
        return Results.File(bytes, "text/csv; charset=utf-8", $"Простои_{clock.LocalNow:yyyy-MM-dd}.csv");
    }
    private static string[] Shops(string[]? values) => (values ?? []).Select(s => s?.Trim() ?? "")
        .Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static void CheckShops(string[] shops)
    {
        if (shops.Length > 100 || shops.Any(s => s.Length > 40 || s.Any(char.IsControl))) throw new ApiError(400, "Проверьте список цехов: до 100 значений, до 40 символов каждое.");
    }
    private static void MapAdmin(RouteGroupBuilder api)
    {
        api.MapGet("/admin/users", async (HttpContext ctx, AccessService access, AppDb db, UserManager<AppUser> users) =>
        {
            AccessService.Admin(await access.Actor(ctx.User));
            var result = new List<object>();
            foreach (var user in await db.Users.OrderBy(u => u.UserName).ToListAsync())
                result.Add(new { user.Id, login = user.UserName, user.FullName, user.IsActive, user.MustChangePassword,
                    roles = await users.GetRolesAsync(user), shops = await db.UserShops.Where(s => s.UserId == user.Id).Select(s => s.Shop).ToArrayAsync() });
            return Results.Ok(result);
        });
        api.MapPost("/admin/users", async (UserRequest input, HttpContext ctx, AccessService access, AppDb db, UserManager<AppUser> users) =>
        {
            AccessService.Admin(await access.Actor(ctx.User));
            string name = input.FullName?.Trim() ?? "", login = input.Login?.Trim() ?? "";
            if (name.Length is 0 or > 120 || login.Length is 0 or > 120 || input.TemporaryPassword?.Length > 1024)
                throw new ApiError(400, "Проверьте логин, фамилию и временный пароль.");
            if (input.Role is not (Roles.Admin or Roles.Operator or Roles.Viewer)) throw new ApiError(400, "Неизвестная роль.");
            string[] shops = Shops(input.Shops); CheckShops(shops);
            if (input.Role != Roles.Admin && shops.Length == 0) throw new ApiError(400, "Назначьте хотя бы один цех.");
            await using var tx = await db.Database.BeginTransactionAsync();
            var user = new AppUser { Id = Guid.NewGuid(), UserName = login, FullName = name, MustChangePassword = true };
            Bootstrap.Check(await users.CreateAsync(user, input.TemporaryPassword ?? ""));
            Bootstrap.Check(await users.AddToRoleAsync(user, input.Role));
            db.UserShops.AddRange(shops.Select(shop => new UserShop { UserId = user.Id, Shop = shop }));
            await db.SaveChangesAsync(); await tx.CommitAsync();
            return Results.Ok(new { user.Id, login = user.UserName });
        });
        api.MapPut("/admin/users/{id:guid}/access", async (Guid id, UserAccessRequest input, HttpContext ctx, AccessService access, AppDb db, UserManager<AppUser> users) =>
        {
            var actor = await access.Actor(ctx.User); AccessService.Admin(actor);
            if (id == actor.Id && !input.IsActive) throw new ApiError(400, "Нельзя отключить собственную учётную запись.");
            string[] shops = Shops(input.Shops); CheckShops(shops);
            var user = await users.FindByIdAsync(id.ToString()) ?? throw new ApiError(404, "Сотрудник не найден.");
            if (input.IsActive && !await users.IsInRoleAsync(user, Roles.Admin) && shops.Length == 0) throw new ApiError(400, "Назначьте хотя бы один цех.");
            await using var tx = await db.Database.BeginTransactionAsync();
            user.IsActive = input.IsActive; Bootstrap.Check(await users.UpdateAsync(user));
            Bootstrap.Check(await users.UpdateSecurityStampAsync(user));
            await db.UserShops.Where(s => s.UserId == id).ExecuteDeleteAsync();
            db.UserShops.AddRange(shops.Select(shop => new UserShop { UserId = id, Shop = shop }));
            await db.SaveChangesAsync(); await tx.CommitAsync();
            return Results.Ok(new { updated = true });
        });
        api.MapPost("/admin/users/{id:guid}/password", async (Guid id, ResetPasswordRequest input, HttpContext ctx, AccessService access, UserManager<AppUser> users, AppDb db) =>
        {
            AccessService.Admin(await access.Actor(ctx.User));
            if (input.TemporaryPassword?.Length > 1024) throw new ApiError(400, "Слишком длинный пароль.");
            var user = await users.FindByIdAsync(id.ToString()) ?? throw new ApiError(404, "Сотрудник не найден.");
            await using var tx = await db.Database.BeginTransactionAsync();
            string token = await users.GeneratePasswordResetTokenAsync(user);
            Bootstrap.Check(await users.ResetPasswordAsync(user, token, input.TemporaryPassword ?? ""));
            user.MustChangePassword = true; Bootstrap.Check(await users.UpdateAsync(user));
            Bootstrap.Check(await users.SetLockoutEndDateAsync(user, null));
            await tx.CommitAsync(); return Results.Ok(new { reset = true });
        });
        api.MapGet("/admin/outbox", async (HttpContext ctx, AccessService access, AppDb db, MaxOptions max, MaxConnectionStatus connection) =>
        {
            AccessService.Admin(await access.Actor(ctx.User));
            bool history = ctx.Request.Query["history"] == "1";
            var source = db.Outbox.AsNoTracking();
            if (!history) source = source.Where(o => o.State != MaxDeliveryState.Sent && o.State != MaxDeliveryState.Cancelled);
            var ordered = history ? source.OrderByDescending(o => o.Sequence) : source.OrderBy(o => o.Sequence);
            var entries = await ordered.Take(200).Select(o => new
            { o.Id, o.RecordId, o.MediaId, o.Stage, o.ChatId, o.Text, o.State, o.Attempts, o.CreatedAtUtc, o.SentAtUtc, o.Error, o.NextAttemptAtUtc }).ToArrayAsync();
            return Results.Ok(new { configured = max.Enabled, chatId = max.ChatId, connection = connection.Current, history, entries });
        });
        api.MapPost("/admin/outbox/{id:guid}/resolve", async (Guid id, ResolveRequest input, HttpContext ctx, AccessService access, AppDb db, CommandRunner commands, RecordService records, BusinessClock clock) =>
        {
            var actor = await access.Actor(ctx.User); AccessService.Admin(actor);
            var scope = await db.Outbox.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id) ?? throw new ApiError(404, "Уведомление не найдено.");
            return JsonResult(await commands.Run(actor, input.CommandId, "outbox-resolve:" + id, input, scope.RecordId, async () =>
            {
                var record = await records.Locked(scope.RecordId, actor);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Outbox\" WHERE \"Id\"={id} FOR UPDATE");
                var item = await db.Outbox.SingleAsync(o => o.Id == id);
                if (item.State is MaxDeliveryState.Sent or MaxDeliveryState.Sending or MaxDeliveryState.Uploading or MaxDeliveryState.Cancelled)
                    throw new ApiError(409, "Это уведомление уже отправлено, отменено или ещё отправляется.");
                if (input.Retry && item.State == MaxDeliveryState.Uncertain && !input.GroupChecked)
                    throw new ApiError(409, "Перед повтором проверьте, не пришло ли сообщение в группу.");
                item.State = input.Retry ? MaxDeliveryState.Pending : MaxDeliveryState.Cancelled;
                item.Error = input.Retry ? "" : "Отправка отменена администратором.";
                item.NextAttemptAtUtc = clock.UtcNow;
                records.AddEvent(record, actor, input.CommandId, input.Retry ? "MaxRetry" : "MaxCancelled");
                return new { item.Id, state = item.State.ToString() };
            }));
        });
        api.MapPost("/admin/import/preview", async (ImportRequest input, HttpContext ctx, AccessService access, ImportService import) =>
        {
            AccessService.Admin(await access.Actor(ctx.User));
            return Results.Ok(await import.Preview(input.Records));
        });
        api.MapPost("/admin/import", async (ImportRequest input, HttpContext ctx, AccessService access, ImportService import) =>
        {
            var actor = await access.Actor(ctx.User); AccessService.Admin(actor);
            return JsonResult(await import.Commit(actor, input));
        });
    }
}
