using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace CraneJournal.Checks;

internal static class HttpChecks
{
    public static async Task Run(string connection, Action<bool, string> check)
    {
        string webDll = Path.GetFullPath("src/CraneJournal.Web/bin/Release/net10.0/CraneJournal.Web.dll");
        if (!File.Exists(webDll)) throw new Exception("Run checks in Release configuration from the project root.");
        var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
        int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add(webDll);
        start.Environment["DATABASE_URL"] = connection;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_CONTENTROOT"] = Path.GetFullPath("src/CraneJournal.Web");
        start.Environment["PORT"] = port.ToString();
        start.Environment["BUSINESS_TIMEZONE"] = "Europe/Moscow";
        start.Environment["MAX_BOT_TOKEN"] = ""; start.Environment["MAX_CHAT_ID"] = "";
        start.Environment["RENDER_TLS_TERMINATION"] = "false";
        using var process = Process.Start(start) ?? throw new Exception("Cannot launch application for HTTP checks.");
        HttpClient Client() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(15) };
        using var client = Client();
        try
        {
            bool ready = false;
            for (int i = 0; i < 40; i++)
            {
                if (process.HasExited) throw new Exception("Application exited during HTTP checks.");
                try { using var health = await client.GetAsync("healthz"); ready = health.IsSuccessStatusCode; }
                catch (HttpRequestException) { }
                if (ready) break;
                await Task.Delay(500);
            }
            check(ready, "real application starts and health check connects to PostgreSQL");
            using (var anon = await client.GetAsync("api/records")) check(anon.StatusCode == HttpStatusCode.Unauthorized, "HTTP anonymous journal access denied");
            string csrf = await Csrf(client);
            using (var bad = await client.PostAsJsonAsync("api/login", new { login = "admin", password = "Ephemeral-check-password-2026!" }))
                check(bad.StatusCode == HttpStatusCode.BadRequest, "login without antiforgery token rejected");
            csrf = await Login(client, "admin", "Ephemeral-check-password-2026!", csrf);
            using (var home = await client.GetAsync("/"))
            {
                check(home.IsSuccessStatusCode && (await home.Content.ReadAsStringAsync()).Contains("Журнал кранов"), "real application serves mobile interface");
                check(home.Headers.Contains("Content-Security-Policy"), "interface has content security policy");
            }
            var commandId = Guid.NewGuid();
            var create = new { commandId, shop = "01", crane = "HTTP", fault = "HTTP integration", eventAt = (string?)null };
            using var first = await Send(client, csrf, "api/records", create);
            first.EnsureSuccessStatusCode();
            using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            string id = firstJson.RootElement.GetProperty("id").GetString()!;
            using var repeat = await Send(client, csrf, "api/records", create);
            repeat.EnsureSuccessStatusCode();
            using var repeatJson = JsonDocument.Parse(await repeat.Content.ReadAsStringAsync());
            check(repeatJson.RootElement.GetProperty("id").GetString() == id, "HTTP retry returns same record ID");
            using var response = await Send(client, csrf, $"api/records/{id}/commands", new { commandId = Guid.NewGuid(), expectedRevision = 1, kind = "Responded" });
            response.EnsureSuccessStatusCode();
            using var stale = await Send(client, csrf, $"api/records/{id}/commands", new { commandId = Guid.NewGuid(), expectedRevision = 1, kind = "Responded" });
            check(stale.StatusCode == HttpStatusCode.Conflict, "HTTP stale revision returns conflict");
            using var recordResponse = await client.GetAsync($"api/records/{id}"); recordResponse.EnsureSuccessStatusCode();
            using var details = JsonDocument.Parse(await recordResponse.Content.ReadAsStringAsync());
            check(details.RootElement.GetProperty("events")[0].GetProperty("actorName").GetString() == "Администратор", "HTTP history attributes event to authenticated actor");
            using var outsider = Client(); await Login(outsider, "other", "Local-testing-password-2026!", await Csrf(outsider));
            using var hidden = await outsider.GetAsync($"api/records/{id}");
            check(hidden.StatusCode == HttpStatusCode.NotFound, "HTTP other shop cannot retrieve record");
            using var viewer = Client(); var viewerCsrf = await Login(viewer, "viewer", "Local-testing-password-2026!", await Csrf(viewer));
            using var forbidden = await Send(viewer, viewerCsrf, "api/records", create with { commandId = Guid.NewGuid() });
            check(forbidden.StatusCode == HttpStatusCode.Forbidden, "HTTP viewer cannot create record");
            using var users = await viewer.GetAsync("api/admin/users");
            check(users.StatusCode == HttpStatusCode.Forbidden, "HTTP operator-facing session cannot administer users");
            using var logout = await Send(client, csrf, "api/logout", new { }); logout.EnsureSuccessStatusCode();
            using var loggedOut = await client.GetAsync("api/records");
            check(loggedOut.StatusCode == HttpStatusCode.Unauthorized, "logout removes access");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("api/session"); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("csrf").GetString()!;
    }
    private static async Task<string> Login(HttpClient client, string login, string password, string csrf)
    {
        using var response = await Send(client, csrf, "api/login", new { login, password }); response.EnsureSuccessStatusCode();
        return await Csrf(client); // antiforgery token is bound to the authenticated identity
    }
    private static async Task<HttpResponseMessage> Send(HttpClient client, string csrf, string path, object data)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(data) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }
}
