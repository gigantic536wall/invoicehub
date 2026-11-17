using System.Collections.Frozen;

namespace InvoiceHub.Domain;

/// <summary>EU member states and their standard VAT rates.</summary>
/// <remarks>
/// Rates as of 2026. Reduced rates depend on the product, so they are set per invoice line.
/// Always check current rates before relying on them for real invoices.
/// </remarks>
public static class EuCountries
{
    public static readonly FrozenDictionary<string, decimal> StandardVatRates = new Dictionary<string, decimal>
    {
        ["AT"] = 20m, ["BE"] = 21m, ["BG"] = 20m, ["CY"] = 19m, ["CZ"] = 21m, ["DE"] = 19m, ["DK"] = 25m,
        ["EE"] = 24m, ["ES"] = 21m, ["FI"] = 25.5m, ["FR"] = 20m, ["GR"] = 24m, ["HR"] = 25m, ["HU"] = 27m,
        ["IE"] = 23m, ["IT"] = 22m, ["LT"] = 21m, ["LU"] = 17m, ["LV"] = 21m, ["MT"] = 18m, ["NL"] = 21m,
        ["PL"] = 23m, ["PT"] = 23m, ["RO"] = 21m, ["SE"] = 25m, ["SI"] = 22m, ["SK"] = 23m,
    }.ToFrozenDictionary();

    public static bool IsMember(string countryCode) => StandardVatRates.ContainsKey(Normalize(countryCode));

    public static decimal StandardRate(string countryCode) =>
        StandardVatRates.TryGetValue(Normalize(countryCode), out var rate)
            ? rate
            : throw new DomainException($"'{countryCode}' is not an EU member state.");

    /// <summary>ISO 3166 code in upper case. Greece uses "EL" in VAT numbers but "GR" in addresses.</summary>
    public static string Normalize(string countryCode)
    {
        var code = countryCode.Trim().ToUpperInvariant();
        return code == "EL" ? "GR" : code;
    }
}
