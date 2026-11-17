using InvoiceHub.Domain;

namespace InvoiceHub.Tests.Domain;

public class InvoiceLifecycleTests
{
    private static readonly DateOnly Nov2 = new(2026, 11, 2);

    private static Invoice DraftWithLine()
    {
        var invoice = new Invoice(Guid.NewGuid(), Guid.NewGuid());
        invoice.AddLine(new InvoiceLine("Website", 1, 1200m, VatCategory.Standard, 19m));
        return invoice;
    }

    [Fact]
    public void Issuing_assigns_number_and_due_date()
    {
        var invoice = DraftWithLine();
        invoice.Issue("2026-0007", Nov2, paymentTermDays: 30);
        Assert.Equal((InvoiceStatus.Issued, "2026-0007", new DateOnly(2026, 12, 2)), (invoice.Status, invoice.Number, invoice.DueDate));
    }

    [Fact]
    public void Issued_invoices_are_immutable()
    {
        var invoice = DraftWithLine();
        invoice.Issue("2026-0001", Nov2);
        var ex = Assert.Throws<DomainException>(() => invoice.AddLine(new InvoiceLine("Extra", 1, 1m, VatCategory.Standard, 19m)));
        Assert.Contains("can no longer be changed", ex.Message, StringComparison.Ordinal);
        Assert.Throws<DomainException>(invoice.ClearLines);
        Assert.Throws<DomainException>(() => invoice.Issue("2026-0002", Nov2));
    }

    [Fact]
    public void Empty_invoice_cannot_be_issued() =>
        Assert.Throws<DomainException>(() => new Invoice(Guid.NewGuid(), Guid.NewGuid()).Issue("2026-0001", Nov2));

    [Fact]
    public void Invoice_without_vat_needs_a_tax_note()
    {
        var invoice = new Invoice(Guid.NewGuid(), Guid.NewGuid());
        invoice.AddLine(new InvoiceLine("Consulting", 10, 100m, VatCategory.ReverseCharge, 0m));
        Assert.Throws<DomainException>(() => invoice.Issue("2026-0001", Nov2));

        invoice.SetNotes(null, "Reverse charge");
        invoice.Issue("2026-0001", Nov2);
        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
    }

    [Fact]
    public void Paid_only_after_issue_and_not_before_issue_date()
    {
        var invoice = DraftWithLine();
        Assert.Throws<DomainException>(() => invoice.MarkPaid(Nov2));
        invoice.Issue("2026-0001", Nov2);
        Assert.Throws<DomainException>(() => invoice.MarkPaid(Nov2.AddDays(-1)));
        invoice.MarkPaid(Nov2.AddDays(3));
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Throws<DomainException>(() => invoice.Cancel("Too late"));
    }

    [Fact]
    public void Cancel_needs_an_issued_invoice_and_a_reason()
    {
        var invoice = DraftWithLine();
        Assert.Throws<DomainException>(() => invoice.Cancel("Wrong customer"));
        invoice.Issue("2026-0001", Nov2);
        Assert.Throws<DomainException>(() => invoice.Cancel("  "));
        invoice.Cancel("Wrong customer");
        Assert.Equal((InvoiceStatus.Cancelled, "Wrong customer"), (invoice.Status, invoice.CancellationReason));
    }

    [Fact]
    public void Currency_must_be_an_iso_code() =>
        Assert.Throws<DomainException>(() => new Invoice(Guid.NewGuid(), Guid.NewGuid(), "EURO"));
}
