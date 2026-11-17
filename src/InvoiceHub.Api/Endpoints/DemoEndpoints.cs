using System.Security.Cryptography;
using InvoiceHub.Api.Auth;
using InvoiceHub.Api.Data;
using InvoiceHub.Api.Services;
using InvoiceHub.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Api.Endpoints;

/// <summary>
/// One-click demo account for the public demo: a seller with a complete profile, customers in three countries
/// and invoices for each VAT situation. Demo accounts are deleted after 24 hours.
/// </summary>
public static class DemoEndpoints
{
    public const string EmailDomain = "@demo.invoicehub.invalid";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public static void MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<IConfiguration>().GetValue("Demo:Enabled", false))
        {
            return;
        }

        app.MapPost("/api/demo", CreateDemo).WithTags("Auth").RequireRateLimiting("auth")
            .WithSummary("Create a demo account with sample data and get an access token (deleted after 24 h)");
    }

    private static async Task<Created<TokenResponse>> CreateDemo(
        AppDbContext db, TokenService tokens, IPasswordHasher<User> hasher, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await DeleteExpiredAsync(db, now - Lifetime, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var seller = new User
        {
            Email = $"demo-{Guid.NewGuid():N}"[..13] + EmailDomain,
            CompanyName = "Kaffeewerk GmbH",
            Street = "Musterstraße 1",
            PostalCode = "10115",
            City = "Berlin",
            Country = "DE",
            VatId = "DE123456788",
            Iban = "DE89370400440532013000",
            ContactName = "Erika Muster",
            Phone = "+49 30 1234567",
            InvoicePrefix = "RE-",
        };
        // Nobody can sign in with the password: the token below is the only way into a demo account.
        seller.PasswordHash = hasher.HashPassword(seller, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        db.Users.Add(seller);

        Customer NewCustomer(string name, string email, string street, string postalCode, string city, string country, string? vatId)
        {
            var c = new Customer
            {
                OwnerId = seller.Id, Name = name, Email = email, Street = street, PostalCode = postalCode, City = city, Country = country, VatId = vatId,
            };
            db.Customers.Add(c);
            return c;
        }

        var bakery = NewCustomer("Bäckerei Schulz GmbH", "einkauf@schulz.example", "Ring 3", "50667", "Köln", "DE", "DE136695976");
        var cafe = NewCustomer("Café Lumière SARL", "compta@lumiere.example", "12 rue de Rivoli", "75004", "Paris", "FR", "FR40303265045");
        var roastery = NewCustomer("Blue Bottle Inc.", "ap@bluebottle.example", "1 Market St", "94105", "San Francisco", "US", null);
        await db.SaveChangesAsync(ct);

        var today = DateOnly.FromDateTime(now.UtcDateTime);

        Invoice NewInvoice(Customer customer, SupplyType supply, string reference, params (string Description, decimal Qty, decimal Price, string Unit, decimal? Rate, decimal Discount)[] lines)
        {
            var tax = TaxRules.Determine(seller.Country, customer.Country, customer.VatId is null ? null : VatId.Parse(customer.VatId), supply);
            var invoice = new Invoice(seller.Id, customer.Id);
            foreach (var l in lines)
            {
                invoice.AddLine(new InvoiceLine(l.Description, l.Qty, l.Price, tax.Category, tax.Category.HasRate() ? l.Rate ?? tax.Rate : 0m, l.Discount, l.Unit));
            }

            invoice.SetNotes("Thank you for your order!", tax.InvoiceNote);
            invoice.SetBuyerReference(reference);
            db.Invoices.Add(invoice);
            return invoice;
        }

        async Task IssueAsync(Invoice invoice, DateOnly date)
        {
            invoice.Issue(await InvoiceNumbering.NextAsync(db, seller.Id, seller.InvoicePrefix, date.Year, ct), date, paymentTermDays: 14);
        }

        var domestic = NewInvoice(bakery, SupplyType.Goods, "PO-4711",
            ("Espressomaschine Classic", 2, 349m, "C62", null, 10m),
            ("Kaffeebohnen Ethiopia 1 kg", 12, 16.25m, "C62", 7m, 0m));
        var reverseCharge = NewInvoice(cafe, SupplyType.Services, "BC-2026-118",
            ("Barista training", 2, 650m, "DAY", null, 0m));
        NewInvoice(roastery, SupplyType.Goods, "PO-88213",
            ("Kaffeemühle Pro", 3, 189m, "C62", null, 0m)); // stays a draft

        await IssueAsync(reverseCharge, today.AddDays(-20));
        reverseCharge.MarkPaid(today.AddDays(-6));
        await IssueAsync(domestic, today);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return TypedResults.Created("/api/me", tokens.Create(seller));
    }

    private static async Task DeleteExpiredAsync(AppDbContext db, DateTimeOffset createdBefore, CancellationToken ct)
    {
        var expired = db.Users.Where(u => u.Email.EndsWith(EmailDomain) && u.CreatedAt < createdBefore).Select(u => u.Id);
        await db.Invoices.Where(i => expired.Contains(i.OwnerId)).ExecuteDeleteAsync(ct);
        await db.Customers.Where(c => expired.Contains(c.OwnerId)).ExecuteDeleteAsync(ct);
        await db.InvoiceCounters.Where(c => expired.Contains(c.OwnerId)).ExecuteDeleteAsync(ct);
        await db.Users.Where(u => u.Email.EndsWith(EmailDomain) && u.CreatedAt < createdBefore).ExecuteDeleteAsync(ct);
    }
}
