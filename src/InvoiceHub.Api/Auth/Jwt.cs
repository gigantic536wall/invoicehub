using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using InvoiceHub.Api.Data;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace InvoiceHub.Api.Auth;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    /// <summary>Signing key, at least 32 characters. Set it via environment variable Jwt__Key, never in source code.</summary>
    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "InvoiceHub";
    public string Audience { get; set; } = "InvoiceHub";
    public int LifetimeMinutes { get; set; } = 480;

    public SymmetricSecurityKey SigningKey()
    {
        if (Key.Length < 32)
        {
            throw new InvalidOperationException("Jwt:Key must be set and at least 32 characters long.");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));
    }
}

public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt);

public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    public TokenResponse Create(User user)
    {
        var o = options.Value;
        var expires = clock.GetUtcNow().AddMinutes(o.LifetimeMinutes);
        var token = new JwtSecurityToken(
            o.Issuer,
            o.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new Claim(JwtRegisteredClaimNames.Email, user.Email)],
            notBefore: clock.GetUtcNow().UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(o.SigningKey(), SecurityAlgorithms.HmacSha256));
        return new TokenResponse(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>The signed-in seller. Endpoints use it to scope every query to the caller's own data.</summary>
    public static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("No user id in token."));
}
