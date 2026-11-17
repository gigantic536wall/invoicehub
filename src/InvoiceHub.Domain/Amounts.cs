namespace InvoiceHub.Domain;

/// <summary>Money rounding as required by EN 16931 (European e-invoicing standard).</summary>
public static class Amounts
{
    /// <summary>Rounds to cents, halves away from zero (0.005 → 0.01), as commercial rounding in the EU.</summary>
    public static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>A business rule was violated, e.g. editing an invoice that was already issued.</summary>
public sealed class DomainException(string message) : Exception(message);
