using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using InvoiceHub.Api.Auth;
using InvoiceHub.Api.Data;
using InvoiceHub.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Api.Endpoints;

public sealed record CustomerRequest(
    [property: Required, MaxLength(200)] string Name,
    [property: EmailAddress, MaxLength(254)] string? Email,
    [property: Required, MaxLength(200)] string Street,
    [property: Required, MaxLength(20)] string PostalCode,
    [property: Required, MaxLength(100)] string City,
    [property: Required, StringLength(2, MinimumLength = 2)] string Country,
    string? VatId);

public sealed record CustomerResponse(
    Guid Id, string Name, string? Email, string Street, string PostalCode, string City, string Country, string? VatId)
{
    public static CustomerResponse From(Customer c) => new(c.Id, c.Name, c.Email, c.Street, c.PostalCode, c.City, c.Country, c.VatId);
}

public static class CustomerEndpoints
{
    public static void MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/customers").WithTags("Customers").RequireAuthorization();
        g.MapGet("/", List).WithSummary("All customers of the signed-in seller");
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/", Create).WithSummary("Add a customer (with VAT ID for business customers)");
        g.MapPut("/{id:guid}", Update);
        g.MapDelete("/{id:guid}", Delete).WithSummary("Delete a customer without invoices");
    }

    private static async Task<Ok<List<CustomerResponse>>> List(ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Customers.AsNoTracking()
            .Where(c => c.OwnerId == user.UserId())
            .OrderBy(c => c.Name)
            .Select(c => new CustomerResponse(c.Id, c.Name, c.Email, c.Street, c.PostalCode, c.City, c.Country, c.VatId))
            .ToListAsync(ct));

    private static async Task<Results<Ok<CustomerResponse>, NotFound>> Get(Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var c = await Find(db, user, id, ct);
        return c is null ? TypedResults.NotFound() : TypedResults.Ok(CustomerResponse.From(c));
    }

    private static async Task<Results<Created<CustomerResponse>, ValidationProblem>> Create(
        CustomerRequest req, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var customer = new Customer
        {
            OwnerId = user.UserId(), Name = "", Street = "", PostalCode = "", City = "", Country = "",
        };
        if (Apply(customer, req) is { } problem)
        {
            return problem;
        }

        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/customers/{customer.Id}", CustomerResponse.From(customer));
    }

    private static async Task<Results<Ok<CustomerResponse>, NotFound, ValidationProblem>> Update(
        Guid id, CustomerRequest req, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var customer = await Find(db, user, id, ct);
        if (customer is null)
        {
            return TypedResults.NotFound();
        }

        if (Apply(customer, req) is { } problem)
        {
            return problem;
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(CustomerResponse.From(customer));
    }

    private static async Task<Results<NoContent, NotFound, Conflict<string>>> Delete(
        Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var customer = await Find(db, user, id, ct);
        if (customer is null)
        {
            return TypedResults.NotFound();
        }

        if (await db.Invoices.AnyAsync(i => i.CustomerId == id, ct))
        {
            return TypedResults.Conflict("This customer has invoices, which must be kept. Delete is not possible.");
        }

        db.Customers.Remove(customer);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static Task<Customer?> Find(AppDbContext db, ClaimsPrincipal user, Guid id, CancellationToken ct) =>
        db.Customers.SingleOrDefaultAsync(c => c.Id == id && c.OwnerId == user.UserId(), ct);

    private static ValidationProblem? Apply(Customer c, CustomerRequest req)
    {
        var country = EuCountries.Normalize(req.Country);
        string? vatId = null;
        if (!string.IsNullOrWhiteSpace(req.VatId))
        {
            if (!VatId.TryParse(req.VatId, out var parsed, out var error))
            {
                return Problems.Field("vatId", error!);
            }

            if (parsed.Country != country)
            {
                return Problems.Field("vatId", $"The VAT ID is from {parsed.Country}, but the customer is in {country}.");
            }

            vatId = parsed.Value;
        }

        c.Name = req.Name.Trim();
        c.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
        c.Street = req.Street.Trim();
        c.PostalCode = req.PostalCode.Trim();
        c.City = req.City.Trim();
        c.Country = country;
        c.VatId = vatId;
        return null;
    }
}
