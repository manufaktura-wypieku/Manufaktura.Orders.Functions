using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace Manufaktura.Orders.Functions.Services;

public class SharePointService : ISharePointService
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];
    private const int SimpleUploadThresholdBytes = 4_000_000; // Conservative 4 MB threshold for Graph simple uploads
    private const int UploadSessionChunkSize = 320 * 1024 * 10; // 3,276,800 bytes — must be a multiple of 320 KiB per Graph requirements

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
        var normalizedSiteId = siteId.Contains(":/", StringComparison.Ordinal) && !siteId.EndsWith(":", StringComparison.Ordinal)
            ? siteId + ":"
            : siteId;
        var encodedPath = string.Join("/", itemPath.Split('/').Select(Uri.EscapeDataString));
        var graphBaseUrl = $"https://graph.microsoft.com/v1.0/sites/{normalizedSiteId}/drive/root:/{encodedPath}:";

        var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScopes), cancellationToken);

        if (pdfContent.Length <= SimpleUploadThresholdBytes)
        {
            await SimpleUploadAsync(graphBaseUrl + "/content", pdfContent, token.Token, cancellationToken);
        }
        else
        {
            await UploadSessionAsync(graphBaseUrl + "/createUploadSession", pdfContent, token.Token, cancellationToken);
        }

        // Return the SharePoint URL of the uploaded file.
        var siteBase = new Uri(_sharePointSiteUrl).GetLeftPart(UriPartial.Authority);
        var sitePath = new Uri(_sharePointSiteUrl).AbsolutePath;
        return $"{siteBase}{sitePath}/Shared%20Documents/{Uri.EscapeDataString("DeliveryPacks")}/{Uri.EscapeDataString(safeRouteName)}/{Uri.EscapeDataString(fileName)}";
    }

    private async Task SimpleUploadAsync(string contentUrl, byte[] pdfContent, string bearerToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, contentUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Content = new ByteArrayContent(pdfContent);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task UploadSessionAsync(string createSessionUrl, byte[] pdfContent, string bearerToken, CancellationToken cancellationToken)
    {
        // Step 1: Create the upload session.
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, createSessionUrl);
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        createRequest.Content = new StringContent(
            """{"item":{"@microsoft.graph.conflictBehavior":"replace"}}""",
            Encoding.UTF8,
            "application/json");

        string uploadUrl;
        using (var createResponse = await _httpClient.SendAsync(createRequest, cancellationToken))
        {
            createResponse.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(
                await createResponse.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            uploadUrl = doc.RootElement.GetProperty("uploadUrl").GetString()
                ?? throw new InvalidOperationException("Graph did not return an uploadUrl for the upload session.");
        }

        // Step 2: Upload the content in Graph-compliant chunks.
        // Each non-final chunk must be a multiple of 320 KiB as required by Microsoft Graph.
        const int chunkSize = UploadSessionChunkSize;
        var totalBytes = pdfContent.Length;
        var offset = 0;

        while (offset < totalBytes)
        {
            var length = Math.Min(chunkSize, totalBytes - offset);

            using var putRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
            putRequest.Content = new ByteArrayContent(pdfContent, offset, length);
            putRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            putRequest.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, totalBytes);

            using var putResponse = await _httpClient.SendAsync(putRequest, cancellationToken);
            // 200/201 = upload complete; 202 = chunk accepted, more to follow.
            if ((int)putResponse.StatusCode != 202)
                putResponse.EnsureSuccessStatusCode();

            offset += length;
        }
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

