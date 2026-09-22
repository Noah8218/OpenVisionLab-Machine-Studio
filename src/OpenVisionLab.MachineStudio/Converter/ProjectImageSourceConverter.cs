using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenVisionLab.MachineStudio.Converter;

/// <summary>
/// Loads project-owned image files for the MMI preview. WPF does not decode
/// the Mono8 PGM fixtures used by the virtual-camera contract, so the small
/// PGM reader keeps that existing source format visible without adding an
/// imaging dependency to the domain or simulation projects.
/// </summary>
[ValueConversion(typeof(string), typeof(ImageSource))]
public sealed class ProjectImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value switch
        {
            string text => text,
            Uri uri when uri.IsFile => uri.LocalPath,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return DependencyProperty.UnsetValue;
        }

        try
        {
            return Path.GetExtension(path).Equals(".pgm", StringComparison.OrdinalIgnoreCase)
                ? LoadPgm(path)
                : LoadBitmap(path);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or OverflowException
            or ArgumentException
            or NotSupportedException)
        {
            return DependencyProperty.UnsetValue;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource LoadPgm(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var index = 0;
        var magic = ReadToken(bytes, ref index);
        if (!string.Equals(magic, "P2", StringComparison.Ordinal)
            && !string.Equals(magic, "P5", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Only P2 and P5 PGM images are supported.");
        }

        var width = ReadPositiveInt(ReadToken(bytes, ref index), "width");
        var height = ReadPositiveInt(ReadToken(bytes, ref index), "height");
        var maximum = ReadPositiveInt(ReadToken(bytes, ref index), "maximum gray value");
        if (maximum > 255)
        {
            throw new InvalidDataException("16-bit PGM images are not supported.");
        }

        var pixelCount = checked(width * height);
        var pixels = new byte[pixelCount];
        if (magic == "P5")
        {
            ConsumeBinarySeparator(bytes, ref index);
            if (bytes.Length - index < pixelCount)
            {
                throw new InvalidDataException("The PGM payload is truncated.");
            }

            for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
            {
                pixels[pixelIndex] = Scale(bytes[index + pixelIndex], maximum);
            }
        }
        else
        {
            for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
            {
                pixels[pixelIndex] = Scale(
                    ReadPositiveInt(ReadToken(bytes, ref index), "pixel"),
                    maximum);
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Gray8,
            null,
            pixels,
            width);
        bitmap.Freeze();
        return bitmap;
    }

    private static int ReadPositiveInt(string token, string field)
    {
        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            throw new InvalidDataException($"Invalid PGM {field}.");
        }

        return value;
    }

    private static byte Scale(int value, int maximum)
    {
        if (value < 0 || value > maximum)
        {
            throw new InvalidDataException("A PGM pixel is outside the declared range.");
        }

        return (byte)((value * 255L + maximum / 2) / maximum);
    }

    private static string ReadToken(byte[] bytes, ref int index)
    {
        SkipWhitespaceAndComments(bytes, ref index);
        var start = index;
        while (index < bytes.Length && !IsWhitespace(bytes[index]) && bytes[index] != (byte)'#')
        {
            index++;
        }

        if (start == index)
        {
            throw new InvalidDataException("The PGM header is incomplete.");
        }

        return System.Text.Encoding.ASCII.GetString(bytes, start, index - start);
    }

    private static void SkipWhitespaceAndComments(byte[] bytes, ref int index)
    {
        while (index < bytes.Length)
        {
            if (IsWhitespace(bytes[index]))
            {
                index++;
                continue;
            }

            if (bytes[index] != (byte)'#')
            {
                return;
            }

            while (index < bytes.Length && bytes[index] is not (byte)'\r' and not (byte)'\n')
            {
                index++;
            }
        }
    }

    private static void ConsumeBinarySeparator(byte[] bytes, ref int index)
    {
        if (index >= bytes.Length || !IsWhitespace(bytes[index]))
        {
            throw new InvalidDataException("The PGM binary payload has no separator.");
        }

        var separator = bytes[index++];
        if (separator == (byte)'\r' && index < bytes.Length && bytes[index] == (byte)'\n')
        {
            index++;
        }
    }

    private static bool IsWhitespace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
