#:property ManagePackageVersionsCentrally=false
#:package Svg.Skia@5.2.3
// Builds the Trellis app icon from its SVG sources: renders each size with Skia and packs them into one .ico
// (PNG-compressed entries, supported by Windows Vista and later), plus a 256 px PNG for docs.
// 16–32 px use the simplified trellis-small.svg; 48 px and up use trellis.svg.
//   dotnet run scripts/make-icon.cs -- src/GuitarPractice.App/Assets/Icon [preview.png]
// With a second argument it also writes a preview sheet: every size at 1:1, and 16/32 px enlarged ×8, on light and dark.
using SkiaSharp;
using Svg.Skia;

var dir = args.Length > 0 ? args[0] : "src/GuitarPractice.App/Assets/Icon";
int[] sizes = [16, 20, 24, 32, 48, 64, 128, 256];

var entries = sizes.Select(size => (Size: size, Png: Render(Path.Combine(dir, size <= 32 ? "trellis-small.svg" : "trellis.svg"), size))).ToList();

using (var ico = new BinaryWriter(File.Create(Path.Combine(dir, "trellis.ico"))))
{
    ico.Write((short)0);               // reserved
    ico.Write((short)1);               // type: icon
    ico.Write((short)entries.Count);
    var offset = 6 + 16 * entries.Count;
    foreach (var (size, png) in entries)
    {
        ico.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
        ico.Write((byte)(size >= 256 ? 0 : size));
        ico.Write((byte)0);            // palette colours
        ico.Write((byte)0);            // reserved
        ico.Write((short)1);           // colour planes
        ico.Write((short)32);          // bits per pixel
        ico.Write(png.Length);
        ico.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in entries) ico.Write(png);
}
File.WriteAllBytes(Path.Combine(dir, "trellis-256.png"), entries.Single(e => e.Size == 256).Png);
Console.WriteLine($"Wrote {Path.Combine(dir, "trellis.ico")} ({string.Join(", ", sizes)} px) and trellis-256.png");

if (args.Length > 1) WritePreview(args[1], entries);

static void WritePreview(string path, List<(int Size, byte[] Png)> entries)
{
    const int Pad = 16;
    var width = entries.Sum(e => e.Size + Pad) + Pad + 2 * (32 * 8 + Pad);
    var height = 2 * (256 + 2 * Pad);
    using var surface = SKSurface.Create(new SKImageInfo(width, height));
    var canvas = surface.Canvas;
    foreach (var (row, bg) in new[] { (0, SKColors.White), (1, new SKColor(0x20, 0x20, 0x20)) })
    {
        var top = row * (256 + 2 * Pad);
        using var paint = new SKPaint { Color = bg };
        canvas.DrawRect(0, top, width, 256 + 2 * Pad, paint);
        var x = Pad;
        foreach (var (size, png) in entries)
        {
            using var img = SKImage.FromEncodedData(png);
            canvas.DrawImage(img, x, top + Pad + 256 - size);
            x += size + Pad;
        }
        foreach (var size in new[] { 16, 32 })
        {
            using var img = SKImage.FromEncodedData(entries.Single(e => e.Size == size).Png);
            canvas.DrawImage(img, SKRect.Create(x, top + Pad, size * 8, size * 8), new SKSamplingOptions(SKFilterMode.Nearest), null);
            x += 32 * 8 + Pad;
        }
    }
    using var snapshot = surface.Snapshot();
    using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, data.ToArray());
}

static byte[] Render(string svgPath, int size)
{
    using var svg = new SKSvg();
    var picture = svg.Load(svgPath) ?? throw new InvalidOperationException($"Couldn't load {svgPath}");
    var scale = size / picture.CullRect.Width;
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(scale);
    canvas.DrawPicture(picture);
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}
