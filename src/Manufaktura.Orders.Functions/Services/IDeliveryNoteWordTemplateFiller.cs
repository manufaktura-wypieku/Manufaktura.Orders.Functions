using Manufaktura.Orders.Functions.Models;

namespace Manufaktura.Orders.Functions.Services;

public interface IDeliveryNoteWordTemplateFiller
{
    /// <summary>
    /// Fills the Word template bytes with delivery-note fields and returns a new .docx payload.
    /// </summary>
    byte[] Fill(byte[] templateDocx, DeliveryNoteDocumentSource source);
}
