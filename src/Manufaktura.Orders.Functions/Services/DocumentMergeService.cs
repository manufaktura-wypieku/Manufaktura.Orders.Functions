using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Manufaktura.Orders.Functions.Services;

public class DocumentMergeService : IDocumentMergeService
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly ILogger<DocumentMergeService> _logger;
    private string? _resolvedSiteId;

    public DocumentMergeService(HttpClient httpClient, DefaultAzureCredential credential, ILogger<DocumentMergeService> logger)
    {
        _httpClient = httpClient;
        _credential = credential;
        _logger = logger;
    }

    public async Task<byte[]> MergeDocumentsAsync(string[] documentUrls, CancellationToken cancellationToken = default)
    {
        if (documentUrls is null)
            throw new ArgumentNullException(nameof(documentUrls));
        if (documentUrls.Length == 0)
            throw new ArgumentException("At least one document URL is required.", nameof(documentUrls));

        using var outputDocument = new PdfDocument();

        foreach (var url in documentUrls)
        {
            await using var pdfStream = await DownloadAsPdfAsync(url, cancellationToken);
            MergePdfIntoDocument(outputDocument, pdfStream);
        }

        return SavePdfDocumentToBytes(outputDocument);
    }

    private static void MergePdfIntoDocument(PdfDocument outputDocument, Stream pdfStream)
    {
        pdfStream.Position = 0;

        using var inputDocument = PdfReader.Open(pdfStream, PdfDocumentOpenMode.Import);
        for (var pageIndex = 0; pageIndex < inputDocument.PageCount; pageIndex++)
        {
            outputDocument.AddPage(inputDocument.Pages[pageIndex]);
        }
    }

    private static byte[] SavePdfDocumentToBytes(PdfDocument outputDocument)
    {
        using var mergedStream = new MemoryStream();
        outputDocument.Save(mergedStream, false);
        return mergedStream.ToArray();
    }
    private async Task<MemoryStream> DownloadAsPdfAsync(string sharePointUrl, CancellationToken cancellationToken)
    {
        var (siteId, itemPath) = ParseSharePointUrl(sharePointUrl);

        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(GraphScopes), cancellationToken);

        // Path-based site identifiers (hostname:/sites/name) cannot be chained with
        // /drive/root:/{path}: addressing — Graph rejects nested colon-paths.
        // Resolve the site to its opaque ID (hostname,collectionId,webId) first.
        var resolvedSiteId = await ResolveSiteIdAsync(siteId, token.Token, cancellationToken);

        // Encode each path segment individually so '/' delimiters are preserved.
        var encodedPath = string.Join("/", itemPath.Split('/').Select(Uri.EscapeDataString));
        var graphUrl = $"https://graph.microsoft.com/v1.0/sites/{resolvedSiteId}/drive/root:/{encodedPath}:/content";

        // Only request PDF conversion if the source file isn't already a PDF.
        if (!itemPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            graphUrl += "?format=pdf";

        _logger.LogInformation("Downloading PDF from Graph: {GraphUrl}", graphUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, graphUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Graph download failed {StatusCode} for {GraphUrl}: {ErrorBody}",
                (int)response.StatusCode, graphUrl, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var memoryStream = new MemoryStream();
        await response.Content.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;
        return memoryStream;
    }

    private async Task<string> ResolveSiteIdAsync(string pathBasedSiteId, string bearerToken, CancellationToken cancellationToken)
    {
        if (_resolvedSiteId is not null)
            return _resolvedSiteId;

        var resolveUrl = $"https://graph.microsoft.com/v1.0/sites/{pathBasedSiteId}:?$select=id";

        _logger.LogInformation("Resolving site ID for '{PathBasedSiteId}'", pathBasedSiteId);

        using var request = new HttpRequestMessage(HttpMethod.Get, resolveUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Site resolution failed {StatusCode} for '{PathBasedSiteId}': {ErrorBody}",
                (int)response.StatusCode, pathBasedSiteId, errorBody);
            response.EnsureSuccessStatusCode();
        }

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        _resolvedSiteId = doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Graph did not return a site id.");

        _logger.LogInformation("Resolved site '{PathBasedSiteId}' to '{ResolvedSiteId}'", pathBasedSiteId, _resolvedSiteId);
        return _resolvedSiteId;
    }

    internal static (string siteId, string itemPath) ParseSharePointUrl(string url)
    {
        // Expected format: https://{tenant}.sharepoint.com/sites/{siteName}/Shared Documents/{path}
        var uri = new Uri(url);
        var host = uri.Host;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var sitesIndex = Array.FindIndex(segments, s => s.Equals("sites", StringComparison.OrdinalIgnoreCase));
        if (sitesIndex < 0 || sitesIndex + 1 >= segments.Length)
            throw new ArgumentException($"Cannot parse SharePoint site from URL: host={uri.Host}");

        var siteName = segments[sitesIndex + 1];
        var siteId = $"{host}:/sites/{siteName}";

        // Find the document path after "Shared Documents" or "Shared%20Documents"
        var pathSegments = segments.Skip(sitesIndex + 2).ToArray();
        var docsIndex = Array.FindIndex(pathSegments, s =>
            s.Equals("Shared Documents", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("Shared%20Documents", StringComparison.OrdinalIgnoreCase));

        if (docsIndex < 0)
            throw new ArgumentException($"Cannot find 'Shared Documents' in URL: host={uri.Host}, site={siteName}");

        var itemPathSegments = pathSegments.Skip(docsIndex + 1).ToArray();
        if (itemPathSegments.Length == 0)
            throw new ArgumentException($"Document path is missing after 'Shared Documents' in URL: host={uri.Host}, site={siteName}");

        // Path relative to drive root (Shared Documents IS the drive root)
        var itemPath = string.Join("/", itemPathSegments);
        itemPath = Uri.UnescapeDataString(itemPath);

        return (siteId, itemPath);
    }
}
