using System.Net.Http.Headers;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace Manufaktura.Orders.Functions.Services;

public class SharePointService : ISharePointService
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly string _sharePointSiteUrl;

    public SharePointService(HttpClient httpClient, DefaultAzureCredential credential, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _credential = credential;
        _sharePointSiteUrl = configuration["SharePointSiteUrl"]
            ?? throw new InvalidOperationException("SharePointSiteUrl configuration is required.");
    }

    public async Task<string> UploadDeliveryPackAsync(string routeName, DateTimeOffset deliveryDate, byte[] pdfContent, CancellationToken cancellationToken = default)
    {
        var safeRouteName = SanitizePathSegment(routeName);
        var fileName = $"{deliveryDate:yyyy-MM-dd}-delivery-pack.pdf";
        var itemPath = $"DeliveryPacks/{safeRouteName}/{fileName}";

        var (siteId, _) = DocumentMergeService.ParseSharePointUrl(_sharePointSiteUrl + "/Shared%20Documents/placeholder");
        var encodedPath = string.Join("/", itemPath.Split('/').Select(Uri.EscapeDataString));
        var graphUrl = $"https://graph.microsoft.com/v1.0/sites/{siteId}/drive/root:/{encodedPath}:/content";

        var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScopes), cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Put, graphUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = new ByteArrayContent(pdfContent);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Return the SharePoint URL of the uploaded file.
        var siteBase = new Uri(_sharePointSiteUrl).GetLeftPart(UriPartial.Authority);
        var sitePath = new Uri(_sharePointSiteUrl).AbsolutePath;
        return $"{siteBase}{sitePath}/Shared%20Documents/{Uri.EscapeDataString("DeliveryPacks")}/{Uri.EscapeDataString(safeRouteName)}/{Uri.EscapeDataString(fileName)}";
    }

    private static string SanitizePathSegment(string name)
    {
        // Remove characters that are invalid in SharePoint folder names.
        var invalid = new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|', '#', '%' };
        foreach (var c in invalid)
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
