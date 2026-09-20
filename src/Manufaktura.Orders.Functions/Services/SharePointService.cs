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

        var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScopes), cancellationToken);

        // Path-based site identifiers (hostname:/sites/name) cannot be chained with
        // /drive/root:/{path}: addressing — Graph rejects nested colon-paths.
        // Resolve the site to its opaque ID (hostname,collectionId,webId) first.
        var resolvedSiteId = await ResolveSiteIdAsync(siteId, token.Token, cancellationToken);

        var encodedPath = string.Join("/", itemPath.Split('/').Select(Uri.EscapeDataString));
        var siteGraphUrl = $"https://graph.microsoft.com/v1.0/sites/{resolvedSiteId}";
        var graphBaseUrl = $"{siteGraphUrl}/drive/root:/{encodedPath}:";

        // Ensure the target folder hierarchy exists before uploading;
        // Graph does not create intermediate folders automatically for upload sessions.
        await EnsureFolderAsync(siteGraphUrl, $"DeliveryPacks/{safeRouteName}", token.Token, cancellationToken);

        if (pdfContent.Length <= SimpleUploadThresholdBytes)
        {
            await SimpleUploadAsync(graphBaseUrl + "/content", pdfContent, "application/pdf", token.Token, cancellationToken);
        }
        else
        {
            await UploadSessionAsync(graphBaseUrl + "/createUploadSession", pdfContent, "application/pdf", token.Token, cancellationToken);
        }

        // Return the SharePoint URL of the uploaded file.
        var siteBase = new Uri(_sharePointSiteUrl).GetLeftPart(UriPartial.Authority);
        var sitePath = new Uri(_sharePointSiteUrl).AbsolutePath.TrimEnd('/');
        return $"{siteBase}{sitePath}/Shared%20Documents/{Uri.EscapeDataString("DeliveryPacks")}/{Uri.EscapeDataString(safeRouteName)}/{Uri.EscapeDataString(fileName)}";
    }

    public async Task<byte[]> DownloadFileByPathAsync(string itemPath, CancellationToken cancellationToken = default)
    {
        var (resolvedSiteId, bearerToken, encodedPath) = await ResolvePathAsync(itemPath, cancellationToken);
        var contentUrl = $"https://graph.microsoft.com/v1.0/sites/{resolvedSiteId}/drive/root:/{encodedPath}:/content";

        using var request = new HttpRequestMessage(HttpMethod.Get, contentUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<string> UploadFileAsync(string folderPath, string fileName, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        var safeFolder = string.Join("/", folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizePathSegment));
        var safeFileName = SanitizePathSegment(fileName);
        var itemPath = $"{safeFolder}/{safeFileName}";

        var (resolvedSiteId, bearerToken, encodedPath) = await ResolvePathAsync(itemPath, cancellationToken);
        var siteGraphUrl = $"https://graph.microsoft.com/v1.0/sites/{resolvedSiteId}";
        var graphBaseUrl = $"{siteGraphUrl}/drive/root:/{encodedPath}:";

        await EnsureFolderAsync(siteGraphUrl, safeFolder, bearerToken, cancellationToken);

        if (content.Length <= SimpleUploadThresholdBytes)
        {
            await SimpleUploadAsync(graphBaseUrl + "/content", content, contentType, bearerToken, cancellationToken);
        }
        else
        {
            await UploadSessionAsync(graphBaseUrl + "/createUploadSession", content, contentType, bearerToken, cancellationToken);
        }

        return BuildSharedDocumentsUrl(safeFolder, safeFileName);
    }

    public async Task<byte[]> DownloadAsPdfAsync(string itemPath, CancellationToken cancellationToken = default)
    {
        var (resolvedSiteId, bearerToken, encodedPath) = await ResolvePathAsync(itemPath, cancellationToken);
        var contentUrl = $"https://graph.microsoft.com/v1.0/sites/{resolvedSiteId}/drive/root:/{encodedPath}:/content?format=pdf";

        using var request = new HttpRequestMessage(HttpMethod.Get, contentUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task DeleteFileByPathAsync(string itemPath, CancellationToken cancellationToken = default)
    {
        var (resolvedSiteId, bearerToken, encodedPath) = await ResolvePathAsync(itemPath, cancellationToken);
        var deleteUrl = $"https://graph.microsoft.com/v1.0/sites/{resolvedSiteId}/drive/root:/{encodedPath}";

        using var request = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;

        response.EnsureSuccessStatusCode();
    }

    private async Task<(string ResolvedSiteId, string BearerToken, string EncodedPath)> ResolvePathAsync(string itemPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemPath);

        var normalized = itemPath.Replace('\\', '/').Trim('/');
        var (siteId, _) = DocumentMergeService.ParseSharePointUrl(_sharePointSiteUrl + "/Shared%20Documents/placeholder");
        var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScopes), cancellationToken);
        var resolvedSiteId = await ResolveSiteIdAsync(siteId, token.Token, cancellationToken);
        var encodedPath = string.Join("/", normalized.Split('/').Select(Uri.EscapeDataString));
        return (resolvedSiteId, token.Token, encodedPath);
    }

    private string BuildSharedDocumentsUrl(string folderPath, string fileName)
    {
        var siteBase = new Uri(_sharePointSiteUrl).GetLeftPart(UriPartial.Authority);
        var sitePath = new Uri(_sharePointSiteUrl).AbsolutePath.TrimEnd('/');
        var folderSegments = folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        var folderUrl = string.Join("/", folderSegments);
        return $"{siteBase}{sitePath}/Shared%20Documents/{folderUrl}/{Uri.EscapeDataString(fileName)}";
    }

    private async Task SimpleUploadAsync(string contentUrl, byte[] content, string contentType, string bearerToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, contentUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task UploadSessionAsync(string createSessionUrl, byte[] content, string contentType, string bearerToken, CancellationToken cancellationToken)
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
        const int chunkSize = UploadSessionChunkSize;
        var totalBytes = content.Length;
        var offset = 0;

        while (offset < totalBytes)
        {
            var length = Math.Min(chunkSize, totalBytes - offset);

            using var putRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
            putRequest.Content = new ByteArrayContent(content, offset, length);
            putRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            putRequest.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, totalBytes);

            using var putResponse = await _httpClient.SendAsync(putRequest, cancellationToken);

            if ((int)putResponse.StatusCode == 202)
            {
                offset = await GetNextExpectedOffsetAsync(putResponse, totalBytes, cancellationToken);
                continue;
            }

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
                ["@microsoft.graph.conflictBehavior"] = "fail"
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, parentEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            // 409 Conflict means the folder already exists — treat as success.
            if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
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

    private async Task<string> ResolveSiteIdAsync(string pathBasedSiteId, string bearerToken, CancellationToken cancellationToken)
    {
        var resolveUrl = $"https://graph.microsoft.com/v1.0/sites/{pathBasedSiteId}:?$select=id";

        using var request = new HttpRequestMessage(HttpMethod.Get, resolveUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Graph did not return a site id.");
    }
}

