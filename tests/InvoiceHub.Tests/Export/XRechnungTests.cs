using System.Xml.Linq;
using System.Xml.XPath;
using InvoiceHub.Api.Export;
using InvoiceHub.Domain;

namespace InvoiceHub.Tests.Export;

public class XRechnungTests
{
    private static readonly System.Xml.XmlNamespaceManager Ns = CreateNamespaces();

    internal static readonly Party Seller = new(
        "Kaffeewerk GmbH", "Musterstraße 1", "10115", "Berlin", "DE", "DE123456788", "rechnung@kaffeewerk.example",
        "Erika Muster", "+49 30 1234567");

    internal static readonly Party GermanBusiness = new(
        "Bäckerei Schulz GmbH", "Ring 3", "50667", "Köln", "DE", "DE136695976", "einkauf@schulz.example");

    internal static readonly Party FrenchBusiness = new(
        "Café Lumière SARL", "12 rue de Rivoli", "75004", "Paris", "FR", "FR40303265045", "compta@lumiere.example");

    internal static readonly Party UsBusiness = new(
        "Blue Bottle Inc.", "1 Market St", "94105", "San Francisco", "US", null, "ap@bluebottle.example");

    /// <summary>Sample invoices covering each VAT situation; CI validates them with the official KoSIT validator.</summary>
    public static TheoryData<string> Samples => new() { "domestic", "reverse-charge", "intra-community", "export", "services-outside-eu" };

