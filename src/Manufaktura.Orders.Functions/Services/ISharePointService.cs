namespace Manufaktura.Orders.Functions.Services;

public interface ISharePointService
{
    /// <summary>
    /// Uploads a PDF file to the /DeliveryPacks/{routeName}/ folder in the configured SharePoint site
    /// and returns the SharePoint URL of the uploaded file.
    /// </summary>
    Task<string> UploadDeliveryPackAsync(string routeName, DateTimeOffset deliveryDate, byte[] pdfContent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a file from the site drive by a path relative to the document library root
    /// (e.g. <c>Templates/delivery_note_patterns_footer.docx</c>).
    /// </summary>
    Task<byte[]> DownloadFileByPathAsync(string itemPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a file under the given folder path (relative to the document library root) and returns its SharePoint URL.
    /// </summary>
    Task<string> UploadFileAsync(string folderPath, string fileName, byte[] content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads an existing drive item as PDF via Graph <c>?format=pdf</c>.
    /// </summary>
    Task<byte[]> DownloadAsPdfAsync(string itemPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file at the given drive-relative path. Missing files are ignored.
    /// </summary>
    Task DeleteFileByPathAsync(string itemPath, CancellationToken cancellationToken = default);
}
