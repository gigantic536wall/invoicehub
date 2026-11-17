using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using InvoiceHub.Api.Auth;
using InvoiceHub.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceHub.Tests.Api;

/// <summary>
/// Runs the real API against a real PostgreSQL test database.
/// Connection string: ConnectionStrings:Test from user secrets (local) or ConnectionStrings__Test (CI).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development"); // loads user secrets of the API project
        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            var built = config.Build();
            var test = built.GetConnectionString("Test")
                ?? throw new InvalidOperationException("Set ConnectionStrings:Test (user secrets) or ConnectionStrings__Test (env).");
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = test,
                ["RateLimiting:AuthPerMinute"] = "100000",
                ["Jwt:Key"] = built["Jwt:Key"] ?? "test-signing-key-test-signing-key-123456",
            });
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public async Task ResetAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE invoice_lines, invoices, invoice_counters, customers, users RESTART IDENTITY CASCADE");
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    /// <summary>Registers a seller and returns a client that sends its token.</summary>
    public async Task<HttpClient> SellerAsync(string email = "owner@kaffeewerk.example", string country = "DE", string? vatId = "DE123456788")
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "correct horse battery",
            companyName = "Kaffeewerk GmbH",
            street = "Musterstraße 1",
            postalCode = "10115",
            city = "Berlin",
            country,
            vatId,
            iban = "DE89 3704 0044 0532 0130 00",
        });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }
}

[CollectionDefinition("api")]
public sealed class ApiTestGroup : ICollectionFixture<ApiFactory>;

/// <summary>Base class: every test starts with an empty database.</summary>
[Collection("api")]
public abstract class ApiTest(ApiFactory factory) : IAsyncLifetime
{
    protected ApiFactory Factory { get; } = factory;

    public Task InitializeAsync() => Factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;
}
