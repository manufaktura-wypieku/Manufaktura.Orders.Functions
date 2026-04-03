using System.Net.Http.Headers;
using Azure.Core;
using Azure.Identity;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Manufaktura.Orders.Functions.Services;

public class DocumentMergeService : IDocumentMergeService
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;

    public DocumentMergeService(HttpClient httpClient, DefaultAzureCredential credential)
    {
        _httpClient = httpClient;
        _credential = credential;
    }

    public async Task<byte[]> MergeDocumentsAsync(string[] documentUrls, CancellationToken cancellationToken = default)
    {
        if (documentUrls is null)
            throw new ArgumentNullException(nameof(documentUrls));
        if (documentUrls.Length == 0)
            throw new ArgumentException("At least one document URL is required.", nameof(documentUrls));

        var pdfStreams = new List<MemoryStream>();
        try
        {
            foreach (var url in documentUrls)
            {
                var pdfStream = await DownloadAsPdfAsync(url, cancellationToken);
                pdfStreams.Add(pdfStream);
            }

            return MergePdfs(pdfStreams);
        }
        finally
        {
            foreach (var stream in pdfStreams)
                stream.Dispose();
        }
    }

    private async Task<MemoryStream> DownloadAsPdfAsync(string sharePointUrl, CancellationToken cancellationToken)
    {
        var (siteId, itemPath) = ParseSharePointUrl(sharePointUrl);

        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(GraphScopes), cancellationToken);

        // Graph REST API:
        // - path-based site identifier: /sites/{hostname}:/sites/{site-path}:/drive/root:/{path}:/content?format=pdf
        // - resolved Graph site id:   /sites/{site-id}/drive/root:/{path}:/content?format=pdf
        // Normalize path-based site identifiers to include the trailing ':' before /drive.
        var normalizedSiteId = siteId.Contains(',', StringComparison.Ordinal)
            ? siteId
            : siteId.EndsWith(":", StringComparison.Ordinal) ? siteId : $"{siteId}:";

        // Encode each path segment individually so '/' delimiters are preserved.
        var encodedPath = string.Join("/", itemPath.Split('/').Select(Uri.EscapeDataString));
        var graphUrl = $"https://graph.microsoft.com/v1.0/sites/{normalizedSiteId}/drive/root:/{encodedPath}:/content?format=pdf";

        using var request = new HttpRequestMessage(HttpMethod.Get, graphUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

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

        // Path relative to drive root (Shared Documents IS the drive root)
        var itemPath = string.Join("/", pathSegments.Skip(docsIndex + 1));
        itemPath = Uri.UnescapeDataString(itemPath);

        return (siteId, itemPath);
    }

    private static byte[] MergePdfs(List<MemoryStream> pdfStreams)
    {
        using var outputDocument = new PdfDocument();

        foreach (var stream in pdfStreams)
        {
            using var inputDocument = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            for (var i = 0; i < inputDocument.PageCount; i++)
            {
                outputDocument.AddPage(inputDocument.Pages[i]);
            }
        }

        using var outputStream = new MemoryStream();
        outputDocument.Save(outputStream);
        return outputStream.ToArray();
    }
}
