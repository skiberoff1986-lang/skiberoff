using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CraneJournal.Web.Api;
using CraneJournal.Web.Data;
using CraneJournal.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
int port = int.TryParse(builder.Configuration["PORT"], out var value) ? value : 8080;
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 32 * 1024 * 1024);
string database = ConnectionStrings.Parse(builder.Configuration["DATABASE_URL"] ?? "");
builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(database));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BusinessClock>();
builder.Services.AddSingleton<MaxOptions>();
builder.Services.AddSingleton<MaxConnectionStatus>();
builder.Services.AddScoped<AccessService>();
builder.Services.AddScoped<CommandRunner>();
builder.Services.AddScoped<RecordService>();
builder.Services.AddScoped<MediaService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddIdentity<AppUser, IdentityRole<Guid>>(options =>
{
    options.Password.RequiredLength = 14; options.Password.RequiredUniqueChars = 6;
    options.Password.RequireDigit = true; options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false; options.Password.RequireNonAlphanumeric = false;
    options.Lockout.MaxFailedAccessAttempts = 5; options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.User.RequireUniqueEmail = false;
}).AddEntityFrameworkStores<AppDb>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "crane-session"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(12); options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.AddAuthorization();
builder.Services.AddDataProtection().SetApplicationName("CraneJournal.Cloud.v1").PersistKeysToDbContext<AppDb>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "crane-csrf"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));
builder.Services.AddHostedService<OutboxWorker>();
// HttpClient instances used for MAX are owned by the worker; signed URLs and tokens are not logged.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

var app = builder.Build();
bool renderEdge = builder.Configuration.GetValue<bool>("RENDER_TLS_TERMINATION");
string publicAddress = builder.Configuration["PUBLIC_URL"] ?? builder.Configuration["RENDER_EXTERNAL_URL"] ?? "";
Uri? publicUri = null;
if (!app.Environment.IsDevelopment())
{
    if (!Uri.TryCreate(publicAddress, UriKind.Absolute, out publicUri) || publicUri.Scheme != "https" ||
        publicUri.AbsolutePath != "/" || publicUri.Query.Length > 0 || publicUri.Fragment.Length > 0 || publicUri.UserInfo.Length > 0)
        throw new InvalidOperationException("Production requires an HTTPS PUBLIC_URL (Render supplies RENDER_EXTERNAL_URL).");
    if (renderEdge && !string.Equals(builder.Configuration["RENDER"], "true", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("RENDER_TLS_TERMINATION can only be enabled inside Render.");
}
if (!renderEdge)
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    foreach (string network in (builder.Configuration["TRUSTED_PROXY_NETWORKS"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    app.UseForwardedHeaders(forwarded);
}
app.Use(async (ctx, next) =>
{
    // Render accepts public traffic at its HTTPS edge; the application port is internal.
    // Use the configured origin, never an arbitrary Host or forwarded-proto value.
    if (!app.Environment.IsDevelopment() && ctx.Request.Path != "/healthz")
    {
        if (!string.Equals(ctx.Request.Host.Host, publicUri!.Host, StringComparison.OrdinalIgnoreCase))
        { ctx.Response.StatusCode = 400; return; }
        if (renderEdge) ctx.Request.Scheme = "https";
        if (!ctx.Request.IsHttps) { ctx.Response.StatusCode = 426; return; }
        ctx.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
    }
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'; media-src 'self' blob:; manifest-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers["Cache-Control"] = "no-store";
    try { await next(ctx); }
    catch (ApiError ex) when (!ctx.Response.HasStarted)
    { ctx.Response.StatusCode = ex.Status; await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (AntiforgeryValidationException) when (!ctx.Response.HasStarted)
    { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "Сессия формы истекла. Обновите страницу и повторите действие." }); }
    catch (Exception ex) when (!ctx.Response.HasStarted && (ex is InvalidDataException or JsonException or BadHttpRequestException))
    { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = ex is InvalidDataException ? ex.Message : "Проверьте формат отправленных данных." }); }
    catch (Exception ex) when (!ctx.Response.HasStarted && (ex is DbUpdateConcurrencyException ||
        ex is DbUpdateException { InnerException: PostgresException { SqlState: "23505" or "40001" or "40P01" } } ||
        ex is PostgresException { SqlState: "23505" or "40001" or "40P01" }))
    { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "Данные уже изменились. Обновите заявку. Повтор той же команды безопасен." }); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex) when (!ctx.Response.HasStarted)
    {
        app.Logger.LogError("Request failed with {ErrorType}; trace {Trace}.", ex.GetType().Name, ctx.TraceIdentifier);
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "Операция не подтверждена. Повторите ту же команду; не создавайте новую заявку повторно.", trace = ctx.TraceIdentifier });
    }
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && (HttpMethods.IsPost(ctx.Request.Method) ||
        HttpMethods.IsPut(ctx.Request.Method) || HttpMethods.IsDelete(ctx.Request.Method) || HttpMethods.IsPatch(ctx.Request.Method)))
        await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
    await next(ctx);
});
app.UseDefaultFiles();
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".mjs"] = "text/javascript";
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });
app.MapGet("/healthz", async (AppDb db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503));
Endpoints.Map(app);
app.MapFallback("/api/{**path}", async context =>
{
    context.Response.StatusCode = 404;
    await context.Response.WriteAsJsonAsync(new { error = "Адрес API не найден." });
});
app.MapFallbackToFile("index.html");
await Bootstrap.Initialize(app.Services, builder.Configuration);
await app.RunAsync();

public partial class Program { }
