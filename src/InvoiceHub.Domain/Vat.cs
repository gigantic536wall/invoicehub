namespace InvoiceHub.Domain;

/// <summary>VAT category codes from UNTDID 5305, as used in XRechnung / ZUGFeRD.</summary>
public enum VatCategory
{
    /// <summary>S: standard or reduced rate.</summary>
    Standard,
    /// <summary>Z: zero-rated goods.</summary>
    ZeroRated,
    /// <summary>E: exempt from VAT (e.g. some medical or educational services).</summary>
    Exempt,
    /// <summary>AE: reverse charge, the buyer pays the VAT.</summary>
    ReverseCharge,
    /// <summary>K: intra-community supply of goods to a business in another EU country.</summary>
    IntraCommunity,
    /// <summary>G: export of goods outside the EU.</summary>
    Export,
    /// <summary>O: services outside the scope of EU VAT (e.g. to a business outside the EU).</summary>
    NotSubject,
}

public static class VatCategoryCodes
{
    public static string Code(this VatCategory category) => category switch
    {
        VatCategory.Standard => "S",
        VatCategory.ZeroRated => "Z",
        VatCategory.Exempt => "E",
        VatCategory.ReverseCharge => "AE",
        VatCategory.IntraCommunity => "K",
        VatCategory.Export => "G",
        VatCategory.NotSubject => "O",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    /// <summary>Only these categories carry a VAT rate above zero.</summary>
    public static bool HasRate(this VatCategory category) => category == VatCategory.Standard;
}

public enum SupplyType
{
    Goods,
    Services,
}

/// <summary>How VAT applies to a sale, with the note that must be printed on the invoice.</summary>
public sealed record TaxTreatment(VatCategory Category, decimal Rate, string? InvoiceNote);

public static class TaxRules
{
    /// <summary>
    /// Default VAT treatment of a sale between the seller's and the buyer's country.
    /// Covers the common cases for EU businesses; special schemes (OSS for consumers abroad,
    /// margin schemes, domestic reverse charge for construction) need individual handling.
    /// </summary>
    public static TaxTreatment Determine(string sellerCountry, string buyerCountry, VatId? buyerVatId, SupplyType supply)
    {
        var seller = EuCountries.Normalize(sellerCountry);
        var buyer = EuCountries.Normalize(buyerCountry);
        if (!EuCountries.IsMember(seller))
        {
            throw new DomainException("The seller must be registered in an EU member state.");
        }

        var buyerIsBusiness = buyerVatId is not null;

        if (seller == buyer || (!buyerIsBusiness && EuCountries.IsMember(buyer)))
        {
            // Domestic sale, or a consumer elsewhere in the EU (below the €10,000 OSS threshold):
            // the seller's own standard rate applies.
            return new TaxTreatment(VatCategory.Standard, EuCountries.StandardRate(seller), null);
        }

        if (EuCountries.IsMember(buyer))
        {
            if (EuCountries.Normalize(buyerVatId!.Country) != buyer)
            {
                throw new DomainException($"The buyer's VAT ID is from {buyerVatId.Country}, but the buyer is in {buyer}.");
            }

            return supply == SupplyType.Goods
                ? new TaxTreatment(VatCategory.IntraCommunity, 0m,
                    "Tax-exempt intra-community supply (Art. 138 VAT Directive). / Steuerfreie innergemeinschaftliche Lieferung.")
                : new TaxTreatment(VatCategory.ReverseCharge, 0m,
                    "Reverse charge: VAT to be paid by the recipient (Art. 196 VAT Directive). / Steuerschuldnerschaft des Leistungsempfängers.");
        }

        // Buyer outside the EU.
        return supply == SupplyType.Goods
            ? new TaxTreatment(VatCategory.Export, 0m, "Tax-exempt export delivery. / Steuerfreie Ausfuhrlieferung.")
            : new TaxTreatment(VatCategory.NotSubject, 0m, "Not subject to VAT in the EU (place of supply outside the EU).");
    }
}
