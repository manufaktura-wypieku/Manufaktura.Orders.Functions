using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class DeliveryNoteWordTemplateFillerTests
{
    private static readonly string RealTemplatePath = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "delivery_note_patterns_footer.docx");

    [Fact]
    public void FillWritesScalarFieldsBySchemaTags()
    {
        var template = CreateTemplateDocx();
        var filler = new DeliveryNoteWordTemplateFiller();
        var source = CreateSource();

        var filled = filler.Fill(template, source);

        using var stream = new MemoryStream(filled);
        using var doc = WordprocessingDocument.Open(stream, false);
        var text = doc.MainDocumentPart!.Document.Body!.InnerText;
        Assert.Contains("Cafe", text);
        Assert.Contains("42", text);
        Assert.Contains("Ul. Prosta 1", text);
        Assert.Contains("Warszawa", text);
        Assert.Contains("00-001", text);
        Assert.Contains("C", text);
        Assert.Contains("21/09/2026", text);
        Assert.Contains("Chleb", text);
        Assert.Contains("Bread", text);
    }

    [Fact]
    public void FillRealSharePointTemplateWritesKnownFields()
    {
        Assert.True(File.Exists(RealTemplatePath), $"Missing fixture at {RealTemplatePath}");
        var template = File.ReadAllBytes(RealTemplatePath);
        var filler = new DeliveryNoteWordTemplateFiller();
        var source = CreateSource();

        var filled = filler.Fill(template, source);

        using var stream = new MemoryStream(filled);
        using var doc = WordprocessingDocument.Open(stream, false);
        var text = doc.MainDocumentPart!.Document.Body!.InnerText;
        Assert.Contains("Cafe", text);
        Assert.Contains("42", text);
        Assert.Contains("21/09/2026", text);
        Assert.Contains("Chleb", text);
        Assert.Contains("2", text);
    }

    [Fact]
    public void FormatDeliveryDateIsDayMonthYear()
    {
        Assert.Equal("21/09/2026", DeliveryNoteWordTemplateFiller.FormatDeliveryDate(
            new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(string.Empty, DeliveryNoteWordTemplateFiller.FormatDeliveryDate(null));
    }

    private static DeliveryNoteDocumentSource CreateSource() =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            null,
            "ORD-1",
            new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero),
            10.5m,
            new DeliveryNoteCustomer("Cafe", "42", "Ul. Prosta 1", "00-001", "Warszawa", true),
            [
                new DeliveryNoteLine("Chleb", "Bread", 2, 1.25m, 2.50m, "10")
            ]);

    private static byte[] CreateTemplateDocx()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(
                CreatePlainTextSdt(DeliveryNoteWordTemplateFiller.TagAccountName, "name"),
                CreatePlainTextSdt(DeliveryNoteWordTemplateFiller.TagAccountNumber, "account"),
                CreatePlainTextSdt(DeliveryNoteWordTemplateFiller.TagAddressLine1, "address"),
                CreatePlainTextSdt(DeliveryNoteWordTemplateFiller.TagCity, "city"),
                CreatePlainTextSdt(DeliveryNoteWordTemplateFiller.TagPostcode, "postcode"),
                CreatePlainTextSdt(DeliveryNoteWordTemplateFiller.TagRedBasket, "basket"),
                CreatePlainTextSdt(DeliveryNoteWordTemplateFiller.TagDeliveryDate, "date"),
                CreatePlainTextSdt(DeliveryNoteWordTemplateFiller.TagTotal, "total"),
                CreateRepeatingSection()));
            mainPart.Document.Save();
        }

        return stream.ToArray();
    }

    private static SdtBlock CreatePlainTextSdt(string tag, string placeholder)
    {
        return new SdtBlock(
            new SdtProperties(new Tag { Val = tag }),
            new SdtContentBlock(new Paragraph(new Run(new Text(placeholder)))));
    }

    private static SdtBlock CreateRepeatingSection()
    {
        var item = new SdtBlock(
            new SdtProperties(new Tag { Val = "RepeatingSectionItem" }),
            new SdtContentBlock(
                new Paragraph(
                    CreateInlineSdt("OrderItemName_PL", "pl"),
                    CreateInlineSdt("OrderItemName_EN", "en"),
                    CreateInlineSdt("Quantity", "q"))));

        return new SdtBlock(
            new SdtProperties(new Tag { Val = DeliveryNoteWordTemplateFiller.TagOrderItems }),
            new SdtContentBlock(item));
    }

    private static SdtRun CreateInlineSdt(string name, string placeholder)
    {
        return new SdtRun(
            new SdtProperties(new Tag { Val = name }),
            new SdtContentRun(new Run(new Text(placeholder))));
    }
}
