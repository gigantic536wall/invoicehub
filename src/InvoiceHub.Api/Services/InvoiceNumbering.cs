using InvoiceHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Api.Services;

/// <summary>
/// Gapless, sequential invoice numbers per seller and year (e.g. 2026-0001, 2026-0002), as German tax rules expect.
/// </summary>
/// <remarks>
/// The counter row is incremented with a single INSERT … ON CONFLICT DO UPDATE, which locks the row until the
/// surrounding transaction ends. Parallel requests therefore wait for each other, and if issuing fails the
/// transaction rolls back and the number is not lost.
/// </remarks>
public static class InvoiceNumbering
{
    public static async Task<string> NextAsync(AppDbContext db, Guid ownerId, string prefix, int year, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Invoice numbers must be taken inside a transaction.");
        }

        // ToListAsync (not SingleAsync): EF must not wrap an INSERT … RETURNING in a sub-query.
        var rows = await db.Database
            .SqlQuery<int>($"""
                INSERT INTO invoice_counters ("OwnerId", "Year", "LastNumber")
                VALUES ({ownerId}, {year}, 1)
                ON CONFLICT ("OwnerId", "Year") DO UPDATE SET "LastNumber" = invoice_counters."LastNumber" + 1
                RETURNING "LastNumber" AS "Value"
                """)
            .ToListAsync(ct);
        var next = rows.Single();

        return $"{prefix}{year}-{next:D4}";
    }
}
