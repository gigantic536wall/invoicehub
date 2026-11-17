# InvoiceHub: invoicing API for EU freelancers and small businesses

[![CI](https://github.com/gigantic536wall/invoicehub/actions/workflows/ci.yml/badge.svg)](https://github.com/gigantic536wall/invoicehub/actions/workflows/ci.yml)

A REST API for customers and invoices that gets European VAT right and produces PDF invoices and German e-invoices (XRechnung).
Every build checks the generated e-invoices with the official KoSIT validator.

> 🚧 Work in progress: Docker and a live demo are coming next.

<p align="center"><img src="docs/invoice-domestic.png" width="560" alt="Generated PDF invoice with 19 % and 7 % VAT"></p>

## Why

Since 2025 every business in Germany must be able to receive e-invoices, and issuing them becomes mandatory in 2027–2028.
InvoiceHub implements the rules an invoicing backend needs, with tests for each of them.

## Highlights

- **EU VAT rules:** standard rates of all 27 member states, reverse charge for B2B services within the EU, tax-free intra-community supplies of goods, exports, with the legally required invoice note in English and German.
- **Totals exactly as EN 16931 requires:** VAT is calculated per rate on the summed line amounts, not per line (the difference shows up in cents, and validators reject the invoice).
- **XRechnung 3.0 e-invoices** (UBL 2.1, EN 16931) for every VAT situation, validated in CI with the [official KoSIT validator](https://github.com/itplr-kosit/validator), the one German public authorities use. If data required by XRechnung is missing (e.g. the seller's contact person), the API lists exactly what to add instead of producing an invalid file.
- **PDF invoices** in German business-letter layout (DIN 5008 address window, bilingual German/English) with VAT breakdown, tax notes and payment details.
- **VAT ID validation** for 16 countries, including the ISO 7064 check digit of German VAT IDs.
- **Bookkeeping rules (GoBD):** issued invoices are immutable, corrections need a cancellation with a reason.
- **Gapless invoice numbers** (2026-0001, 2026-0002 …) per seller and year, guaranteed by PostgreSQL even under concurrent requests (tested with 20 parallel requests).
- **Automatic VAT treatment:** the API decides standard rate, reverse charge or export from seller, customer and type of supply; reduced rates (e.g. 7 %) can be set per line.
- **Secure multi-tenant API:** JWT auth, every query scoped to the signed-in seller, rate-limited sign-in, RFC 9457 problem details for errors, database constraints as a second line of defence.
- **Interactive API docs** (OpenAPI 3.1 + Scalar) at `/docs`.
- **Clean architecture:** business rules live in a domain project with no dependencies on the web or the database.

## Tech stack

C# 14 · .NET 10 (LTS) · ASP.NET Core minimal APIs · Entity Framework Core · PostgreSQL · JWT · OpenAPI + Scalar · QuestPDF · UBL / XRechnung · xUnit · WebApplicationFactory · GitHub Actions · strict compiler settings (nullable, warnings as errors, analyzers)

## Export endpoints

| Endpoint | Result |
| --- | --- |
| `GET /api/invoices/{id}/pdf` | PDF invoice (drafts are marked "ENTWURF / DRAFT") |
| `GET /api/invoices/{id}/xrechnung` | XRechnung 3.0 XML, or `422` with the list of missing data |

For XRechnung the seller profile (`PUT /api/me`) needs a contact name, phone and IBAN, and the customer needs an email address.

## Roadmap

- [x] Domain: amounts, VAT categories, tax rules, VAT IDs, invoice lifecycle
- [x] PostgreSQL with EF Core, gapless invoice numbers
- [x] REST API with JWT auth and OpenAPI docs
- [x] Integration tests against a real PostgreSQL database
- [x] PDF invoices and XRechnung (UBL) export, validated with KoSIT in CI
- [x] Docker image, smoke-tested in CI; one-click demo accounts (91 tests in total)
- [ ] Live demo

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

## Docker and deployment

```bash
docker build -t invoicehub .
docker run -p 8080:8080 \
  -e ConnectionStrings__Default="postgres://user:password@host:5432/invoicehub" \
  -e Jwt__Key="<at least 32 random characters>" \
  -e Demo__Enabled=true \
  invoicehub
```

The image runs as a non-root user, applies database migrations on start and accepts both Npgsql connection strings and `postgres://` URLs.
[`render.yaml`](render.yaml) deploys it to Render (Frankfurt); the database is a free Neon PostgreSQL instance.
With `Demo__Enabled`, `POST /api/demo` creates a demo seller with sample customers and invoices; demo accounts are deleted after 24 hours.

## License notes

PDFs are generated with [QuestPDF](https://www.questpdf.com/) under its Community license (free for individuals and companies with less than USD 1M annual revenue).
