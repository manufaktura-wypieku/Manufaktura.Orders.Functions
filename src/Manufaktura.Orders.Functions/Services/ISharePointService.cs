namespace Manufaktura.Orders.Functions.Services;

public interface ISharePointService
{
    /// <summary>
    /// Uploads a PDF file to the /DeliveryPacks/{routeName}/ folder in the configured SharePoint site
    /// and returns the SharePoint URL of the uploaded file.
    /// </summary>
    Task<string> UploadDeliveryPackAsync(string routeName, DateTimeOffset deliveryDate, byte[] pdfContent, CancellationToken cancellationToken = default);
}
