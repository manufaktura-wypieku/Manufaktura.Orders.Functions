using System.Net.Http.Headers;
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

        // Graph REST API path-based site identifier format:
        // /sites/{hostname}:/sites/{site-path}/drive/root:/{path}:/content?format=pdf
        // The site identifier must NOT have a trailing ':' when followed by /drive/root:/{path};
        // a trailing colon causes Graph to reject 'root:' as an unknown segment.
        // Encode each path segment individually so '/' delimiters are preserved.
        var encodedPath = string.Join("/", itemPath.Split('/').Select(Uri.EscapeDataString));
        var graphUrl = $"https://graph.microsoft.com/v1.0/sites/{siteId}/drive/root:/{encodedPath}:/content?format=pdf";

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
