using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InvoiceHub.Api.Auth;
using InvoiceHub.Api.Data;
using InvoiceHub.Api.Endpoints;
using InvoiceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceHub.Tests.Api;

public class DemoTests(ApiFactory factory) : ApiTest(factory)
{
    private async Task<HttpClient> DemoClient()
    {
        var client = Factory.CreateClient();
        var res = await client.PostAsync("/api/demo", null);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var token = (await res.Content.ReadFromJsonAsync<TokenResponse>(ApiFactory.Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    [Fact]
    public async Task Demo_account_has_sample_data_ready_for_export()
    {
        var client = await DemoClient();
        var invoices = (await client.GetFromJsonAsync<List<InvoiceSummary>>("/api/invoices", ApiFactory.Json))!;
        Assert.Equal(3, invoices.Count);
        Assert.Equal(
            [InvoiceStatus.Draft, InvoiceStatus.Issued, InvoiceStatus.Paid],
            invoices.Select(i => i.Status).Order());

        var issued = invoices.Single(i => i.Status == InvoiceStatus.Issued);
        Assert.Equal(956.21m, issued.GrossTotal);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/invoices/{issued.Id}/xrechnung")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/invoices/{issued.Id}/pdf")).StatusCode);
    }

    [Fact]
    public async Task Demo_accounts_are_isolated_and_expire()
    {
        var first = await DemoClient();
        var second = await DemoClient();
        var firstInvoices = (await first.GetFromJsonAsync<List<InvoiceSummary>>("/api/invoices", ApiFactory.Json))!;
        Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync($"/api/invoices/{firstInvoices[0].Id}")).StatusCode);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.CreatedAt, DateTimeOffset.UtcNow.AddDays(-2)));
        }

        await DemoClient(); // creating a demo account removes the expired ones

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.Users.CountAsync());
            Assert.Equal(3, await db.Invoices.CountAsync());
        }
    }
}

public class ConnectionStringTests
{
    [Fact]
    public void Postgres_urls_are_converted()
    {
        var cs = ConnectionStrings.Normalize("postgresql://app%40x:p%2Fss@ep-cool-1.eu-central-1.aws.neon.tech/invoicehub?sslmode=require&channel_binding=require");
        var b = new Npgsql.NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("ep-cool-1.eu-central-1.aws.neon.tech", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Equal("invoicehub", b.Database);
        Assert.Equal("app@x", b.Username);
        Assert.Equal("p/ss", b.Password);
        Assert.Equal(Npgsql.SslMode.Require, b.SslMode);
    }

    [Fact]
    public void Npgsql_strings_are_kept()
    {
        const string cs = "Host=localhost;Database=x;Username=y";
        Assert.Equal(cs, ConnectionStrings.Normalize(cs));
    }
}
