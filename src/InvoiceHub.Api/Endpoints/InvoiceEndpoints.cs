using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using InvoiceHub.Api.Auth;
using InvoiceHub.Api.Data;
using InvoiceHub.Api.Export;
using InvoiceHub.Api.Services;
using InvoiceHub.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Api.Endpoints;

public sealed record LineRequest(
    [property: Required, MaxLength(500)] string Description,
    [property: Range(typeof(decimal), "0.0001", "1000000")] decimal Quantity,
    [property: Range(typeof(decimal), "0", "100000000")] decimal UnitPrice,
    [property: StringLength(3, MinimumLength = 2)] string Unit = "C62",
    [property: Range(typeof(decimal), "0", "100")] decimal DiscountPercent = 0,
    /// <summary>Optional reduced rate (e.g. 7 for books or food in Germany). Default: the seller's standard rate.</summary>
    [property: Range(typeof(decimal), "0.01", "30")] decimal? VatRate = null);

public sealed record InvoiceRequest(
    Guid CustomerId,
    SupplyType SupplyType,
    [property: Required, MinLength(1), MaxLength(200)] List<LineRequest> Lines,
    [property: StringLength(3, MinimumLength = 3)] string Currency = "EUR",
    [property: MaxLength(2000)] string? Note = null,
    /// <summary>Buyer reference (BT-10): purchase order number, or the Leitweg-ID for German public authorities.</summary>
    [property: MaxLength(100)] string? BuyerReference = null);

public sealed record IssueRequest(DateOnly? IssueDate = null, [property: Range(0, 180)] int PaymentTermDays = 14);
public sealed record PayRequest(DateOnly PaidOn);
public sealed record CancelRequest([property: Required, MaxLength(500)] string Reason);

public sealed record LineResponse(
    string Description, decimal Quantity, string Unit, decimal UnitPrice, decimal DiscountPercent,
    VatCategory VatCategory, string VatCategoryCode, decimal VatRate, decimal NetAmount);

public sealed record VatBreakdownResponse(VatCategory Category, string CategoryCode, decimal Rate, decimal TaxableAmount, decimal TaxAmount);

public sealed record InvoiceResponse(
    Guid Id, string? Number, InvoiceStatus Status, Guid CustomerId, string Currency,
    DateOnly? IssueDate, DateOnly? DueDate, DateOnly? PaidOn, string? Note, string? TaxNote, string? BuyerReference, string? CancellationReason,
    IReadOnlyList<LineResponse> Lines, IReadOnlyList<VatBreakdownResponse> VatBreakdown,
    decimal NetTotal, decimal VatTotal, decimal GrossTotal, decimal AmountDue)
{
    public static InvoiceResponse From(Invoice i)
    {
        var t = i.CalculateTotals();
        return new InvoiceResponse(
            i.Id, i.Number, i.Status, i.CustomerId, i.Currency, i.IssueDate, i.DueDate, i.PaidOn, i.Note, i.TaxNote,
            i.BuyerReference, i.CancellationReason,
            [.. i.Lines.Select(l => new LineResponse(l.Description, l.Quantity, l.Unit, l.UnitPrice, l.DiscountPercent,
                l.VatCategory, l.VatCategory.Code(), l.VatRate, l.NetAmount))],
            [.. t.VatBreakdown.Select(b => new VatBreakdownResponse(b.Category, b.Category.Code(), b.Rate, b.TaxableAmount, b.TaxAmount))],
            t.NetTotal, t.VatTotal, t.GrossTotal, t.AmountDue);
    }
}

public sealed record InvoiceSummary(Guid Id, string? Number, InvoiceStatus Status, Guid CustomerId, string CustomerName,
    DateOnly? IssueDate, DateOnly? DueDate, string Currency, decimal GrossTotal);

