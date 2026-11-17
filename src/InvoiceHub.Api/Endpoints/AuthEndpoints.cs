using System.ComponentModel.DataAnnotations;
using InvoiceHub.Api.Auth;
using InvoiceHub.Api.Data;
using InvoiceHub.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Api.Endpoints;

public sealed record RegisterRequest(
    [property: Required, EmailAddress, MaxLength(254)] string Email,
    [property: Required, MinLength(10), MaxLength(200)] string Password,
    [property: Required, MaxLength(200)] string CompanyName,
    [property: Required, MaxLength(200)] string Street,
    [property: Required, MaxLength(20)] string PostalCode,
    [property: Required, MaxLength(100)] string City,
    [property: Required, StringLength(2, MinimumLength = 2)] string Country,
    string? VatId,
    [property: MaxLength(34)] string? Iban);

public sealed record LoginRequest([property: Required] string Email, [property: Required] string Password);

public sealed record SellerProfile(
    Guid Id, string Email, string CompanyName, string Street, string PostalCode, string City, string Country,
    string? VatId, string? Iban);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth").RequireRateLimiting("auth");

        auth.MapPost("/register", Register).WithSummary("Create a seller account and get an access token");
        auth.MapPost("/login", Login).WithSummary("Get an access token");

        app.MapGet("/api/me", Me).WithTags("Auth").RequireAuthorization().WithSummary("The signed-in seller");
    }

    private static async Task<Results<Created<TokenResponse>, Conflict<string>, ValidationProblem>> Register(
        RegisterRequest req, AppDbContext db, TokenService tokens, IPasswordHasher<User> hasher, CancellationToken ct)
    {
        var country = EuCountries.Normalize(req.Country);
        if (!EuCountries.IsMember(country))
        {
            return Problems.Field("country", "Sellers must be based in an EU member state.");
        }

        string? vatId = null;
        if (!string.IsNullOrWhiteSpace(req.VatId))
        {
            if (!VatId.TryParse(req.VatId, out var parsed, out var error))
            {
                return Problems.Field("vatId", error!);
            }

            if (parsed.Country != country)
            {
                return Problems.Field("vatId", "The VAT ID must be from the seller's country.");
            }

            vatId = parsed.Value;
        }

        var email = req.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return TypedResults.Conflict("An account with this email already exists.");
        }

        var user = new User
        {
            Email = email,
            CompanyName = req.CompanyName.Trim(),
            Street = req.Street.Trim(),
            PostalCode = req.PostalCode.Trim(),
            City = req.City.Trim(),
            Country = country,
            VatId = vatId,
            Iban = req.Iban?.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant(),
        };
        user.PasswordHash = hasher.HashPassword(user, req.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created("/api/me", tokens.Create(user));
    }

    private static async Task<Results<Ok<TokenResponse>, UnauthorizedHttpResult>> Login(
        LoginRequest req, AppDbContext db, TokenService tokens, IPasswordHasher<User> hasher, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            // Hash anyway, so the response time does not reveal whether the email exists.
            hasher.HashPassword(new User { Email = email, CompanyName = "", Street = "", PostalCode = "", City = "", Country = "" }, req.Password);
            return TypedResults.Unauthorized();
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            return TypedResults.Unauthorized();
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, req.Password);
            await db.SaveChangesAsync(ct);
        }

        return TypedResults.Ok(tokens.Create(user));
    }

    private static async Task<Results<Ok<SellerProfile>, NotFound>> Me(System.Security.Claims.ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var u = await db.Users.FindAsync([principal.UserId()], ct);
        return u is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new SellerProfile(u.Id, u.Email, u.CompanyName, u.Street, u.PostalCode, u.City, u.Country, u.VatId, u.Iban));
    }
}

public static class Problems
{
    public static ValidationProblem Field(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
