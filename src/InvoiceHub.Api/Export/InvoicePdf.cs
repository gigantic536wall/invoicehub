using System.Globalization;
using InvoiceHub.Domain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace InvoiceHub.Api.Export;

/// <summary>A4 invoice in German business-letter layout (DIN 5008 address window), bilingual German / English.</summary>
public static class InvoicePdf
{
    /// <summary>German number format (1.234,56) built explicitly: the app runs with invariant globalization (no ICU in Docker).</summary>
    private static readonly CultureInfo De = CreateGermanFormat();
    private const string Accent = "#0F766E";

    static InvoicePdf()
    {
        // Free for individuals and companies with less than USD 1M annual revenue.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] Render(InvoiceDocument doc) => Build(doc).GeneratePdf();

    /// <summary>The document model, so it can also be rendered as images (used for previews in tests).</summary>
    public static IDocument Build(InvoiceDocument doc)
    {
        var i = doc.Invoice;
        var t = doc.Totals;
        string Money(decimal v) => v.ToString("N2", De) + " " + (i.Currency == "EUR" ? "€" : i.Currency);

        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.MarginHorizontal(20, Unit.Millimetre);
            page.MarginVertical(15, Unit.Millimetre);
            page.DefaultTextStyle(s => s.FontSize(9.5f).FontColor("#1F2937"));

            page.Header().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(doc.Seller.Name).FontSize(16).Bold().FontColor(Accent);
                    c.Item().Text($"{doc.Seller.Street} · {doc.Seller.PostalCode} {doc.Seller.City}").FontColor("#6B7280");
                });
                row.ConstantItem(70, Unit.Millimetre).AlignRight().Column(c =>
                {
                    c.Item().AlignRight().Text(i.Status == InvoiceStatus.Draft ? "ENTWURF / DRAFT" : "RECHNUNG / INVOICE")
                        .FontSize(14).Bold().FontColor(i.Status == InvoiceStatus.Draft ? Colors.Orange.Darken2 : "#111827");
                    if (i.Status == InvoiceStatus.Cancelled)
                    {
                        c.Item().AlignRight().Text("STORNIERT / CANCELLED").Bold().FontColor(Colors.Red.Darken2);
                    }
                });
            });

            page.Content().PaddingTop(12, Unit.Millimetre).Column(col =>
            {
                col.Spacing(6);
                col.Item().Row(row =>
                {
                    // Address window (DIN 5008): sender line above the recipient.
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text($"{doc.Seller.Name}, {doc.Seller.Street}, {doc.Seller.PostalCode} {doc.Seller.City}").FontSize(7).Underline().FontColor("#6B7280");
                        c.Item().PaddingTop(4).Text(doc.Buyer.Name).Bold();
                        c.Item().Text(doc.Buyer.Street);
                        c.Item().Text($"{doc.Buyer.PostalCode} {doc.Buyer.City}");
                        c.Item().Text(doc.Buyer.Country);
                        if (doc.Buyer.VatId is not null)
                        {
                            c.Item().PaddingTop(2).Text($"USt-IdNr. / VAT ID: {doc.Buyer.VatId}").FontSize(8.5f);
                        }
                    });
                    row.ConstantItem(70, Unit.Millimetre).Table(table =>
                    {
                        table.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.RelativeColumn(); });
                        void Meta(string label, string? value)
                        {
                            table.Cell().PaddingVertical(1).Text(label).FontColor("#6B7280");
                            table.Cell().PaddingVertical(1).AlignRight().Text(value ?? "–").SemiBold();
                        }

                        Meta("Rechnungsnr. / No.", i.Number ?? "–");
                        Meta("Datum / Date", i.IssueDate?.ToString("dd.MM.yyyy", De));
                        Meta("Fällig / Due", i.DueDate?.ToString("dd.MM.yyyy", De));
                        if (i.BuyerReference is not null)
                        {
                            Meta("Ihre Ref. / Your ref.", i.BuyerReference);
                        }

                        if (doc.Seller.VatId is not null)
                        {
                            Meta("USt-IdNr.", doc.Seller.VatId);
                        }
                    });
                });

                col.Item().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(cd =>
                    {
                        cd.ConstantColumn(8, Unit.Millimetre);
                        cd.RelativeColumn(5);
                        cd.RelativeColumn(1.6f);
                        cd.RelativeColumn(1.8f);
                        cd.RelativeColumn(1.1f);
                        cd.RelativeColumn(2);
                    });

                    table.Header(h =>
                    {
                        foreach (var (text, right) in new[]
                        {
                            ("#", false), ("Beschreibung / Description", false), ("Menge / Qty", true),
                            ("Preis / Price", true), ("USt / VAT", true), ("Betrag / Amount", true),
                        })
                        {
                            var cell = h.Cell().BorderBottom(1).BorderColor(Accent).PaddingVertical(4);
                            (right ? cell.AlignRight() : cell).Text(text).Bold().FontSize(8.5f);
                        }
                    });

                    var n = 1;
                    foreach (var l in i.Lines)
                    {
                        IContainer Cell() => table.Cell().BorderBottom(0.5f).BorderColor("#E5E7EB").PaddingVertical(4);
                        Cell().Text((n++).ToString(CultureInfo.InvariantCulture));
                        Cell().Column(c =>
                        {
                            c.Item().Text(l.Description);
                            if (l.DiscountPercent > 0)
                            {
                                c.Item().Text($"Rabatt / discount {l.DiscountPercent.ToString("0.##", De)} %").FontSize(8).FontColor("#6B7280");
                            }
                        });
                        Cell().AlignRight().Text($"{l.Quantity.ToString("0.####", De)} {UnitLabel(l.Unit)}");
                        Cell().AlignRight().Text(Money(l.UnitPrice));
                        Cell().AlignRight().Text(l.VatCategory.HasRate() ? $"{l.VatRate.ToString("0.##", De)} %" : l.VatCategory.Code());
                        Cell().AlignRight().Text(Money(l.NetAmount));
                    }
                });

                col.Item().AlignRight().Width(85, Unit.Millimetre).Table(table =>
                {
                    table.ColumnsDefinition(cd => { cd.RelativeColumn(3); cd.RelativeColumn(2); });
                    void Sum(string label, string value, bool bold = false)
                    {
                        var l = table.Cell().PaddingVertical(2).Text(label);
                        var v = table.Cell().PaddingVertical(2).AlignRight().Text(value);
                        if (bold)
                        {
                            l.Bold();
                            v.Bold();
                        }
                    }

                    Sum("Nettobetrag / Net total", Money(t.NetTotal));
                    foreach (var b in t.VatBreakdown)
                    {
                        Sum(b.Category.HasRate()
                            ? $"USt / VAT {b.Rate.ToString("0.##", De)} % auf {Money(b.TaxableAmount)}"
                            : $"USt / VAT ({b.Category.Code()}) auf {Money(b.TaxableAmount)}", Money(b.TaxAmount));
                    }

                    table.Cell().ColumnSpan(2).PaddingTop(2).BorderTop(1).BorderColor(Accent);
                    Sum("Gesamtbetrag / Total", Money(t.GrossTotal), bold: true);
                });

                if (i.TaxNote is not null)
                {
                    col.Item().PaddingTop(6).Background("#F0FDFA").Padding(6).Text(i.TaxNote).FontSize(8.5f);
                }

                if (i.Note is not null)
                {
                    col.Item().PaddingTop(4).Text(i.Note);
                }

                if (i.DueDate is not null)
                {
                    col.Item().PaddingTop(6).Text(text =>
                    {
                        text.Span("Bitte überweisen Sie den Betrag bis zum ");
                        text.Span(i.DueDate.Value.ToString("dd.MM.yyyy", De)).SemiBold();
                        text.Span($" unter Angabe der Rechnungsnummer {i.Number}. / Please pay by the due date, quoting the invoice number.");
                    });
                }

                if (i.Status == InvoiceStatus.Cancelled && i.CancellationReason is not null)
                {
                    col.Item().PaddingTop(4).Text($"Storniert / Cancelled: {i.CancellationReason}").FontColor(Colors.Red.Darken2);
                }
            });

            page.Footer().BorderTop(0.5f).BorderColor("#D1D5DB").PaddingTop(4).Row(row =>
            {
                row.RelativeItem().Text($"{doc.Seller.Name} · {doc.Seller.Street} · {doc.Seller.PostalCode} {doc.Seller.City}").FontSize(7.5f).FontColor("#6B7280");
                row.RelativeItem().AlignCenter().Text(string.Join(" · ", new[] { doc.Seller.Phone, doc.Seller.Email }.Where(x => x is not null))).FontSize(7.5f).FontColor("#6B7280");
                row.RelativeItem().AlignRight().Text(doc.Iban is null ? "" : $"IBAN {FormatIban(doc.Iban)}").FontSize(7.5f).FontColor("#6B7280");
            });
        }));
    }

    private static string UnitLabel(string code) => code switch
    {
        "HUR" => "Std.",
        "DAY" => "Tag(e)",
        "C62" or "H87" => "Stk.",
        "MON" => "Monat(e)",
        "KGM" => "kg",
        _ => code,
    };

    private static CultureInfo CreateGermanFormat()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = ".";
        return CultureInfo.ReadOnly(culture);
    }

    private static string FormatIban(string iban) =>
        string.Join(' ', Enumerable.Range(0, (iban.Length + 3) / 4).Select(k => iban.Substring(k * 4, Math.Min(4, iban.Length - k * 4))));
}