    internal static InvoiceDocument Sample(string name)
    {
        var (buyer, supply) = name switch
        {
            "domestic" => (GermanBusiness, SupplyType.Goods),
            "reverse-charge" => (FrenchBusiness, SupplyType.Services),
            "intra-community" => (FrenchBusiness, SupplyType.Goods),
            "export" => (UsBusiness, SupplyType.Goods),
            "services-outside-eu" => (UsBusiness, SupplyType.Services),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        var tax = TaxRules.Determine(Seller.Country, buyer.Country, buyer.VatId is null ? null : VatId.Parse(buyer.VatId), supply);
        var invoice = new Invoice(Guid.NewGuid(), Guid.NewGuid());
        if (name == "domestic")
        {
            invoice.AddLine(new InvoiceLine("Espressomaschine Classic", 2, 349m, VatCategory.Standard, 19m, discountPercent: 10m));
            invoice.AddLine(new InvoiceLine("Kaffeebohnen Ethiopia 1 kg", 12, 16.25m, VatCategory.Standard, 7m));
        }
        else
        {
            invoice.AddLine(new InvoiceLine(supply == SupplyType.Goods ? "Kaffeemühle Pro" : "Barista training", 3, 189m, tax.Category, tax.Rate,
                unit: supply == SupplyType.Goods ? "C62" : "DAY"));
        }

        invoice.SetNotes("Thank you for your order!", tax.InvoiceNote);
        invoice.SetBuyerReference("PO-4711");
        invoice.Issue("RE-2026-0042", new DateOnly(2026, 11, 18), paymentTermDays: 14);
        return new InvoiceDocument(invoice, Seller, buyer, "DE89370400440532013000");
    }

    private static XDocument Xml(InvoiceDocument doc) => XDocument.Parse(XRechnung.Write(doc));

    private static string? Value(XDocument xml, string path) => xml.XPathSelectElement(path, Ns)?.Value;

    [Fact]
    public void Header_identifies_xrechnung_3()
    {
        var xml = Xml(Sample("domestic"));
        Assert.Equal(XRechnung.CustomizationId, Value(xml, "/ubl:Invoice/cbc:CustomizationID"));
        Assert.Equal("RE-2026-0042", Value(xml, "/ubl:Invoice/cbc:ID"));
        Assert.Equal("2026-11-18", Value(xml, "/ubl:Invoice/cbc:IssueDate"));
        Assert.Equal("2026-12-02", Value(xml, "/ubl:Invoice/cbc:DueDate"));
        Assert.Equal("380", Value(xml, "/ubl:Invoice/cbc:InvoiceTypeCode"));
        Assert.Equal("PO-4711", Value(xml, "/ubl:Invoice/cbc:BuyerReference"));
        Assert.Equal("DE89370400440532013000", Value(xml, "//cac:PaymentMeans/cac:PayeeFinancialAccount/cbc:ID"));
    }

    [Fact]
    public void Domestic_totals_and_breakdown_match_the_domain()
    {
        var doc = Sample("domestic");
        var xml = Xml(doc);
        Assert.Equal(doc.Totals.NetTotal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            Value(xml, "//cac:LegalMonetaryTotal/cbc:LineExtensionAmount"));
        Assert.Equal("956.21", Value(xml, "//cac:LegalMonetaryTotal/cbc:PayableAmount")); // 628.20 + 195.00 net, 119.36 + 13.65 VAT
        var subtotals = xml.XPathSelectElements("//cac:TaxTotal/cac:TaxSubtotal", Ns)
            .Select(s => (Value(new XDocument(s), "//cac:TaxCategory/cbc:Percent"), Value(new XDocument(s), "/cac:TaxSubtotal/cbc:TaxAmount")))
            .ToList();
        Assert.Equal([("19", "119.36"), ("7", "13.65")], subtotals);
        Assert.Equal("EUR", xml.XPathSelectElement("//cac:TaxTotal/cbc:TaxAmount", Ns)!.Attribute("currencyID")!.Value);
    }

    [Fact]
    public void Line_discount_is_an_allowance()
    {
        var xml = Xml(Sample("domestic"));
        var line = xml.XPathSelectElement("//cac:InvoiceLine[cbc:ID='1']", Ns)!;
        var lineDoc = new XDocument(line);
        Assert.Equal("628.20", Value(lineDoc, "//cbc:LineExtensionAmount"));
        Assert.Equal("69.80", Value(lineDoc, "//cac:AllowanceCharge/cbc:Amount"));
        Assert.Equal("698.00", Value(lineDoc, "//cac:AllowanceCharge/cbc:BaseAmount"));
        Assert.Equal("349.00", Value(lineDoc, "//cac:Price/cbc:PriceAmount"));
    }

    [Fact]
    public void Reverse_charge_has_category_AE_with_reason_and_buyer_vat_id()
    {
        var xml = Xml(Sample("reverse-charge"));
        Assert.Equal("AE", Value(xml, "//cac:TaxSubtotal/cac:TaxCategory/cbc:ID"));
        Assert.Equal("0", Value(xml, "//cac:TaxSubtotal/cac:TaxCategory/cbc:Percent"));
        Assert.Contains("Reverse charge", Value(xml, "//cac:TaxSubtotal/cac:TaxCategory/cbc:TaxExemptionReason"), StringComparison.Ordinal);
        Assert.Equal("FR40303265045", Value(xml, "//cac:AccountingCustomerParty//cac:PartyTaxScheme/cbc:CompanyID"));
        Assert.Equal("0.00", Value(xml, "//cac:TaxTotal/cbc:TaxAmount"));
    }

    [Fact]
    public void Out_of_scope_services_have_no_rate_and_no_vat_ids()
    {
        var xml = Xml(Sample("services-outside-eu"));
        Assert.Equal("O", Value(xml, "//cac:TaxSubtotal/cac:TaxCategory/cbc:ID"));
        Assert.Null(xml.XPathSelectElement("//cac:TaxSubtotal/cac:TaxCategory/cbc:Percent", Ns));
        Assert.Null(xml.XPathSelectElement("//cac:PartyTaxScheme", Ns));
    }

    [Fact]
    public void Special_characters_are_escaped()
    {
        var doc = Sample("domestic") with { Buyer = GermanBusiness with { Name = "Müller & Söhne <GmbH>" } };
        var xml = Xml(doc);
        Assert.Equal("Müller & Söhne <GmbH>", Value(xml, "//cac:AccountingCustomerParty//cbc:RegistrationName"));
    }

    [Fact]
    public void Missing_data_is_explained()
    {
        var doc = Sample("domestic") with { Iban = null, Seller = Seller with { Phone = null }, Buyer = GermanBusiness with { Email = null } };
        var missing = XRechnung.MissingData(doc);
        Assert.Equal(3, missing.Count);
        Assert.Throws<DomainException>(() => XRechnung.Write(doc));
    }

    [Fact]
    public void Drafts_cannot_be_exported()
    {
        var draft = new Invoice(Guid.NewGuid(), Guid.NewGuid());
        draft.AddLine(new InvoiceLine("X", 1, 1m, VatCategory.Standard, 19m));
        Assert.Contains(XRechnung.MissingData(new InvoiceDocument(draft, Seller, GermanBusiness, "DE89370400440532013000")),
            m => m.Contains("issued", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Samples_are_well_formed_and_saved_for_the_official_validator(string name)
    {
        var xml = XRechnung.Write(Sample(name));
        XDocument.Parse(xml); // well-formed
        if (Environment.GetEnvironmentVariable("XRECHNUNG_SAMPLES_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{name}.xml"), xml);
        }
    }

    private static System.Xml.XmlNamespaceManager CreateNamespaces()
    {
        var ns = new System.Xml.XmlNamespaceManager(new System.Xml.NameTable());
        ns.AddNamespace("ubl", "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2");
        ns.AddNamespace("cac", "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2");
        ns.AddNamespace("cbc", "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2");
        return ns;
    }
}
