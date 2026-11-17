using System.Net;
using System.Net.Http.Json;
using InvoiceHub.Api.Endpoints;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceHub.Tests.Api;

public class AuthTests(ApiFactory factory) : ApiTest(factory)
{
    private static object Register(string email, string password = "correct horse battery", string country = "DE", string? vatId = null) => new
    {
        email, password, companyName = "Studio Blau", street = "Hauptstr. 5", postalCode = "80331", city = "München", country, vatId,
    };

    [Fact]
    public async Task Register_login_and_me()
    {
        var client = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", Register("Anna@Studio.example"))).StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "anna@studio.example", password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var seller = await Factory.SellerAsync("other@studio.example");
        var me = await seller.GetFromJsonAsync<SellerProfile>("/api/me", ApiFactory.Json);
        Assert.Equal(("other@studio.example", "DE", "DE123456788", "DE89370400440532013000"), (me!.Email, me.Country, me.VatId, me.Iban));
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_look_the_same()
    {
        var client = Factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", Register("anna@studio.example"));
        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { email = "anna@studio.example", password = "nope nope nope" });
        var unknown = await client.PostAsJsonAsync("/api/auth/login", new { email = "ghost@studio.example", password = "nope nope nope" });
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized), (wrong.StatusCode, unknown.StatusCode));
    }

    [Fact]
    public async Task Duplicate_email_is_a_conflict()
    {
        var client = Factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", Register("anna@studio.example"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/register", Register("ANNA@studio.example"))).StatusCode);
    }

    [Theory]
    [InlineData("not-an-email", "correct horse battery", "DE", null, "Email")]
    [InlineData("a@b.example", "short", "DE", null, "Password")]
    [InlineData("a@b.example", "correct horse battery", "US", null, "country")]
    [InlineData("a@b.example", "correct horse battery", "DE", "DE123456789", "vatId")]
    [InlineData("a@b.example", "correct horse battery", "DE", "ATU12345678", "vatId")]
    public async Task Invalid_registrations_are_rejected_with_field_errors(string email, string password, string country, string? vatId, string field)
    {
        var res = await Factory.CreateClient().PostAsJsonAsync("/api/auth/register", Register(email, password, country, vatId));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains(problem!.Errors.Keys, k => k.Equals(field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Api_requires_a_token()
    {
        var client = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/customers")).StatusCode);
    }
}

public class CustomerTests(ApiFactory factory) : ApiTest(factory)
{
    internal static object FrenchBusiness => new
    {
        name = "Café Lumière SARL", email = "compta@lumiere.example", street = "12 rue de Rivoli", postalCode = "75004",
        city = "Paris", country = "FR", vatId = "FR40303265045",
    };

    [Fact]
    public async Task Crud_and_vat_id_normalization()
    {
        var seller = await Factory.SellerAsync();
        var created = await seller.PostAsJsonAsync("/api/customers", new
        {
            name = "Bäckerei Schulz", street = "Ring 3", postalCode = "50667", city = "Köln", country = "de", vatId = "de 123.456.788",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var customer = (await created.Content.ReadFromJsonAsync<CustomerResponse>(ApiFactory.Json))!;
        Assert.Equal(("DE", "DE123456788"), (customer.Country, customer.VatId));

        var update = await seller.PutAsJsonAsync($"/api/customers/{customer.Id}", new
        {
            name = "Bäckerei Schulz & Söhne", street = "Ring 3", postalCode = "50667", city = "Köln", country = "DE", vatId = (string?)null,
        });
        Assert.Equal("Bäckerei Schulz & Söhne", (await update.Content.ReadFromJsonAsync<CustomerResponse>(ApiFactory.Json))!.Name);

        Assert.Single((await seller.GetFromJsonAsync<List<CustomerResponse>>("/api/customers", ApiFactory.Json))!);
        Assert.Equal(HttpStatusCode.NoContent, (await seller.DeleteAsync($"/api/customers/{customer.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await seller.GetAsync($"/api/customers/{customer.Id}")).StatusCode);
    }

    [Fact]
    public async Task Vat_id_must_match_customer_country()
    {
        var seller = await Factory.SellerAsync();
        var res = await seller.PostAsJsonAsync("/api/customers", new
        {
            name = "X", street = "Y", postalCode = "1", city = "Z", country = "IT", vatId = "FR40303265045",
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Sellers_cannot_see_each_others_customers()
    {
        var anna = await Factory.SellerAsync("anna@studio.example");
        var ben = await Factory.SellerAsync("ben@studio.example");
        var created = await (await anna.PostAsJsonAsync("/api/customers", FrenchBusiness)).Content.ReadFromJsonAsync<CustomerResponse>(ApiFactory.Json);

        Assert.Empty((await ben.GetFromJsonAsync<List<CustomerResponse>>("/api/customers", ApiFactory.Json))!);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/customers/{created!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.DeleteAsync($"/api/customers/{created.Id}")).StatusCode);
    }
}
