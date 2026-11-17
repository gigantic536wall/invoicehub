using InvoiceHub.Domain;

namespace InvoiceHub.Tests.Domain;

public class InvoiceTotalsTests
{
    private static Invoice NewInvoice() => new(Guid.NewGuid(), Guid.NewGuid());

    [Theory]
    [InlineData(0.005, 0.01)]
    [InlineData(0.004, 0.00)]
    [InlineData(-0.005, -0.01)]
    [InlineData(2.675, 2.68)]
    public void Rounds_commercially_half_away_from_zero(decimal value, decimal expected) =>
        Assert.Equal(expected, Amounts.Round2(value));

    [Fact]
    public void Line_net_amount_applies_quantity_and_discount()
    {
        var line = new InvoiceLine("Consulting", 7.5m, 95m, VatCategory.Standard, 19m, discountPercent: 10m, unit: "HUR");
        Assert.Equal(641.25m, line.NetAmount);
    }

    [Fact]
    public void Mixed_rates_are_broken_down_per_rate()
    {
        var invoice = NewInvoice();
        invoice.AddLine(new InvoiceLine("Coffee machine", 1, 349m, VatCategory.Standard, 19m));
        invoice.AddLine(new InvoiceLine("Coffee beans 1 kg", 2, 16.25m, VatCategory.Standard, 7m));
        invoice.AddLine(new InvoiceLine("Descaler", 1, 9.99m, VatCategory.Standard, 19m));

        var totals = invoice.CalculateTotals();

        Assert.Equal(391.49m, totals.NetTotal);
        Assert.Equal(
            [
                new VatBreakdown(VatCategory.Standard, 19m, 358.99m, 68.21m),
                new VatBreakdown(VatCategory.Standard, 7m, 32.50m, 2.28m),
            ],
            totals.VatBreakdown);
        Assert.Equal(70.49m, totals.VatTotal);
        Assert.Equal(461.98m, totals.GrossTotal);
        Assert.Equal(461.98m, totals.AmountDue);
    }

    [Fact]
    public void Vat_is_rounded_per_rate_not_per_line_as_EN16931_requires()
    {
        // Rounding each line would give 3 × 0.01 = 0.03; the standard requires 0.09 × 19 % = 0.0171 → 0.02.
        var invoice = NewInvoice();
        for (var i = 0; i < 3; i++)
        {
            invoice.AddLine(new InvoiceLine("Sticker", 1, 0.03m, VatCategory.Standard, 19m));
        }

        Assert.Equal(0.02m, invoice.CalculateTotals().VatTotal);
    }

    [Fact]
    public void Reverse_charge_lines_have_no_vat()
    {
        var invoice = NewInvoice();
        invoice.AddLine(new InvoiceLine("Web development", 40, 80m, VatCategory.ReverseCharge, 0m, unit: "HUR"));
        var totals = invoice.CalculateTotals();
        Assert.Equal((3200m, 0m, 3200m), (totals.NetTotal, totals.VatTotal, totals.GrossTotal));
    }

    [Fact]
    public void Paid_invoice_has_nothing_due()
    {
        var invoice = NewInvoice();
        invoice.AddLine(new InvoiceLine("Logo design", 1, 500m, VatCategory.Standard, 19m));
        invoice.Issue("2026-0001", new DateOnly(2026, 11, 2));
        invoice.MarkPaid(new DateOnly(2026, 11, 10));

        var totals = invoice.CalculateTotals();
        Assert.Equal((595m, 595m, 0m), (totals.GrossTotal, totals.PaidAmount, totals.AmountDue));
    }

    [Theory]
    [InlineData("", 1, 10, 19, "description")]
    [InlineData("X", 0, 10, 19, "Quantity")]
    [InlineData("X", 1, -1, 19, "negative")]
    [InlineData("X", 1, 10, 0, "VAT rate")]
    [InlineData("X", 1, 10, 45, "VAT rate")]
    public void Invalid_standard_lines_are_rejected(string description, decimal qty, decimal price, decimal rate, string message)
    {
        var ex = Assert.Throws<DomainException>(() => new InvoiceLine(description, qty, price, VatCategory.Standard, rate));
        Assert.Contains(message, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Zero_rated_categories_must_have_rate_zero() =>
        Assert.Throws<DomainException>(() => new InvoiceLine("X", 1, 10, VatCategory.ReverseCharge, 19m));
}
