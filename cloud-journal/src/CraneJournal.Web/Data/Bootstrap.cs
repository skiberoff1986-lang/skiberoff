using CraneJournal.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CraneJournal.Web.Data;

public static class Bootstrap
{
    public static async Task Initialize(IServiceProvider services, IConfiguration config)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        await using var leader = new NpgsqlConnection(db.Database.GetConnectionString());
        await leader.OpenAsync();
        await using var take = new NpgsqlCommand("SELECT pg_advisory_lock(831614073265)", leader);
        await take.ExecuteNonQueryAsync();
        try
        {
            // First release: create the schema only in a new, empty database.
            // Future versions must supply an explicit migration; this never drops data.
            bool created = await db.Database.EnsureCreatedAsync();
            var schema = await db.SchemaStates.SingleOrDefaultAsync();
            if (!created && schema?.Version != 1)
                throw new InvalidOperationException("Unknown database schema. Do not replace the existing database; apply the matching migration.");
            if (created)
            {
                db.SchemaStates.Add(new() { Id = 1, Version = 1 });
                await db.SaveChangesAsync();
            }
            await db.Database.ExecuteSqlRawAsync("""
                CREATE OR REPLACE FUNCTION crane_journal_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Journal events and command receipts are immutable'; END; $$;
                DO $guard$ BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname='events_immutable') THEN
                    CREATE TRIGGER events_immutable BEFORE UPDATE OR DELETE ON "Events" FOR EACH ROW EXECUTE FUNCTION crane_journal_immutable();
                  END IF;
                  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname='commands_immutable') THEN
                    CREATE TRIGGER commands_immutable BEFORE UPDATE OR DELETE ON "Commands" FOR EACH ROW EXECUTE FUNCTION crane_journal_immutable();
                  END IF;
                END; $guard$;
                """);
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            foreach (string name in new[] { Roles.Admin, Roles.Operator, Roles.Viewer })
                if (!await roles.RoleExistsAsync(name)) Check(await roles.CreateAsync(new(name)));
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            if (!await users.Users.AnyAsync())
            {
                string password = config["BOOTSTRAP_PASSWORD"] ?? throw new InvalidOperationException("BOOTSTRAP_PASSWORD is required for the first administrator.");
                var user = new AppUser { Id = Guid.NewGuid(), UserName = config["BOOTSTRAP_LOGIN"] ?? "admin", FullName = "Администратор", MustChangePassword = true };
                await using var tx = await db.Database.BeginTransactionAsync();
                Check(await users.CreateAsync(user, password));
                Check(await users.AddToRoleAsync(user, Roles.Admin));
                await tx.CommitAsync();
            }
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(831614073265)", leader);
            await release.ExecuteNonQueryAsync();
        }
    }
    public static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new ApiError(400, string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}
