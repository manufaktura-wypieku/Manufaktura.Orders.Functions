using Manufaktura.Orders.Functions.Services;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class ParseSharePointUrlTests
{
    [Fact]
    public void ParsesStandardSharePointUrl()
    {
        var url = "https://manufakturawypieku.sharepoint.com/sites/Manufaktura-DEV/Shared%20Documents/DeliveryNotes/DN-ORD-001.docx";

        var (siteId, itemPath) = DocumentMergeService.ParseSharePointUrl(url);

        Assert.Equal("manufakturawypieku.sharepoint.com:/sites/Manufaktura-DEV", siteId);
        Assert.Equal("DeliveryNotes/DN-ORD-001.docx", itemPath);
    }

    [Fact]
    public void ParsesUrlWithNestedPath()
    {
        var url = "https://manufakturawypieku.sharepoint.com/sites/Manufaktura-DEV/Shared%20Documents/2026/03/DN-ORD-042.docx";

        var (siteId, itemPath) = DocumentMergeService.ParseSharePointUrl(url);

        Assert.Equal("manufakturawypieku.sharepoint.com:/sites/Manufaktura-DEV", siteId);
        Assert.Equal("2026/03/DN-ORD-042.docx", itemPath);
    }

    [Fact]
    public void ParsesUrlWithSpacesInDocumentName()
    {
        var url = "https://manufakturawypieku.sharepoint.com/sites/Manufaktura-DEV/Shared%20Documents/Delivery%20Note%20March.docx";

        var (siteId, itemPath) = DocumentMergeService.ParseSharePointUrl(url);

        Assert.Equal("manufakturawypieku.sharepoint.com:/sites/Manufaktura-DEV", siteId);
        Assert.Equal("Delivery Note March.docx", itemPath);
    }

    [Fact]
    public void ThrowsForUrlWithoutSites()
    {
        var url = "https://manufakturawypieku.sharepoint.com/teams/SomeTeam/Shared%20Documents/file.docx";

        Assert.Throws<ArgumentException>(() => DocumentMergeService.ParseSharePointUrl(url));
    }

    [Fact]
    public void ThrowsForUrlWithoutSharedDocuments()
    {
        var url = "https://manufakturawypieku.sharepoint.com/sites/Manufaktura-DEV/SitePages/Home.aspx";

        Assert.Throws<ArgumentException>(() => DocumentMergeService.ParseSharePointUrl(url));
    }
}
