using System.Net;
using System.Net.Http.Json;
using InvoiceHub.Api.Endpoints;
using InvoiceHub.Domain;

namespace InvoiceHub.Tests.Api;

public class InvoiceApiTests(ApiFactory factory) : ApiTest(factory)
{
    private static async Task<Guid> AddCustomer(HttpClient seller, object customer) =>
        (await (await seller.PostAsJsonAsync("/api/customers", customer)).Content.ReadFromJsonAsync<CustomerResponse>(ApiFactory.Json))!.Id;

    private static readonly object GermanConsumer = new
    {
        name = "Max Mustermann", street = "Lindenweg 4", postalCode = "20095", city = "Hamburg", country = "DE",
    };

    private static async Task<InvoiceResponse> Draft(HttpClient seller, Guid customerId, string supply = "Services", params object[] lines)
    {
        var res = await seller.PostAsJsonAsync("/api/invoices", new { customerId, supplyType = supply, lines });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<InvoiceResponse>(ApiFactory.Json))!;
    }

    private static async Task<InvoiceResponse> Issue(HttpClient seller, Guid id, string? date = null)
    {
        var res = await seller.PostAsJsonAsync($"/api/invoices/{id}/issue", new { issueDate = date, paymentTermDays = 14 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<InvoiceResponse>(ApiFactory.Json))!;
    }

    [Fact]
    public async Task Services_to_an_eu_business_are_reverse_charged_automatically()
    {
        var seller = await Factory.SellerAsync();
        var customer = await AddCustomer(seller, CustomerTests.FrenchBusiness);

        var invoice = await Draft(seller, customer, "Services", new { description = "Web development", quantity = 32, unit = "HUR", unitPrice = 85 });

        var line = Assert.Single(invoice.Lines);
        Assert.Equal((VatCategory.ReverseCharge, "AE", 0m), (line.VatCategory, line.VatCategoryCode, line.VatRate));
        Assert.Equal((2720m, 0m, 2720m), (invoice.NetTotal, invoice.VatTotal, invoice.GrossTotal));
        Assert.Contains("Reverse charge", invoice.TaxNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Domestic_sale_with_standard_and_reduced_rates()
    {
        var seller = await Factory.SellerAsync();
        var customer = await AddCustomer(seller, GermanConsumer);

        var invoice = await Draft(seller, customer, "Goods",
            new { description = "Espresso machine", quantity = 1, unitPrice = 349 },
            new { description = "Coffee beans 1 kg", quantity = 2, unitPrice = 16.25, vatRate = 7 });

        Assert.Equal([("S", 19m, 66.31m), ("S", 7m, 2.28m)], invoice.VatBreakdown.Select(b => (b.CategoryCode, b.Rate, b.TaxAmount)));
        Assert.Equal(450.09m, invoice.GrossTotal);
        Assert.Null(invoice.TaxNote);
    }

    [Fact]
    public async Task Issuing_numbers_invoices_without_gaps_per_year()
    {
        var seller = await Factory.SellerAsync();
        var customer = await AddCustomer(seller, GermanConsumer);
        var line = new { description = "Workshop", quantity = 1, unitPrice = 900 };

        var first = await Issue(seller, (await Draft(seller, customer, "Services", line)).Id, "2026-11-02");
        var second = await Issue(seller, (await Draft(seller, customer, "Services", line)).Id, "2026-11-05");
        var nextYear = await Issue(seller, (await Draft(seller, customer, "Services", line)).Id, "2027-01-04");

        Assert.Equal(["2026-0001", "2026-0002", "2027-0001"], new[] { first.Number!, second.Number!, nextYear.Number! });
        Assert.Equal(new DateOnly(2026, 11, 16), first.DueDate);
    }

    [Fact]
    public async Task Parallel_issuing_never_skips_or_repeats_a_number()
    {
        var seller = await Factory.SellerAsync();
        var customer = await AddCustomer(seller, GermanConsumer);
        var drafts = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            drafts.Add((await Draft(seller, customer, "Services", new { description = $"Item {i}", quantity = 1, unitPrice = 10 })).Id);
        }

        // 20 requests at the same time, each from its own client.
        var token = seller.DefaultRequestHeaders.Authorization;
        var issued = await Task.WhenAll(drafts.Select(async id =>
        {
            using var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = token;
            return await Issue(client, id, "2026-11-20");
        }));

        Assert.Equal(Enumerable.Range(1, 20).Select(n => $"2026-{n:D4}"), issued.Select(i => i.Number).Order());
    }

    [Fact]
    public async Task Issued_invoices_are_locked_and_follow_the_lifecycle()
    {
        var seller = await Factory.SellerAsync();
        var customer = await AddCustomer(seller, GermanConsumer);
        var draft = await Draft(seller, customer, "Services", new { description = "Logo", quantity = 1, unitPrice = 500 });
        await Issue(seller, draft.Id, "2026-11-02");

        var edit = await seller.PutAsJsonAsync($"/api/invoices/{draft.Id}", new
        {
            customerId = customer, supplyType = "Services", lines = new[] { new { description = "Logo", quantity = 1, unitPrice = 1 } },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await seller.DeleteAsync($"/api/invoices/{draft.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.DeleteAsync($"/api/customers/{customer}")).StatusCode);

        var paid = await seller.PostAsJsonAsync($"/api/invoices/{draft.Id}/pay", new { paidOn = "2026-11-10" });
        var body = (await paid.Content.ReadFromJsonAsync<InvoiceResponse>(ApiFactory.Json))!;
        Assert.Equal((InvoiceStatus.Paid, 0m), (body.Status, body.AmountDue));

        var cancel = await seller.PostAsJsonAsync($"/api/invoices/{draft.Id}/cancel", new { reason = "Mistake" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cancel.StatusCode);
    }

    [Fact]
    public async Task Drafts_can_be_edited_and_deleted()
    {
        var seller = await Factory.SellerAsync();
        var customer = await AddCustomer(seller, GermanConsumer);
        var draft = await Draft(seller, customer, "Services", new { description = "A", quantity = 1, unitPrice = 100 });

        var edited = await seller.PutAsJsonAsync($"/api/invoices/{draft.Id}", new
        {
            customerId = customer, supplyType = "Services", note = "Thank you!",
            lines = new[] { new { description = "B", quantity = 2, unitPrice = 50.5 } },
        });
        var body = (await edited.Content.ReadFromJsonAsync<InvoiceResponse>(ApiFactory.Json))!;
        Assert.Equal(("B", 101m, "Thank you!"), (body.Lines.Single().Description, body.NetTotal, body.Note));

        Assert.Equal(HttpStatusCode.NoContent, (await seller.DeleteAsync($"/api/invoices/{draft.Id}")).StatusCode);
    }

    [Fact]
    public async Task Invalid_input_and_foreign_invoices()
    {
        var anna = await Factory.SellerAsync("anna@studio.example");
        var ben = await Factory.SellerAsync("ben@studio.example");
        var customer = await AddCustomer(anna, GermanConsumer);

        var noLines = await anna.PostAsJsonAsync("/api/invoices", new { customerId = customer, supplyType = "Services", lines = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, noLines.StatusCode);
        var negative = await anna.PostAsJsonAsync("/api/invoices", new
        {
            customerId = customer, supplyType = "Services", lines = new[] { new { description = "X", quantity = -1, unitPrice = 1 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);

        var draft = await Draft(anna, customer, "Services", new { description = "X", quantity = 1, unitPrice = 1 });
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/invoices/{draft.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.PostAsJsonAsync($"/api/invoices/{draft.Id}/issue", new { })).StatusCode);
        var benUsingAnnasCustomer = await ben.PostAsJsonAsync("/api/invoices", new
        {
            customerId = customer, supplyType = "Services", lines = new[] { new { description = "X", quantity = 1, unitPrice = 1 } },
        });
        Assert.Equal(HttpStatusCode.NotFound, benUsingAnnasCustomer.StatusCode);
    }
}
