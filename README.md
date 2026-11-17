# InvoiceHub: invoicing API for EU freelancers and small businesses

A REST API for customers and invoices that gets European VAT right and produces German e-invoices (XRechnung).

> 🚧 Work in progress: PDF and XRechnung export, Docker and a live demo are coming next.

## Why

Since 2025 every business in Germany must be able to receive e-invoices, and issuing them becomes mandatory in 2027–2028.
InvoiceHub implements the rules an invoicing backend needs, with tests for each of them.

## Highlights

- **EU VAT rules:** standard rates of all 27 member states, reverse charge for B2B services within the EU, tax-free intra-community supplies of goods, exports, with the legally required invoice note in English and German.
- **Totals exactly as EN 16931 requires:** VAT is calculated per rate on the summed line amounts, not per line (the difference shows up in cents, and validators reject the invoice).
- **VAT ID validation** for 16 countries, including the ISO 7064 check digit of German VAT IDs.
- **Bookkeeping rules (GoBD):** issued invoices are immutable, corrections need a cancellation with a reason.
- **Gapless invoice numbers** (2026-0001, 2026-0002 …) per seller and year, guaranteed by PostgreSQL even under concurrent requests (tested with 20 parallel requests).
- **Automatic VAT treatment:** the API decides standard rate, reverse charge or export from seller, customer and type of supply; reduced rates (e.g. 7 %) can be set per line.
- **Secure multi-tenant API:** JWT auth, every query scoped to the signed-in seller, rate-limited sign-in, RFC 9457 problem details for errors, database constraints as a second line of defence.
- **Interactive API docs** (OpenAPI 3.1 + Scalar) at `/docs`.
- **Clean architecture:** business rules live in a domain project with no dependencies on the web or the database.

## Tech stack

C# 14 · .NET 10 (LTS) · ASP.NET Core minimal APIs · Entity Framework Core · PostgreSQL · JWT · OpenAPI + Scalar · xUnit · WebApplicationFactory · strict compiler settings (nullable, warnings as errors, analyzers)

## Roadmap

- [x] Domain: amounts, VAT categories, tax rules, VAT IDs, invoice lifecycle + 45 tests
- [x] PostgreSQL with EF Core, gapless invoice numbers
- [x] REST API with JWT auth and OpenAPI docs
- [x] Integration tests against a real PostgreSQL database (64 tests in total)
- [ ] PDF invoices and XRechnung (UBL) export
- [ ] Docker, CI and live demo

## Run locally

Requirements: .NET 10 SDK, PostgreSQL.

```bash
cd src/InvoiceHub.Api
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Database=invoicehub;Username=postgres;Password=…"
dotnet user-secrets set "ConnectionStrings:Test" "Host=localhost;Database=invoicehub_test;Username=postgres;Password=…"
dotnet user-secrets set "Jwt:Key" "<at least 32 random characters>"
cd ../..
dotnet tool restore
dotnet ef database update -p src/InvoiceHub.Api
dotnet run --project src/InvoiceHub.Api   # API docs on http://localhost:5xxx/docs
dotnet test
```
