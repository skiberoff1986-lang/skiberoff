using System.Globalization;
using System.Security.Claims;
using CraneJournal.Web.Data;
using EquipmentDowntime.Core;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CraneJournal.Web.Services;

public sealed class ApiError(int status, string message) : Exception(message) { public int Status { get; } = status; }
public static class Roles { public const string Admin = "Admin", Operator = "Operator", Viewer = "Viewer"; }
public sealed class BusinessClock(IConfiguration configuration, TimeProvider time)
{
    private readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(configuration["BUSINESS_TIMEZONE"] ?? "Europe/Moscow");
    public DateTimeOffset UtcNow => time.GetUtcNow();
    public DateTime LocalNow => TimeText.ToMinute(TimeZoneInfo.ConvertTime(UtcNow, zone).DateTime);
    public string ZoneId => zone.Id;
    public DateTime EventTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return LocalNow;
        if (!DateTime.TryParseExact(text, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new ApiError(400, "Укажите дату и время с точностью до минуты.");
        value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (value > LocalNow) throw new ApiError(400, "Время события не может быть в будущем.");
        return value;
    }
}
public sealed record Actor(Guid Id, string Name, string Login, bool Admin, bool CanWrite, bool MustChangePassword, string[] Shops);
public sealed class AccessService(UserManager<AppUser> users, AppDb db)
{
    public async Task<Actor> Actor(ClaimsPrincipal principal, bool allowPasswordChange = false)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null || !user.IsActive) throw new ApiError(401, "Войдите под своей учётной записью.");
        var roles = await users.GetRolesAsync(user);
        var actor = new Actor(user.Id, user.FullName, user.UserName!, roles.Contains(Roles.Admin),
            roles.Contains(Roles.Admin) || roles.Contains(Roles.Operator), user.MustChangePassword,
            await db.UserShops.Where(x => x.UserId == user.Id).OrderBy(x => x.Shop).Select(x => x.Shop).ToArrayAsync());
        if (user.MustChangePassword && !allowPasswordChange) throw new ApiError(403, "Сначала смените временный пароль.");
        return actor;
    }
    public static void Shop(Actor actor, string shop, bool write = false)
    {
        if (!actor.Admin && !actor.Shops.Contains(shop, StringComparer.Ordinal)) throw new ApiError(403, "Нет доступа к этому цеху.");
        if (write && !actor.CanWrite) throw new ApiError(403, "Для этой учётной записи разрешён только просмотр.");
    }
    public static void Admin(Actor actor) { if (!actor.Admin) throw new ApiError(403, "Действие доступно администратору."); }
    public static IQueryable<RepairRecord> Visible(IQueryable<RepairRecord> query, Actor actor) =>
        actor.Admin ? query : query.Where(r => actor.Shops.Contains(r.Shop));
}
public sealed class MaxOptions(IConfiguration configuration)
{
    public string Token => configuration["MAX_BOT_TOKEN"]?.Trim() ?? "";
    public long ChatId => long.TryParse(configuration["MAX_CHAT_ID"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
    public bool Enabled => Token.Length > 0 && ChatId != 0;
}
public sealed record MaxConnectionInfo(bool Verified, string Message, DateTimeOffset? CheckedAtUtc);
public sealed class MaxConnectionStatus
{
    private MaxConnectionInfo current = new(false, "Ожидается проверка подключения к группе.", null);
    public MaxConnectionInfo Current => Volatile.Read(ref current);
    public void Set(bool verified, string message) => Volatile.Write(ref current, new(verified, message, DateTimeOffset.UtcNow));
}
public static class ConnectionStrings
{
    public static string Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("DATABASE_URL is required.");
        if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return new NpgsqlConnectionStringBuilder(raw) { MaxPoolSize = 30, IncludeErrorDetail = false }.ConnectionString;
        var uri = new Uri(raw); var parts = uri.UserInfo.Split(':', 2);
        if (parts.Length != 2) throw new InvalidOperationException("DATABASE_URL must include user and password.");
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host, Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Username = Uri.UnescapeDataString(parts[0]), Password = Uri.UnescapeDataString(parts[1]),
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')), MaxPoolSize = 30,
            Timeout = 15, CommandTimeout = 60, IncludeErrorDetail = false,
            SslMode = SslMode.VerifyFull
        };
        // Same-region internal Render endpoints can explicitly use sslmode=disable;
        // external connections keep certificate verification.
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var item = pair.Split('=', 2);
            if (item.Length == 2 && item[0] == "sslmode") builder.SslMode = item[1].ToLowerInvariant() switch
            { "disable" => SslMode.Disable, "verify-full" => SslMode.VerifyFull, _ => throw new InvalidOperationException("Use sslmode=verify-full or a private sslmode=disable connection.") };
        }
        if (!uri.Host.Contains('.') && !uri.Query.Contains("sslmode", StringComparison.OrdinalIgnoreCase)) builder.SslMode = SslMode.Disable;
        return builder.ConnectionString;
    }
}