public static class InvoiceEndpoints
{
    public static void MapInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/invoices").WithTags("Invoices").RequireAuthorization();
        g.MapGet("/", List).WithSummary("Invoices of the signed-in seller, newest first");
        g.MapGet("/{id:guid}", Get).WithSummary("One invoice with lines, VAT breakdown and totals");
        g.MapPost("/", Create).WithSummary("Create a draft. VAT treatment (incl. reverse charge) is chosen automatically.");
        g.MapPut("/{id:guid}", Update).WithSummary("Replace the content of a draft");
        g.MapDelete("/{id:guid}", Delete).WithSummary("Delete a draft (issued invoices are kept)");
        g.MapPost("/{id:guid}/issue", Issue).WithSummary("Issue: assigns the next gapless number and locks the invoice");
        g.MapPost("/{id:guid}/pay", Pay).WithSummary("Mark an issued invoice as paid");
        g.MapPost("/{id:guid}/cancel", Cancel).WithSummary("Cancel an issued invoice, with a reason");
        g.MapGet("/{id:guid}/pdf", Pdf).WithSummary("PDF in German business-letter layout (drafts are marked as such)")
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf");
        g.MapGet("/{id:guid}/xrechnung", XRechnungXml).WithSummary("German e-invoice: XRechnung 3.0 (UBL 2.1, EN 16931)")
            .Produces(StatusCodes.Status200OK, contentType: "application/xml");
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> Pdf(Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var doc = await InvoiceDocument.LoadAsync(db, user.UserId(), id, ct);
        return doc is null
            ? TypedResults.NotFound()
            : TypedResults.File(InvoicePdf.Render(doc), "application/pdf", $"{FileName(doc)}.pdf");
    }

    private static async Task<Results<FileContentHttpResult, NotFound, UnprocessableEntity<HttpValidationProblemDetails>>> XRechnungXml(
        Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var doc = await InvoiceDocument.LoadAsync(db, user.UserId(), id, ct);
        if (doc is null)
        {
            return TypedResults.NotFound();
        }

        if (XRechnung.MissingData(doc) is { Count: > 0 } missing)
        {
            return TypedResults.UnprocessableEntity(new HttpValidationProblemDetails
            {
                Title = "XRechnung needs more data",
                Status = StatusCodes.Status422UnprocessableEntity,
                Errors = new Dictionary<string, string[]> { ["xrechnung"] = [.. missing] },
            });
        }

        return TypedResults.File(System.Text.Encoding.UTF8.GetBytes(XRechnung.Write(doc)), "application/xml", $"{FileName(doc)}.xml");
    }

    private static string FileName(InvoiceDocument doc) =>
        doc.Invoice.Number is { } number ? $"invoice-{number}" : $"draft-{doc.Invoice.Id:N}";

    private static async Task<Ok<List<InvoiceSummary>>> List(
        ClaimsPrincipal user, AppDbContext db, InvoiceStatus? status, Guid? customerId, CancellationToken ct)
    {
        var ownerId = user.UserId();
        var query = db.Invoices.AsNoTracking().Where(i => i.OwnerId == ownerId);
        if (status is not null)
        {
            query = query.Where(i => i.Status == status);
        }

        if (customerId is not null)
        {
            query = query.Where(i => i.CustomerId == customerId);
        }

        var invoices = await query.OrderByDescending(i => i.IssueDate).ThenByDescending(i => i.Number).Take(500).ToListAsync(ct);
        var ids = invoices.Select(i => i.CustomerId).Distinct().ToList();
        var names = await db.Customers.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return TypedResults.Ok(invoices
            .Select(i => new InvoiceSummary(i.Id, i.Number, i.Status, i.CustomerId, names.GetValueOrDefault(i.CustomerId, "?"),
                i.IssueDate, i.DueDate, i.Currency, i.CalculateTotals().GrossTotal))
            .ToList());
    }

    private static async Task<Results<Ok<InvoiceResponse>, NotFound>> Get(Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var invoice = await Find(db, user, id, ct);
        return invoice is null ? TypedResults.NotFound() : TypedResults.Ok(InvoiceResponse.From(invoice));
    }

    private static async Task<Results<Created<InvoiceResponse>, NotFound<string>>> Create(
        InvoiceRequest req, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var ownerId = user.UserId();
        var invoice = new Invoice(ownerId, req.CustomerId, req.Currency);
        if (await Fill(db, ownerId, invoice, req, ct) is { } error)
        {
            return error;
        }

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/invoices/{invoice.Id}", InvoiceResponse.From(invoice));
    }

