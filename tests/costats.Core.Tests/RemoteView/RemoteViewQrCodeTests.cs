using costats.Application.RemoteView;
using costats.Application.Settings;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using ZXing.ImageSharp;

namespace costats.Core.Tests.RemoteView;

public sealed class RemoteViewQrCodeTests
{
    [Fact]
    public void Create_UsesTheReadOnlyShareLinkAndProducesPng()
    {
        const string writeId = "0123456789abcdef0123456789abcdef";
        var settings = new AppSettings
        {
            RemoteViewEnabled = true,
            RemoteViewId = writeId,
            DefaultRemoteViewPageUrl = "https://view.example.com"
        };

        var qr = RemoteViewQrCode.Create(settings);

        Assert.NotNull(qr);
        Assert.Equal(settings.RemoteViewShareLink, qr.ShareLink);
        Assert.DoesNotContain(writeId, qr.ShareLink);
        Assert.True(qr.PngBytes.Length > 100);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, qr.PngBytes[..8]);

        using var image = Image.Load<Rgba32>(qr.PngBytes);
        var decoded = new BarcodeReader<Rgba32>().Decode(image);
        Assert.NotNull(decoded);
        Assert.Equal(qr.ShareLink, decoded.Text);
    }

    [Fact]
    public void Create_ReturnsNullWhenRemoteViewHasNoShareLink()
    {
        Assert.Null(RemoteViewQrCode.Create(new AppSettings()));
    }
}
