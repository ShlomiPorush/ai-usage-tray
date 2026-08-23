using costats.Application.Settings;
using QRCoder;

namespace costats.Application.RemoteView;

/// <summary>A QR image containing the same read-only URL shown in Settings.</summary>
public sealed record RemoteViewQrCodeImage(string ShareLink, byte[] PngBytes);

/// <summary>Builds a phone-scannable QR without ever accepting the private write id.</summary>
public static class RemoteViewQrCode
{
    public static RemoteViewQrCodeImage? Create(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var shareLink = settings.RemoteViewShareLink;
        if (string.IsNullOrWhiteSpace(shareLink))
        {
            return null;
        }

        using var data = QRCodeGenerator.GenerateQrCode(shareLink, QRCodeGenerator.ECCLevel.Q);
        using var code = new PngByteQRCode(data);
        return new RemoteViewQrCodeImage(shareLink, code.GetGraphic(6));
    }
}
