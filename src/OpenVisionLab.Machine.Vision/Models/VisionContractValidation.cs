namespace OpenVisionLab.Machine.Vision.Models;

internal static class VisionContractValidation
{
    public static string RequiredIdentifier(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty identifier is required.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Identifiers cannot start or end with whitespace.", parameterName);
        }

        return value;
    }

    public static string RequiredText(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The value cannot start or end with whitespace.", parameterName);
        }

        return value;
    }

    public static string RelativePath(string? value, string parameterName)
    {
        var path = RequiredText(value, parameterName).Replace('\\', '/');
        if (path.StartsWith("/", StringComparison.Ordinal) ||
            (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'))
        {
            throw new ArgumentException("The path must be relative.", parameterName);
        }

        var segments = path.Split('/');
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new ArgumentException("The relative path cannot contain empty, current-directory, or parent-directory segments.", parameterName);
        }

        return string.Join('/', segments);
    }
}
