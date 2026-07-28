using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ListaryOpen.App.Previewing;

internal sealed class PimPreviewProvider : IFilePreviewProvider
{
    private const int MaximumItems = 100;
    private const int MaximumOutputCharacters = 120_000;

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Pim);

    public async Task<PreviewContent?> LoadAsync(PreviewContext context, CancellationToken cancellationToken)
    {
        var source = await PreviewTextReader.ReadAsync(context.FullPath, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }
        if (context.Extension.Equals(".contact", StringComparison.OrdinalIgnoreCase))
        {
            return LoadWindowsContact(source);
        }
        if (context.Extension.Equals(".ldif", StringComparison.OrdinalIgnoreCase))
        {
            return LoadLdif(source, cancellationToken);
        }

        var lines = Unfold(source);
        var isCalendar =
            context.Extension.Equals(".ics", StringComparison.OrdinalIgnoreCase) ||
            context.Extension.Equals(".ifb", StringComparison.OrdinalIgnoreCase) ||
            context.Extension.Equals(".vcs", StringComparison.OrdinalIgnoreCase);
        var begin = isCalendar ? "BEGIN:VEVENT" : "BEGIN:VCARD";
        var end = isCalendar ? "END:VEVENT" : "END:VCARD";
        var fields = isCalendar
            ? new HashSet<string>(["SUMMARY", "DTSTART", "DTEND", "LOCATION", "DESCRIPTION", "ORGANIZER", "ATTENDEE"],
                StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(["FN", "N", "ORG", "TITLE", "TEL", "EMAIL", "ADR", "URL", "BDAY", "NOTE"],
                StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder(isCalendar ? "Calendar" : "Contact cards").AppendLine().AppendLine();
        var itemCount = 0;
        var inItem = false;
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Equals(begin, StringComparison.OrdinalIgnoreCase))
            {
                if (++itemCount > MaximumItems)
                {
                    break;
                }
                inItem = true;
                builder.Append("— ").Append(isCalendar ? "Event " : "Contact ").Append(itemCount).AppendLine(" —");
                continue;
            }
            if (line.Equals(end, StringComparison.OrdinalIgnoreCase))
            {
                inItem = false;
                builder.AppendLine();
                continue;
            }
            if (!inItem)
            {
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var property = line[..colon];
            var semicolon = property.IndexOf(';');
            var name = semicolon < 0 ? property : property[..semicolon];
            if (!fields.Contains(name))
            {
                continue;
            }
            var value = DecodeValue(line[(colon + 1)..]);
            var remaining = MaximumOutputCharacters - builder.Length;
            if (remaining <= 0)
            {
                break;
            }
            builder.Append(name).Append(": ");
            builder.Append(value, 0, Math.Min(value.Length, Math.Max(0, MaximumOutputCharacters - builder.Length)));
            builder.AppendLine();
        }

        if (itemCount > MaximumItems || builder.Length >= MaximumOutputCharacters)
        {
            builder.AppendLine("… preview truncated");
        }
        if (itemCount == 0)
        {
            return null;
        }
        return PreviewContent.ForText(isCalendar ? "Calendar events" : "Contact cards", builder.ToString());
    }

    private static PreviewContent? LoadWindowsContact(string source)
    {
        using var input = new StringReader(source);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = PreviewTextReader.MaximumBytes
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var fields = new HashSet<string>(
            ["FormattedName", "GivenName", "FamilyName", "Company", "JobTitle", "EmailAddress",
             "Number", "Street", "Locality", "Region", "PostalCode", "Country"],
            StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder("Windows Contact").AppendLine().AppendLine();
        var count = 0;
        foreach (var element in document.Descendants().Where(element => fields.Contains(element.Name.LocalName)))
        {
            var value = element.Value.Trim();
            if (value.Length == 0)
            {
                continue;
            }
            builder.Append(element.Name.LocalName).Append(": ")
                .AppendLine(value.Length <= 4096 ? value : value[..4096] + "…");
            if (++count >= MaximumItems || builder.Length >= MaximumOutputCharacters)
            {
                builder.AppendLine("… preview truncated");
                break;
            }
        }
        return count == 0 ? null : PreviewContent.ForText("Windows Contact", builder.ToString());
    }

    private static PreviewContent? LoadLdif(string source, CancellationToken cancellationToken)
    {
        var fields = new HashSet<string>(
            ["dn", "cn", "sn", "givenName", "mail", "telephoneNumber", "mobile", "o", "ou",
             "title", "street", "l", "st", "postalCode"],
            StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder("LDAP directory records").AppendLine().AppendLine();
        var itemCount = 0;
        var inItem = false;
        foreach (var line in Unfold(source).Append(string.Empty))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (inItem)
                {
                    builder.AppendLine();
                    inItem = false;
                }
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var name = line[..colon];
            if (!fields.Contains(name) || line.AsSpan(colon).StartsWith(":<"))
            {
                continue;
            }
            if (!inItem)
            {
                if (++itemCount > MaximumItems)
                {
                    break;
                }
                builder.Append("— Record ").Append(itemCount).AppendLine(" —");
                inItem = true;
            }
            var value = line.AsSpan(colon + 1);
            string decoded;
            if (!value.IsEmpty && value[0] == ':')
            {
                var encoded = value[1..].Trim();
                try
                {
                    var bytes = Convert.FromBase64String(encoded.ToString());
                    decoded = bytes.Length <= 4096
                        ? Encoding.UTF8.GetString(bytes)
                        : Encoding.UTF8.GetString(bytes.AsSpan(0, 4096)) + "…";
                }
                catch (FormatException)
                {
                    continue;
                }
            }
            else
            {
                decoded = value.TrimStart().ToString();
            }
            builder.Append(name).Append(": ")
                .AppendLine(decoded.Length <= 4096 ? decoded : decoded[..4096] + "…");
            if (builder.Length >= MaximumOutputCharacters)
            {
                builder.AppendLine("… preview truncated");
                break;
            }
        }
        return itemCount == 0 ? null : PreviewContent.ForText("LDAP contacts", builder.ToString());
    }

    private static IEnumerable<string> Unfold(string source)
    {
        string? current = null;
        foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && current is not null)
            {
                current += line[1..];
            }
            else
            {
                if (current is not null)
                {
                    yield return current;
                }
                current = line.TrimEnd('\r');
            }
        }
        if (current is not null)
        {
            yield return current;
        }
    }

    private static string DecodeValue(string value) =>
        value.Replace("\\n", Environment.NewLine, StringComparison.OrdinalIgnoreCase)
            .Replace("\\,", ",", StringComparison.Ordinal)
            .Replace("\\;", ";", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
}
