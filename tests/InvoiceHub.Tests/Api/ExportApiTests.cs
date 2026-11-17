using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using InvoiceHub.Api.Endpoints;

namespace InvoiceHub.Tests.Api;

public class ExportApiTests(ApiFactory factory) : ApiTest(factory)
{
    private async Task<(HttpClient Seller, Guid InvoiceId)> IssuedInvoice(bool completeProfile)
    {
        var seller = await Factory.SellerAsync();
        if (completeProfile)
        {
            var profile = await seller.PutAsJsonAsync("/api/me", new
            {
                companyName = "Kaffeewerk GmbH", street = "Musterstraße 1", postalCode = "10115", city = "Berlin",
                vatId = "DE123456788", iban = "DE89 3704 0044 0532 0130 00", contactName = "Erika Muster", phone = "+49 30 1234567",
                invoicePrefix = "RE-",
            });
            Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        }

        var customer = (await (await seller.PostAsJsonAsync("/api/customers", CustomerTests.FrenchBusiness))
            .Content.ReadFromJsonAsync<CustomerResponse>(ApiFactory.Json))!.Id;
        var draft = (await (await seller.PostAsJsonAsync("/api/invoices", new
        {
            customerId = customer, supplyType = "Services", buyerReference = "PO-4711",
            lines = new[] { new { description = "Barista training", quantity = 2, unit = "DAY", unitPrice = 650 } },
        })).Content.ReadFromJsonAsync<InvoiceResponse>(ApiFactory.Json))!;
        (await seller.PostAsJsonAsync($"/api/invoices/{draft.Id}/issue", new { issueDate = "2026-11-18" })).EnsureSuccessStatusCode();
        return (seller, draft.Id);
    }

    [Fact]
    public async Task Pdf_download()
    {
        var (seller, id) = await IssuedInvoice(completeProfile: false);
        var res = await seller.GetAsync($"/api/invoices/{id}/pdf");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("invoice-2026-0001.pdf", res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString((await res.Content.ReadAsByteArrayAsync())[..5]));
    }

    [Fact]
    public async Task Xrechnung_lists_missing_profile_data()
    {
        var (seller, id) = await IssuedInvoice(completeProfile: false);
        var res = await seller.GetAsync($"/api/invoices/{id}/xrechnung");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("BR-DE-2", body, StringComparison.Ordinal);
        Assert.DoesNotContain("BR-DE-1", body, StringComparison.Ordinal); // IBAN was given at registration
    }

    [Fact]
    public async Task Xrechnung_download_with_complete_profile()
    {
        var (seller, id) = await IssuedInvoice(completeProfile: true);
        var res = await seller.GetAsync($"/api/invoices/{id}/xrechnung");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var xml = XDocument.Parse(await res.Content.ReadAsStringAsync());
        XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        Assert.Equal("RE-2026-0001", xml.Root!.Element(cbc + "ID")!.Value);
        Assert.Equal("PO-4711", xml.Root!.Element(cbc + "BuyerReference")!.Value);
    }

    [Fact]
    public async Task Other_sellers_cannot_download()
    {
        var (_, id) = await IssuedInvoice(completeProfile: true);
        var other = await Factory.SellerAsync("intruder@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/invoices/{id}/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/invoices/{id}/xrechnung")).StatusCode);
    }
}
