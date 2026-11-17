namespace InvoiceHub.Domain;

public enum InvoiceStatus
{
    Draft,
    Issued,
    Paid,
    Cancelled,
}

public sealed class InvoiceLine
{
    private InvoiceLine()
    {
        Description = string.Empty;
        Unit = "C62";
    }

    public InvoiceLine(string description, decimal quantity, decimal unitPrice, VatCategory category, decimal vatRate,
        decimal discountPercent = 0m, string unit = "C62")
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new DomainException("Line description is required.");
        }

        if (quantity <= 0)
        {
            throw new DomainException("Quantity must be greater than zero.");
        }

        if (unitPrice < 0)
        {
            throw new DomainException("Unit price must not be negative (use a credit note instead).");
        }

        if (discountPercent is < 0 or > 100)
        {
            throw new DomainException("Discount must be between 0 and 100 %.");
        }

        if (category.HasRate() ? vatRate is <= 0 or > 30 : vatRate != 0)
        {
            throw new DomainException(category.HasRate()
                ? "A standard-rated line needs a VAT rate between 0 and 30 %."
                : $"VAT category {category.Code()} must have a rate of 0 %.");
        }

        Description = description.Trim();
        Quantity = quantity;
        UnitPrice = unitPrice;
        DiscountPercent = discountPercent;
        VatCategory = category;
        VatRate = vatRate;
        Unit = unit;
    }

    public int Id { get; private set; }
    public string Description { get; private set; }
    public decimal Quantity { get; private set; }
    /// <summary>Unit of measure code (UN/ECE Rec 20): C62 = piece, HUR = hour, DAY = day.</summary>
    public string Unit { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal DiscountPercent { get; private set; }
    public VatCategory VatCategory { get; private set; }
    public decimal VatRate { get; private set; }

    /// <summary>Net amount of the line, rounded to cents (EN 16931 BT-131).</summary>
    public decimal NetAmount => Amounts.Round2(Quantity * UnitPrice * (1 - DiscountPercent / 100m));
}

public sealed record VatBreakdown(VatCategory Category, decimal Rate, decimal TaxableAmount, decimal TaxAmount);

public sealed record InvoiceTotals(
    decimal NetTotal,
    IReadOnlyList<VatBreakdown> VatBreakdown,
    decimal VatTotal,
    decimal GrossTotal,
    decimal PaidAmount,
    decimal AmountDue);

/// <summary>
/// An invoice. Drafts can be edited freely; once issued it gets a gapless number and becomes immutable,
/// as German and EU bookkeeping rules require (GoBD). Corrections then need a cancellation.
/// </summary>
public sealed class Invoice
{
    private readonly List<InvoiceLine> _lines = [];

    private Invoice()
    {
        Currency = "EUR";
    }

    public Invoice(Guid ownerId, Guid customerId, string currency = "EUR")
    {
        if (currency.Length != 3)
        {
            throw new DomainException("Currency must be an ISO 4217 code, e.g. EUR.");
        }

        Id = Guid.NewGuid();
        OwnerId = ownerId;
        CustomerId = customerId;
        Currency = currency.ToUpperInvariant();
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid CustomerId { get; private set; }
    public InvoiceStatus Status { get; private set; } = InvoiceStatus.Draft;
    public string? Number { get; private set; }
    public string Currency { get; private set; }
    public DateOnly? IssueDate { get; private set; }
    public DateOnly? DueDate { get; private set; }
    public DateOnly? PaidOn { get; private set; }
    public string? Note { get; private set; }
    public string? TaxNote { get; private set; }
    public string? CancellationReason { get; private set; }
    public IReadOnlyList<InvoiceLine> Lines => _lines;

    public void AddLine(InvoiceLine line)
    {
        EnsureDraft();
        _lines.Add(line);
    }

    public void ClearLines()
    {
        EnsureDraft();
        _lines.Clear();
    }

    public void SetNotes(string? note, string? taxNote)
    {
        EnsureDraft();
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        TaxNote = string.IsNullOrWhiteSpace(taxNote) ? null : taxNote.Trim();
    }

    /// <summary>Finalizes the invoice with its number. Afterwards nothing on it can change.</summary>
    public void Issue(string number, DateOnly issueDate, int paymentTermDays = 14)
    {
        EnsureDraft();
        if (_lines.Count == 0)
        {
            throw new DomainException("An invoice needs at least one line.");
        }

        if (paymentTermDays is < 0 or > 180)
        {
            throw new DomainException("Payment term must be between 0 and 180 days.");
        }

        if (_lines.Any(l => !l.VatCategory.HasRate()) && TaxNote is null)
        {
            throw new DomainException("Invoices without VAT need a note explaining why (e.g. reverse charge).");
        }

        Number = number;
        IssueDate = issueDate;
        DueDate = issueDate.AddDays(paymentTermDays);
        Status = InvoiceStatus.Issued;
    }

    public void MarkPaid(DateOnly paidOn)
    {
        if (Status != InvoiceStatus.Issued)
        {
            throw new DomainException($"Only issued invoices can be marked as paid (this one is {Status}).");
        }

        if (paidOn < IssueDate)
        {
            throw new DomainException("Payment date cannot be before the issue date.");
        }

        PaidOn = paidOn;
        Status = InvoiceStatus.Paid;
    }

    public void Cancel(string reason)
    {
        if (Status != InvoiceStatus.Issued)
        {
            throw new DomainException(Status == InvoiceStatus.Draft
                ? "Drafts are deleted, not cancelled."
                : $"A {Status} invoice cannot be cancelled.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("A cancellation needs a reason.");
        }

        CancellationReason = reason.Trim();
        Status = InvoiceStatus.Cancelled;
    }

    /// <summary>Totals according to EN 16931: VAT is calculated per category and rate on the summed line amounts.</summary>
    public InvoiceTotals CalculateTotals()
    {
        var net = _lines.Sum(l => l.NetAmount);
        var breakdown = _lines
            .GroupBy(l => (l.VatCategory, l.VatRate))
            .OrderBy(g => g.Key.VatCategory).ThenByDescending(g => g.Key.VatRate)
            .Select(g =>
            {
                var taxable = g.Sum(l => l.NetAmount);
                return new VatBreakdown(g.Key.VatCategory, g.Key.VatRate, taxable, Amounts.Round2(taxable * g.Key.VatRate / 100m));
            })
            .ToList();
        var vat = breakdown.Sum(b => b.TaxAmount);
        var gross = net + vat;
        var paid = Status == InvoiceStatus.Paid ? gross : 0m;
        return new InvoiceTotals(net, breakdown, vat, gross, paid, gross - paid);
    }

    private void EnsureDraft()
    {
        if (Status != InvoiceStatus.Draft)
        {
            throw new DomainException($"Invoice {Number} is {Status.ToString().ToLowerInvariant()} and can no longer be changed.");
        }
    }
}
