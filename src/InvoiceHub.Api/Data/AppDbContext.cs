using InvoiceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceCounter> InvoiceCounters => Set<InvoiceCounter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(254);
            e.Property(u => u.CompanyName).HasMaxLength(200);
            e.Property(u => u.Country).HasMaxLength(2).IsFixedLength();
            e.Property(u => u.VatId).HasMaxLength(20);
            e.Property(u => u.Iban).HasMaxLength(34);
            e.Property(u => u.InvoicePrefix).HasMaxLength(10);
            e.Property(u => u.ContactName).HasMaxLength(200);
            e.Property(u => u.Phone).HasMaxLength(40);
        });

        modelBuilder.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.HasIndex(c => c.OwnerId);
            e.HasOne<User>().WithMany().HasForeignKey(c => c.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(c => c.Name).HasMaxLength(200);
            e.Property(c => c.Email).HasMaxLength(254);
            e.Property(c => c.Country).HasMaxLength(2).IsFixedLength();
            e.Property(c => c.VatId).HasMaxLength(20);
        });

        modelBuilder.Entity<InvoiceCounter>(e =>
        {
            e.ToTable("invoice_counters");
            e.HasKey(c => new { c.OwnerId, c.Year });
            e.HasOne<User>().WithMany().HasForeignKey(c => c.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Invoice>(e =>
        {
            e.ToTable("invoices", t =>
            {
                t.HasCheckConstraint("ck_invoices_issued_has_number", "\"Status\" = 'Draft' OR (\"Number\" IS NOT NULL AND \"IssueDate\" IS NOT NULL)");
                t.HasCheckConstraint("ck_invoices_paid_has_date", "\"Status\" <> 'Paid' OR \"PaidOn\" IS NOT NULL");
            });
            e.HasKey(i => i.Id);
            e.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(i => i.Number).HasMaxLength(30);
            e.Property(i => i.Note).HasMaxLength(2000);
            e.Property(i => i.BuyerReference).HasMaxLength(100);
            e.Property(i => i.TaxNote).HasMaxLength(500);
            e.Property(i => i.CancellationReason).HasMaxLength(500);
            // The same number can never be used twice by one seller, even if application code has a bug.
            e.HasIndex(i => new { i.OwnerId, i.Number }).IsUnique().HasFilter("\"Number\" IS NOT NULL");
            e.HasIndex(i => new { i.OwnerId, i.Status });
            e.HasOne<User>().WithMany().HasForeignKey(i => i.OwnerId).OnDelete(DeleteBehavior.Cascade);
            // Customers with invoices cannot be deleted: invoices must be kept for 10 years.
            e.HasOne<Customer>().WithMany().HasForeignKey(i => i.CustomerId).OnDelete(DeleteBehavior.Restrict);

            e.OwnsMany(i => i.Lines, l =>
            {
                l.ToTable("invoice_lines", t => t.HasCheckConstraint("ck_invoice_lines_quantity", "\"Quantity\" > 0"));
                l.WithOwner().HasForeignKey("InvoiceId");
                l.HasKey(x => x.Id);
                l.Property(x => x.Description).HasMaxLength(500);
                l.Property(x => x.Unit).HasMaxLength(3);
                l.Property(x => x.Quantity).HasPrecision(14, 4);
                l.Property(x => x.UnitPrice).HasPrecision(14, 4);
                l.Property(x => x.DiscountPercent).HasPrecision(5, 2);
                l.Property(x => x.VatRate).HasPrecision(5, 2);
                l.Property(x => x.VatCategory).HasConversion<string>().HasMaxLength(20);
            });
            e.Navigation(i => i.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");
        });
    }
}
