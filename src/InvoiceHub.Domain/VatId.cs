using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace InvoiceHub.Domain;

/// <summary>An EU VAT identification number, e.g. DE123456789. Checks the format (and the check digit for DE).</summary>
/// <remarks>Format checks catch typos; whether the number is active can be confirmed with the EU VIES service.</remarks>
public sealed partial record VatId
{
    private static readonly FrozenDictionary<string, Regex> Formats = new Dictionary<string, Regex>
    {
        ["AT"] = AtFormat(), ["BE"] = BeFormat(), ["CZ"] = CzFormat(), ["DE"] = DeFormat(), ["DK"] = DkFormat(),
        ["EL"] = ElFormat(), ["ES"] = EsFormat(), ["FI"] = FiFormat(), ["FR"] = FrFormat(), ["IE"] = IeFormat(),
        ["IT"] = ItFormat(), ["LU"] = LuFormat(), ["NL"] = NlFormat(), ["PL"] = PlFormat(), ["PT"] = PtFormat(),
        ["SE"] = SeFormat(),
    }.ToFrozenDictionary();

    private VatId(string value) => Value = value;

    /// <summary>Normalized value without spaces, dots or dashes, e.g. "DE123456789".</summary>
    public string Value { get; }

    /// <summary>Country of the buyer, as an ISO code ("EL" is returned as "GR").</summary>
    public string Country => EuCountries.Normalize(Value[..2]);

    public static bool TryParse(string? input, [NotNullWhen(true)] out VatId? vatId, out string? error)
    {
        vatId = null;
        var value = Cleanup().Replace(input ?? string.Empty, string.Empty).ToUpperInvariant();
        if (value.Length < 4)
        {
            error = "VAT ID is too short.";
            return false;
        }

        var prefix = value[..2];
        if (!Formats.TryGetValue(prefix, out var format))
        {
            error = $"VAT IDs from '{prefix}' are not supported.";
            return false;
        }

        if (!format.IsMatch(value))
        {
            error = $"'{value}' does not match the {prefix} VAT ID format.";
            return false;
        }

        if (prefix == "DE" && !HasValidGermanCheckDigit(value[2..]))
        {
            error = $"'{value}' has an invalid check digit.";
            return false;
        }

        error = null;
        vatId = new VatId(value);
        return true;
    }

    public static VatId Parse(string input) =>
        TryParse(input, out var vatId, out var error) ? vatId : throw new DomainException(error!);

    public override string ToString() => Value;

    /// <summary>German USt-IdNr. check digit: ISO 7064 MOD 11,10.</summary>
    private static bool HasValidGermanCheckDigit(string digits)
    {
        var product = 10;
        for (var i = 0; i < 8; i++)
        {
            var sum = (digits[i] - '0' + product) % 10;
            if (sum == 0)
            {
                sum = 10;
            }

            product = sum * 2 % 11;
        }

        var check = 11 - product;
        if (check == 10)
        {
            check = 0;
        }

        return check == digits[8] - '0';
    }

    [GeneratedRegex(@"[\s.\-]")] private static partial Regex Cleanup();
    [GeneratedRegex(@"^ATU\d{8}$")] private static partial Regex AtFormat();
    [GeneratedRegex(@"^BE[01]\d{9}$")] private static partial Regex BeFormat();
    [GeneratedRegex(@"^CZ\d{8,10}$")] private static partial Regex CzFormat();
    [GeneratedRegex(@"^DE\d{9}$")] private static partial Regex DeFormat();
    [GeneratedRegex(@"^DK\d{8}$")] private static partial Regex DkFormat();
    [GeneratedRegex(@"^EL\d{9}$")] private static partial Regex ElFormat();
    [GeneratedRegex(@"^ES[0-9A-Z]\d{7}[0-9A-Z]$")] private static partial Regex EsFormat();
    [GeneratedRegex(@"^FI\d{8}$")] private static partial Regex FiFormat();
    [GeneratedRegex(@"^FR[0-9A-HJ-NP-Z]{2}\d{9}$")] private static partial Regex FrFormat();
    [GeneratedRegex(@"^IE(\d{7}[A-W][A-I]?|\d[A-Z+*]\d{5}[A-W])$")] private static partial Regex IeFormat();
    [GeneratedRegex(@"^IT\d{11}$")] private static partial Regex ItFormat();
    [GeneratedRegex(@"^LU\d{8}$")] private static partial Regex LuFormat();
    [GeneratedRegex(@"^NL\d{9}B\d{2}$")] private static partial Regex NlFormat();
    [GeneratedRegex(@"^PL\d{10}$")] private static partial Regex PlFormat();
    [GeneratedRegex(@"^PT\d{9}$")] private static partial Regex PtFormat();
    [GeneratedRegex(@"^SE\d{12}$")] private static partial Regex SeFormat();
}
