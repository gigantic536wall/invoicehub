namespace InvoiceHub.Api.Data;

/// <summary>A business that issues invoices (one login = one seller).</summary>
public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public required string CompanyName { get; set; }
    public required string Street { get; set; }
    public required string PostalCode { get; set; }
    public required string City { get; set; }
    /// <summary>ISO 3166-1 alpha-2, e.g. DE.</summary>
    public required string Country { get; set; }
    public string? VatId { get; set; }
    public string? Iban { get; set; }
    public string InvoicePrefix { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    public string? Email { get; set; }
    public required string Street { get; set; }
    public required string PostalCode { get; set; }
    public required string City { get; set; }
    public required string Country { get; set; }
    /// <summary>Set for business customers; decides reverse charge within the EU.</summary>
    public string? VatId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Last used invoice number per seller and year. Incremented atomically when an invoice is issued.</summary>
public sealed class InvoiceCounter
{
    public Guid OwnerId { get; set; }
    public int Year { get; set; }
    public int LastNumber { get; set; }
}
