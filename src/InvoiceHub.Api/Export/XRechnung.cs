using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using InvoiceHub.Domain;

namespace InvoiceHub.Api.Export;

/// <summary>
/// Writes an invoice as XRechnung 3.0 in UBL 2.1 syntax: the German CIUS of the European standard EN 16931,
/// accepted by German public authorities and required for B2B invoices in Germany from 2027/2028.
/// </summary>
/// <remarks>Element order follows the UBL schema; business rules are referenced by their EN 16931 / XRechnung IDs.</remarks>
public static class XRechnung
{
    public const string CustomizationId = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0";
    public const string ProfileId = "urn:fdc:peppol.eu:2017:poacc:billing:01:1.0";

    private static readonly XNamespace Ubl = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    /// <summary>Data XRechnung requires that an invoice may lack. Empty means the invoice can be exported.</summary>
    public static IReadOnlyList<string> MissingData(InvoiceDocument doc)
    {
        var missing = new List<string>();
        var i = doc.Invoice;
        if (i.Status == InvoiceStatus.Draft)
        {
            missing.Add("The invoice must be issued first.");
        }

        if (string.IsNullOrWhiteSpace(doc.Seller.ContactName) || string.IsNullOrWhiteSpace(doc.Seller.Phone))
        {
            missing.Add("Seller contact name and phone (BR-DE-2): set them via PUT /api/me.");
        }

        if (string.IsNullOrWhiteSpace(doc.Iban))
        {
            missing.Add("Seller bank account IBAN (BR-DE-1, payment instructions): set it via PUT /api/me.");
        }

        var onlyOutOfScope = i.Lines.All(l => l.VatCategory == VatCategory.NotSubject);
        if (!onlyOutOfScope && string.IsNullOrWhiteSpace(doc.Seller.VatId))
        {
            missing.Add("Seller VAT ID (BR-CO-26): set it via PUT /api/me.");
        }

        if (string.IsNullOrWhiteSpace(doc.Buyer.Email))
        {
            missing.Add("Customer email, the buyer's electronic address (BT-49): add it to the customer.");
        }

        return missing;
    }

    public static string Write(InvoiceDocument doc)
    {
        if (MissingData(doc) is { Count: > 0 } missing)
        {
            throw new DomainException("XRechnung export not possible: " + string.Join(" ", missing));
        }

        var i = doc.Invoice;
        var t = doc.Totals;
        var cur = i.Currency;
        var outOfScope = i.Lines.Any(l => l.VatCategory == VatCategory.NotSubject);

        var root = new XElement(Ubl + "Invoice",
            new XAttribute(XNamespace.Xmlns + "cac", Cac),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc),
            Cbc_("CustomizationID", CustomizationId),
            Cbc_("ProfileID", ProfileId),
            Cbc_("ID", i.Number),                                              // BT-1
            Cbc_("IssueDate", Date(i.IssueDate!.Value)),                       // BT-2
            Cbc_("DueDate", Date(i.DueDate!.Value)),                           // BT-9
            Cbc_("InvoiceTypeCode", "380"),                                    // BT-3: commercial invoice
            i.Note is null ? null : Cbc_("Note", i.Note),                      // BT-22
            i.TaxNote is null ? null : Cbc_("Note", i.TaxNote),
            Cbc_("DocumentCurrencyCode", cur),                                 // BT-5
            Cbc_("BuyerReference", i.BuyerReference ?? doc.Buyer.Name),        // BT-10, mandatory in XRechnung (BR-DE-15)
            new XElement(Cac + "AccountingSupplierParty", PartyElement(doc.Seller, includeVatId: !outOfScope, contact: true)),
            new XElement(Cac + "AccountingCustomerParty", PartyElement(doc.Buyer, includeVatId: doc.Buyer.VatId is not null && !outOfScope, contact: false)),
            new XElement(Cac + "Delivery",                                     // date of supply = invoice date (§ 14 UStG)
                Cbc_("ActualDeliveryDate", Date(i.IssueDate!.Value)),          // BT-72, required for intra-community supplies (BR-IC-11)
                new XElement(Cac + "DeliveryLocation", new XElement(Cac + "Address", // BG-15: the customer's address
                    Cbc_("StreetName", doc.Buyer.Street),
                    Cbc_("CityName", doc.Buyer.City),                          // BR-DE-10
                    Cbc_("PostalZone", doc.Buyer.PostalCode),                  // BR-DE-11
                    new XElement(Cac + "Country", Cbc_("IdentificationCode", doc.Buyer.Country))))), // BT-80 (BR-IC-12)
            new XElement(Cac + "PaymentMeans",
                Cbc_("PaymentMeansCode", "58"),                                // SEPA credit transfer
                Cbc_("PaymentID", i.Number),                                   // BT-83: reference for the transfer
                new XElement(Cac + "PayeeFinancialAccount", Cbc_("ID", doc.Iban))),
            new XElement(Cac + "PaymentTerms",
                Cbc_("Note", $"Payable by {Date(i.DueDate!.Value)} without deduction. / Zahlbar bis {i.DueDate:dd.MM.yyyy} ohne Abzug.")),
            new XElement(Cac + "TaxTotal",
                Amount("TaxAmount", t.VatTotal, cur),                         // BT-110
                t.VatBreakdown.Select(b => new XElement(Cac + "TaxSubtotal",
                    Amount("TaxableAmount", b.TaxableAmount, cur),            // BT-116
                    Amount("TaxAmount", b.TaxAmount, cur),                    // BT-117
                    TaxCategory("TaxCategory", b.Category, b.Rate, exemptionReason: b.Category.HasRate() ? null : i.TaxNote, breakdown: true)))),
            new XElement(Cac + "LegalMonetaryTotal",
                Amount("LineExtensionAmount", t.NetTotal, cur),               // BT-106
                Amount("TaxExclusiveAmount", t.NetTotal, cur),                // BT-109
                Amount("TaxInclusiveAmount", t.GrossTotal, cur),              // BT-112
                Amount("PayableAmount", t.GrossTotal, cur)),                  // BT-115
            i.Lines.Select((l, n) => LineElement(l, n + 1, cur)));

        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static XElement PartyElement(Party p, bool includeVatId, bool contact) =>
        new(Cac + "Party",
            new XElement(Cbc + "EndpointID", new XAttribute("schemeID", "EM"), p.Email),   // BT-34 / BT-49: e-mail address
            // BT-29: when the VAT ID may not appear (category "O"), the seller still needs an identifier (BR-CO-26).
            contact && !includeVatId && p.VatId is not null ? new XElement(Cac + "PartyIdentification", Cbc_("ID", p.VatId)) : null,
            new XElement(Cac + "PostalAddress",
                Cbc_("StreetName", p.Street),
                Cbc_("CityName", p.City),
                Cbc_("PostalZone", p.PostalCode),
                new XElement(Cac + "Country", Cbc_("IdentificationCode", p.Country))),
            includeVatId && p.VatId is not null
                ? new XElement(Cac + "PartyTaxScheme", Cbc_("CompanyID", p.VatId), new XElement(Cac + "TaxScheme", Cbc_("ID", "VAT")))
                : null,
            new XElement(Cac + "PartyLegalEntity", Cbc_("RegistrationName", p.Name)),
            contact
                ? new XElement(Cac + "Contact", Cbc_("Name", p.ContactName), Cbc_("Telephone", p.Phone), Cbc_("ElectronicMail", p.Email))
                : null);

