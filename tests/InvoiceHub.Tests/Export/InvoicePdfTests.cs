using System.Text;
using InvoiceHub.Api.Export;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace InvoiceHub.Tests.Export;

public class InvoicePdfTests
{
    [Theory]
    [MemberData(nameof(XRechnungTests.Samples), MemberType = typeof(XRechnungTests))]
    public void Renders_a_pdf_for_every_vat_situation(string sample)
    {
        var pdf = InvoicePdf.Render(XRechnungTests.Sample(sample));

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        Assert.InRange(pdf.Length, 5_000, 2_000_000);
        if (Environment.GetEnvironmentVariable("XRECHNUNG_SAMPLES_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"{sample}.pdf"), pdf);
            var png = InvoicePdf.Build(XRechnungTests.Sample(sample)).GenerateImages(new ImageGenerationSettings { RasterDpi = 110 }).First();
            File.WriteAllBytes(Path.Combine(dir, $"{sample}.png"), png);
        }
    }
}
