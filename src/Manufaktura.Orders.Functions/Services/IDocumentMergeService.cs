namespace Manufaktura.Orders.Functions.Services;

public interface IDocumentMergeService
{
    /// <summary>
    /// Downloads documents from the given SharePoint URLs and merges them into a single PDF.
    /// </summary>
    Task<byte[]> MergeDocumentsAsync(string[] documentUrls, CancellationToken cancellationToken = default);
}
