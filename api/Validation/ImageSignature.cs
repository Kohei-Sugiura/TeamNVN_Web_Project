namespace Weepa.Api.Validation;

// Content-Type はクライアント申告値のため、実バイトの署名 (マジックバイト) も検証する
public static class ImageSignature
{
    public static bool IsAllowedImage(IFormFile file)
    {
        Span<byte> head = stackalloc byte[12];
        using var stream = file.OpenReadStream();
        var total = 0;
        while (total < head.Length)
        {
            var n = stream.Read(head[total..]);
            if (n == 0) break;
            total += n;
        }

        if (total >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return true; // JPEG
        if (total >= 8 && head[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return true; // PNG
        if (total >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            return true; // WebP
        return false;
    }
}
