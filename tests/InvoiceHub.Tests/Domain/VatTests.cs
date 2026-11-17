using InvoiceHub.Domain;

namespace InvoiceHub.Tests.Domain;

public class VatIdTests
{
    [Theory]
    [InlineData("DE123456788", "DE123456788", "DE")]
    [InlineData("de 123 456 788", "DE123456788", "DE")]
    [InlineData("ATU12345678", "ATU12345678", "AT")]
    [InlineData("FR40303265045", "FR40303265045", "FR")]
    [InlineData("NL123456789B01", "NL123456789B01", "NL")]
    [InlineData("EL123456789", "EL123456789", "GR")]
    [InlineData("IE1234567T", "IE1234567T", "IE")]
    [InlineData("ES-X1234567-L", "ESX1234567L", "ES")]
    public void Valid_ids_are_normalized(string input, string value, string country)
    {
        Assert.True(VatId.TryParse(input, out var id, out var error), error);
        Assert.Equal((value, country), (id.Value, id.Country));
    }

    [Theory]
    [InlineData("DE123456789", "check digit")] // right format, wrong check digit
    [InlineData("DE12345678", "format")]
    [InlineData("ATU1234567", "format")]
    [InlineData("US123456789", "not supported")]
    [InlineData("", "too short")]
    [InlineData(null, "too short")]
    public void Invalid_ids_explain_why(string? input, string message)
    {
        Assert.False(VatId.TryParse(input, out _, out var error));
        Assert.Contains(message, error, StringComparison.OrdinalIgnoreCase);
    }
}

public class TaxRulesTests
{
    private static readonly VatId FrenchBusiness = VatId.Parse("FR40303265045");

    [Fact]
    public void Domestic_sale_uses_the_standard_rate()
    {
        var t = TaxRules.Determine("DE", "de", VatId.Parse("DE123456788"), SupplyType.Services);
        Assert.Equal((VatCategory.Standard, 19m, (string?)null), (t.Category, t.Rate, t.InvoiceNote));
    }

    [Fact]
    public void Services_to_a_business_in_another_eu_country_are_reverse_charged()
    {
        var t = TaxRules.Determine("DE", "FR", FrenchBusiness, SupplyType.Services);
        Assert.Equal((VatCategory.ReverseCharge, 0m), (t.Category, t.Rate));
        Assert.Contains("Reverse charge", t.InvoiceNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Goods_to_a_business_in_another_eu_country_are_an_intra_community_supply()
    {
        var t = TaxRules.Determine("DE", "FR", FrenchBusiness, SupplyType.Goods);
        Assert.Equal(VatCategory.IntraCommunity, t.Category);
    }

    [Fact]
    public void Consumer_in_another_eu_country_pays_the_sellers_rate() =>
        Assert.Equal(20m, TaxRules.Determine("AT", "IT", null, SupplyType.Goods).Rate);

    [Theory]
    [InlineData(SupplyType.Goods, VatCategory.Export)]
    [InlineData(SupplyType.Services, VatCategory.NotSubject)]
    public void Customers_outside_the_eu_pay_no_eu_vat(SupplyType supply, VatCategory expected) =>
        Assert.Equal(expected, TaxRules.Determine("NL", "US", null, supply).Category);

    [Fact]
    public void Finland_uses_its_current_rate() =>
        Assert.Equal(25.5m, TaxRules.Determine("FI", "FI", null, SupplyType.Services).Rate);

    [Fact]
    public void Vat_id_must_match_the_buyers_country() =>
        Assert.Throws<DomainException>(() => TaxRules.Determine("DE", "IT", FrenchBusiness, SupplyType.Services));

    [Fact]
    public void Seller_must_be_in_the_eu() =>
        Assert.Throws<DomainException>(() => TaxRules.Determine("CH", "DE", null, SupplyType.Services));
}
