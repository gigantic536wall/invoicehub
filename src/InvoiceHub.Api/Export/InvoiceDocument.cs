using InvoiceHub.Api.Data;
using InvoiceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Api.Export;

public sealed record Party(
    string Name, string Street, string PostalCode, string City, string Country,
    string? VatId, string? Email, string? ContactName = null, string? Phone = null);

/// <summary>Everything needed to render an invoice (PDF or XRechnung): the invoice plus seller and buyer data.</summary>
public sealed record InvoiceDocument(Invoice Invoice, Party Seller, Party Buyer, string? Iban)
{
    public InvoiceTotals Totals { get; } = Invoice.CalculateTotals();

    public static async Task<InvoiceDocument?> LoadAsync(AppDbContext db, Guid ownerId, Guid invoiceId, CancellationToken ct)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.Id == invoiceId && i.OwnerId == ownerId, ct);
        if (invoice is null)
        {
            return null;
        }

        var seller = await db.Users.AsNoTracking().SingleAsync(u => u.Id == ownerId, ct);
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == invoice.CustomerId, ct);
        return new InvoiceDocument(
            invoice,
            new Party(seller.CompanyName, seller.Street, seller.PostalCode, seller.City, seller.Country, seller.VatId, seller.Email,
                seller.ContactName, seller.Phone),
            new Party(customer.Name, customer.Street, customer.PostalCode, customer.City, customer.Country, customer.VatId, customer.Email),
            seller.Iban);
    }
}
