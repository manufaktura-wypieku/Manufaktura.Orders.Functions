using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Manufaktura.Orders.Functions.Models;

namespace Manufaktura.Orders.Functions.Services;

/// <summary>
/// Fills Word content controls whose tags match
/// <c>templates/delivery_note_patterns_footer.docx</c> (also deployed to SharePoint
/// <c>Templates/delivery_note_patterns_footer.docx</c> per environment).
/// </summary>
public sealed class DeliveryNoteWordTemplateFiller : IDeliveryNoteWordTemplateFiller
{
    internal const string TagAddressLine1 = "AddressLine1";
    internal const string TagCity = "City";
    internal const string TagAccountNumber = "AccountNumber";
    internal const string TagRedBasket = "RedBasket";
    internal const string TagDeliveryDate = "DeliveryDate";
    internal const string TagAccountName = "AccountName";
    internal const string TagPostcode = "Postcode";
    internal const string TagTotal = "Total";
    internal const string TagOrderItems = "OrderItems";

    private static readonly CultureInfo NumberCulture = CultureInfo.GetCultureInfo("en-GB");

    public byte[] Fill(byte[] templateDocx, DeliveryNoteDocumentSource source)
    {
        ArgumentNullException.ThrowIfNull(templateDocx);
        ArgumentNullException.ThrowIfNull(source);

        using var input = new MemoryStream(templateDocx);
        using var output = new MemoryStream();
        input.CopyTo(output);
        output.Position = 0;

        using (var document = WordprocessingDocument.Open(output, true))
        {
            var body = document.MainDocumentPart?.Document.Body
                ?? throw new InvalidOperationException("Word template has no document body.");

            SetPlainText(body, TagAddressLine1, source.Customer.AddressLine1);
            SetPlainText(body, TagCity, source.Customer.City);
            SetPlainText(body, TagAccountNumber, source.Customer.AccountNumber);
            SetPlainText(body, TagRedBasket, source.Customer.RedBasket ? "C" : string.Empty);
            SetPlainText(body, TagDeliveryDate, FormatDeliveryDate(source.DeliveryDate));
            SetPlainText(body, TagAccountName, source.Customer.Name);
            SetPlainText(body, TagPostcode, source.Customer.PostalCode);
            SetPlainText(body, TagTotal, FormatNumber(source.OrderTotal));

            FillRepeatingLines(body, source.Lines);

            document.MainDocumentPart!.Document.Save();
        }

        return output.ToArray();
    }

    internal static string FormatDeliveryDate(DateTimeOffset? deliveryDate)
    {
        if (deliveryDate is null)
            return string.Empty;

        return deliveryDate.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    internal static string FormatNumber(decimal? value)
    {
        if (value is null)
            return string.Empty;

        return value.Value.ToString("N2", NumberCulture);
    }

    private static void FillRepeatingLines(Body body, IReadOnlyList<DeliveryNoteLine> lines)
    {
        var repeating = FindSdtByTag(body, TagOrderItems);
        if (repeating is null)
            return;

        var sectionItems = repeating.Descendants<SdtElement>()
            .Where(IsRepeatingSectionItem)
            .ToList();

        if (sectionItems.Count == 0)
        {
            FillTableRowsFallback(repeating, lines);
            return;
        }

        var prototype = sectionItems[0];
        foreach (var extra in sectionItems.Skip(1).ToList())
            extra.Remove();

        if (lines.Count == 0)
        {
            ClearLineFields(prototype);
            return;
        }

        SdtElement? previous = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var row = i == 0 ? prototype : (SdtElement)prototype.CloneNode(true);
            SetLineFields(row, lines[i]);
            if (i > 0)
            {
                if (previous is null)
                    prototype.Parent!.InsertAfter(row, prototype);
                else
                    previous.Parent!.InsertAfter(row, previous);
            }

            previous = row;
        }
    }

