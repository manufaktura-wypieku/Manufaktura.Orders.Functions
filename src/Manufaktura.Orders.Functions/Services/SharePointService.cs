using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Configuration;

namespace Manufaktura.Orders.Functions.Services;

public class SharePointService : ISharePointService
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];
    private const int SimpleUploadThresholdBytes = 4_000_000; // Conservative 4 MB threshold for Graph simple uploads
    private const int UploadSessionChunkSize = 320 * 1024 * 10; // 3,276,800 bytes — must be a multiple of 320 KiB per Graph requirements

    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;
    private readonly string _sharePointSiteUrl;

    public SharePointService(HttpClient httpClient, TokenCredential credential, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _credential = credential;
        _sharePointSiteUrl = configuration["SharePointSiteUrl"]
            ?? throw new InvalidOperationException("SharePointSiteUrl configuration is required.");

        // Validate when this service is constructed so a misconfigured URL fails
        // with a clear message rather than throwing ArgumentException later during upload processing.
        try
        {
            DocumentMergeService.ParseSharePointUrl(_sharePointSiteUrl + "/Shared%20Documents/placeholder");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"SharePointSiteUrl '{_sharePointSiteUrl}' is not a valid SharePoint site URL. " +
                "Expected format: https://tenant.sharepoint.com/sites/name", ex);
        }
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
        var siteGraphUrl = $"https://graph.microsoft.com/v1.0/sites/{normalizedSiteId}";
        var graphBaseUrl = $"{siteGraphUrl}/drive/root:/{encodedPath}:";

        var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScopes), cancellationToken);

        // Ensure the target folder hierarchy exists before uploading;
        // Graph does not create intermediate folders automatically for upload sessions.
        await EnsureFolderAsync(siteGraphUrl, $"DeliveryPacks/{safeRouteName}", token.Token, cancellationToken);

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

            if ((int)putResponse.StatusCode == 202)
            {
                // Parse nextExpectedRanges from the Graph response to get the confirmed next chunk offset.
                offset = await GetNextExpectedOffsetAsync(putResponse, totalBytes, cancellationToken);
                continue;
            }

            // 200/201 = upload complete.
            putResponse.EnsureSuccessStatusCode();
            break;
        }
    }

    private async Task EnsureFolderAsync(string siteGraphUrl, string folderPath, string bearerToken, CancellationToken cancellationToken)
    {
        // Walk each path segment and ensure the folder exists using conflictBehavior=replace (idempotent).
        var segments = folderPath.Split('/');
        var processedSegments = new List<string>();

        foreach (var segment in segments)
        {
            var encodedParentPath = string.Join("/", processedSegments.Select(Uri.EscapeDataString));
            var parentEndpoint = processedSegments.Count == 0
                ? $"{siteGraphUrl}/drive/root/children"
                : $"{siteGraphUrl}/drive/root:/{encodedParentPath}:/children";

            processedSegments.Add(segment);

            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = segment,
                ["folder"] = new { },
                ["@microsoft.graph.conflictBehavior"] = "replace"
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, parentEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    private static async Task<int> GetNextExpectedOffsetAsync(HttpResponseMessage response, int totalBytes, CancellationToken cancellationToken)
    {
        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);

        if (!doc.RootElement.TryGetProperty("nextExpectedRanges", out var nextExpectedRanges)
            || nextExpectedRanges.ValueKind != JsonValueKind.Array
            || nextExpectedRanges.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Graph returned 202 Accepted without nextExpectedRanges.");
        }

        var nextRange = nextExpectedRanges[0].GetString();
        if (string.IsNullOrWhiteSpace(nextRange))
            throw new InvalidOperationException("Graph returned an empty nextExpectedRanges entry.");

        var separatorIndex = nextRange.IndexOf('-');
        var startText = separatorIndex >= 0 ? nextRange[..separatorIndex] : nextRange;

        if (!int.TryParse(startText, out var nextOffset) || nextOffset < 0 || nextOffset > totalBytes)
            throw new InvalidOperationException($"Graph returned an invalid next expected range start: '{nextRange}'.");

        return nextOffset;
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