    private static async Task<Results<Ok<InvoiceResponse>, NotFound, NotFound<string>>> Update(
        Guid id, InvoiceRequest req, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var invoice = await Find(db, user, id, ct);
        if (invoice is null)
        {
            return TypedResults.NotFound();
        }

        if (invoice.CustomerId != req.CustomerId || !string.Equals(invoice.Currency, req.Currency, StringComparison.OrdinalIgnoreCase))
        {
            // Customer and currency are fixed after creation; create a new draft instead.
            throw new DomainException("Customer and currency of a draft cannot be changed.");
        }

        invoice.ClearLines();
        if (await Fill(db, user.UserId(), invoice, req, ct) is { } error)
        {
            return error;
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(InvoiceResponse.From(invoice));
    }

    private static async Task<Results<NoContent, NotFound>> Delete(Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var invoice = await Find(db, user, id, ct);
        if (invoice is null)
        {
            return TypedResults.NotFound();
        }

        if (invoice.Status != InvoiceStatus.Draft)
        {
            throw new DomainException("Issued invoices cannot be deleted; cancel them instead.");
        }

        db.Invoices.Remove(invoice);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<InvoiceResponse>, NotFound>> Issue(
        Guid id, IssueRequest req, ClaimsPrincipal user, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var invoice = await Find(db, user, id, ct);
        if (invoice is null)
        {
            return TypedResults.NotFound();
        }

        var seller = await db.Users.SingleAsync(u => u.Id == invoice.OwnerId, ct);
        var issueDate = req.IssueDate ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (invoice.Status != InvoiceStatus.Draft)
        {
            throw new DomainException($"Invoice {invoice.Number} is already {invoice.Status.ToString().ToLowerInvariant()}.");
        }

        var number = await InvoiceNumbering.NextAsync(db, invoice.OwnerId, seller.InvoicePrefix, issueDate.Year, ct);
        invoice.Issue(number, issueDate, req.PaymentTermDays);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return TypedResults.Ok(InvoiceResponse.From(invoice));
    }

    private static async Task<Results<Ok<InvoiceResponse>, NotFound>> Pay(
        Guid id, PayRequest req, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var invoice = await Find(db, user, id, ct);
        if (invoice is null)
        {
            return TypedResults.NotFound();
        }

        invoice.MarkPaid(req.PaidOn);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(InvoiceResponse.From(invoice));
    }

    private static async Task<Results<Ok<InvoiceResponse>, NotFound>> Cancel(
        Guid id, CancelRequest req, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var invoice = await Find(db, user, id, ct);
        if (invoice is null)
        {
            return TypedResults.NotFound();
        }

        invoice.Cancel(req.Reason);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(InvoiceResponse.From(invoice));
    }

    private static Task<Invoice?> Find(AppDbContext db, ClaimsPrincipal user, Guid id, CancellationToken ct) =>
        db.Invoices.SingleOrDefaultAsync(i => i.Id == id && i.OwnerId == user.UserId(), ct);

    /// <summary>Adds the lines with the VAT treatment that follows from seller, customer and type of supply.</summary>
    private static async Task<NotFound<string>?> Fill(AppDbContext db, Guid ownerId, Invoice invoice, InvoiceRequest req, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == req.CustomerId && c.OwnerId == ownerId, ct);
        if (customer is null)
        {
            return TypedResults.NotFound("Customer not found.");
        }

        var seller = await db.Users.AsNoTracking().SingleAsync(u => u.Id == ownerId, ct);
        var buyerVatId = customer.VatId is null ? null : VatId.Parse(customer.VatId);
        var tax = TaxRules.Determine(seller.Country, customer.Country, buyerVatId, req.SupplyType);

        foreach (var l in req.Lines)
        {
            var rate = tax.Category.HasRate() ? l.VatRate ?? tax.Rate : 0m;
            invoice.AddLine(new InvoiceLine(l.Description, l.Quantity, l.UnitPrice, tax.Category, rate, l.DiscountPercent, l.Unit.ToUpperInvariant()));
        }

        invoice.SetNotes(req.Note, tax.InvoiceNote);
        invoice.SetBuyerReference(req.BuyerReference);
        return null;
    }
}