    private static void FillTableRowsFallback(SdtElement repeating, IReadOnlyList<DeliveryNoteLine> lines)
    {
        var table = repeating.Descendants<Table>().FirstOrDefault();
        if (table is null)
            return;

        var dataRows = table.Elements<TableRow>().Skip(1).ToList();
        if (dataRows.Count == 0)
            return;

        var prototype = dataRows[0];
        foreach (var extra in dataRows.Skip(1).ToList())
            extra.Remove();

        if (lines.Count == 0)
        {
            ClearLineFields(prototype);
            return;
        }

        TableRow? previous = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var row = i == 0 ? prototype : (TableRow)prototype.CloneNode(true);
            SetLineFields(row, lines[i]);
            if (i > 0)
            {
                if (previous is null)
                    prototype.Parent!.InsertAfter(row, prototype);
                else
                    previous.Parent!.InsertAfter(row, previous);
            }

            previous = row;
        }
    }

    private static bool IsRepeatingSectionItem(SdtElement element)
    {
        var tag = element.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value;
        if (string.Equals(tag, "RepeatingSectionItem", StringComparison.OrdinalIgnoreCase))
            return true;

        // Word's repeating-section item often has no tag — only a w15:repeatingSectionItem marker.
        if (element.SdtProperties?.ChildElements.Any(c => c.LocalName == "repeatingSectionItem") == true)
            return true;

        // Do not treat the outer OrderItems section as an item.
        if (string.Equals(tag, TagOrderItems, StringComparison.OrdinalIgnoreCase))
            return false;

        return FindSdtByTag(element, "OrderItemName_PL") is not null
            && element.SdtProperties?.ChildElements.Any(c => c.LocalName == "repeatingSection") != true;
    }

    private static void SetLineFields(OpenXmlElement container, DeliveryNoteLine line)
    {
        // Template lines currently expose name + quantity only (no unit price / value / product number controls).
        SetPlainTextByTag(container, "OrderItemName_PL", line.NamePl);
        SetPlainTextByTag(container, "OrderItemName_EN", line.NameEn);
        SetPlainTextByTag(container, "Quantity", line.Quantity?.ToString(CultureInfo.InvariantCulture));
    }

    private static void ClearLineFields(OpenXmlElement container)
    {
        SetLineFields(container, new DeliveryNoteLine(null, null, null, null, null, null));
    }

    private static void SetPlainText(OpenXmlElement root, string tag, string? value)
    {
        var sdt = FindSdtByTag(root, tag);
        if (sdt is null)
            return;

        SetSdtText(sdt, value ?? string.Empty);
    }

    private static void SetPlainTextByTag(OpenXmlElement root, string tag, string? value)
    {
        var sdt = FindSdtByTag(root, tag);
        if (sdt is null)
            return;

        SetSdtText(sdt, value ?? string.Empty);
    }

    private static SdtElement? FindSdtByTag(OpenXmlElement root, string tag) =>
        root.Descendants<SdtElement>().FirstOrDefault(e =>
            string.Equals(e.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value, tag, StringComparison.Ordinal));

    private static void SetSdtText(SdtElement sdt, string value)
    {
        var content = sdt.Descendants<SdtContentRun>().FirstOrDefault()
            ?? sdt.Descendants<SdtContentBlock>().FirstOrDefault() as OpenXmlElement
            ?? sdt.Descendants<SdtContentCell>().FirstOrDefault();

        if (content is SdtContentRun runContent)
        {
            runContent.RemoveAllChildren();
            runContent.AppendChild(new Run(new Text(value)));
            return;
        }

        if (content is SdtContentBlock blockContent)
        {
            blockContent.RemoveAllChildren();
            blockContent.AppendChild(new Paragraph(new Run(new Text(value))));
            return;
        }

        if (content is SdtContentCell cellContent)
        {
            var paragraph = cellContent.Descendants<Paragraph>().FirstOrDefault();
            if (paragraph is null)
            {
                cellContent.RemoveAllChildren();
                cellContent.AppendChild(new Paragraph(new Run(new Text(value))));
            }
            else
            {
                paragraph.RemoveAllChildren<Run>();
                paragraph.AppendChild(new Run(new Text(value)));
            }

            return;
        }

        var texts = sdt.Descendants<Text>().ToList();
        if (texts.Count == 0)
            return;

        texts[0].Text = value;
        for (var i = 1; i < texts.Count; i++)
            texts[i].Text = string.Empty;
    }
}