    private static XElement LineElement(InvoiceLine l, int position, string cur)
    {
        var gross = Amounts.Round2(l.Quantity * l.UnitPrice);
        var discount = gross - l.NetAmount;
        return new XElement(Cac + "InvoiceLine",
            Cbc_("ID", position.ToString(CultureInfo.InvariantCulture)),
            new XElement(Cbc + "InvoicedQuantity", new XAttribute("unitCode", l.Unit), Number(l.Quantity)),
            Amount("LineExtensionAmount", l.NetAmount, cur),                  // BT-131
            discount > 0
                ? new XElement(Cac + "AllowanceCharge",                       // BG-27: line discount
                    Cbc_("ChargeIndicator", "false"),
                    Cbc_("AllowanceChargeReason", "Discount"),
                    Cbc_("MultiplierFactorNumeric", Number(l.DiscountPercent)),
                    Amount("Amount", discount, cur),
                    Amount("BaseAmount", gross, cur))
                : null,
            new XElement(Cac + "Item",
                Cbc_("Name", l.Description),
                TaxCategory("ClassifiedTaxCategory", l.VatCategory, l.VatRate, exemptionReason: null)),
            new XElement(Cac + "Price", Amount("PriceAmount", l.UnitPrice, cur, decimals: 4)));
    }

    private static XElement TaxCategory(string name, VatCategory category, decimal rate, string? exemptionReason, bool breakdown = false) =>
        new(Cac + name,
            Cbc_("ID", category.Code()),
            // Lines of category "O" carry no rate (BR-O-05); the breakdown always needs one in XRechnung (BR-DE-14).
            category == VatCategory.NotSubject && !breakdown ? null : Cbc_("Percent", Number(rate)),
            exemptionReason is null ? null : Cbc_("TaxExemptionReason", exemptionReason),
            new XElement(Cac + "TaxScheme", Cbc_("ID", "VAT")));

    private static XElement Cbc_(string name, string? value) => new(Cbc + name, value);

    private static XElement Amount(string name, decimal value, string currency, int decimals = 2) =>
        new(Cbc + name, new XAttribute("currencyID", currency),
            value.ToString(decimals == 2 ? "0.00" : "0.00##", CultureInfo.InvariantCulture));

    private static string Number(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
