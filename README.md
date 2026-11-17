# InvoiceHub: invoicing API for EU freelancers and small businesses

A REST API for customers and invoices that gets European VAT right and produces German e-invoices (XRechnung).

> 🚧 Work in progress: database, REST API, PDF and XRechnung export are coming next.

## Why

Since 2025 every business in Germany must be able to receive e-invoices, and issuing them becomes mandatory in 2027–2028.
InvoiceHub implements the rules an invoicing backend needs, with tests for each of them.

## Highlights

- **EU VAT rules:** standard rates of all 27 member states, reverse charge for B2B services within the EU, tax-free intra-community supplies of goods, exports, with the legally required invoice note in English and German.
- **Totals exactly as EN 16931 requires:** VAT is calculated per rate on the summed line amounts, not per line (the difference shows up in cents, and validators reject the invoice).
- **VAT ID validation** for 16 countries, including the ISO 7064 check digit of German VAT IDs.
- **Bookkeeping rules (GoBD):** issued invoices are immutable, corrections need a cancellation with a reason.
- **Clean architecture:** business rules live in a domain project with no dependencies on the web or the database.

## Tech stack

C# 14 · .NET 10 · ASP.NET Core · xUnit · strict compiler settings (nullable, warnings as errors, analyzers)

## Roadmap

- [x] Domain: amounts, VAT categories, tax rules, VAT IDs, invoice lifecycle + 45 tests
- [ ] PostgreSQL with EF Core, gapless invoice numbers
- [ ] REST API with JWT auth and OpenAPI docs
- [ ] PDF invoices and XRechnung (UBL) export
- [ ] Docker, CI and live demo

## Run the tests

```bash
dotnet test
```
