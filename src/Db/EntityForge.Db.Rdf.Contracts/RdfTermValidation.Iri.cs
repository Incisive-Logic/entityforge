using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EntityForge.Db.Rdf.Contracts;

internal static partial class RdfTermValidation
{
    // RFC 3987 section 2.2 generic IRI grammar. Identifiers need not be
    // dereferenceable URLs; do not normalize them or apply URL port limits.
    internal static bool IsAbsoluteIri(string? value)
    {
        if (String.IsNullOrEmpty(value) || !Char.IsAsciiLetter(value[0]))
        {
            return false;
        }
        ReadOnlySpan<char> text = value.AsSpan();
        int colon = text.IndexOf(':');
        if (colon < 1)
        {
            return false;
        }
        for (int i = 1; i < colon; i++)
        {
            if (!Char.IsAsciiLetterOrDigit(text[i]) && text[i] is not ('+' or '-' or '.'))
            {
                return false;
            }
        }
        text = text[(colon + 1)..];
        int fragment = text.IndexOf('#');
        if (fragment >= 0)
        {
            if (!IriComponent(text[(fragment + 1)..], ":@/?", false))
            {
                return false;
            }
            text = text[..fragment];
        }
        int query = text.IndexOf('?');
        if (query >= 0)
        {
            if (!IriComponent(text[(query + 1)..], ":@/?", true))
            {
                return false;
            }
            text = text[..query];
        }
        if (text.StartsWith("//"))
        {
            text = text[2..];
            int slash = text.IndexOf('/');
            if (!IriAuthority(slash < 0 ? text : text[..slash]))
            {
                return false;
            }
            text = slash < 0 ? [] : text[slash..];
        }
        return IriComponent(text, ":@/", false);
    }

    private static bool IriAuthority(ReadOnlySpan<char> text)
    {
        int at = text.IndexOf('@');
        if (at >= 0)
        {
            if (!IriComponent(text[..at], ":", false))
            {
                return false;
            }
            text = text[(at + 1)..];
        }
        if (text.StartsWith("["))
        {
            int close = text.IndexOf(']');
            if (close < 0 || !IriIpLiteral(text[1..close]))
            {
                return false;
            }
            text = text[(close + 1)..];
            if (text.IsEmpty)
            {
                return true;
            }
            if (text[0] != ':')
            {
                return false;
            }
        }
        else
        {
            int colon = text.IndexOf(':');
            if (!IriComponent(colon < 0 ? text : text[..colon], "", false))
            {
                return false;
            }
            if (colon < 0)
            {
                return true;
            }
            text = text[colon..];
        }
        foreach (char c in text[1..])
        {
            if (!Char.IsAsciiDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IriIpLiteral(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }
        if (text[0] is 'v' or 'V')
        {
            int dot = text.IndexOf('.');
            if (dot < 2 || dot == text.Length - 1)
            {
                return false;
            }
            foreach (char c in text[1..dot])
            {
                if (!Char.IsAsciiHexDigit(c))
                {
                    return false;
                }
            }
            foreach (char c in text[(dot + 1)..])
            {
                if (!IriAsciiUnreserved(c) && !IriSubDelimiter(c) && c != ':')
                {
                    return false;
                }
            }
            return true;
        }
        foreach (char c in text)
        {
            if (!Char.IsAsciiHexDigit(c) && c is not (':' or '.'))
            {
                return false;
            }
        }
        if (text.Contains('.'))
        {
            // IPv6's embedded IPv4 tail uses RFC 3986 dec-octet, not the
            // platform's more permissive legacy address spellings.
            ReadOnlySpan<char> tail = text[(text.LastIndexOf(':') + 1)..];
            int count = 0;
            while (!tail.IsEmpty)
            {
                int dot = tail.IndexOf('.');
                ReadOnlySpan<char> part = dot < 0 ? tail : tail[..dot];
                if (part.Length is < 1 or > 3 || part.Length > 1 && part[0] == '0')
                {
                    return false;
                }
                int number = 0;
                foreach (char c in part)
                {
                    if (!Char.IsAsciiDigit(c))
                    {
                        return false;
                    }
                    number = number * 10 + c - '0';
                }
                if (number > 255)
                {
                    return false;
                }
                count++;
                if (dot < 0)
                {
                    break;
                }
                tail = tail[(dot + 1)..];
                if (tail.IsEmpty)
                {
                    return false;
                }
            }
            if (count != 4)
            {
                return false;
            }
        }
        return IPAddress.TryParse(text, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetworkV6;
    }

    private static bool IriAsciiUnreserved(char c) => Char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';

    private static bool IriSubDelimiter(char c) => c is '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=';

    private static bool IriComponent(ReadOnlySpan<char> text, string additional, bool privateAllowed)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 128)
            {
                if (IriAsciiUnreserved(c) || IriSubDelimiter(c) || additional.Contains(c))
                {
                    continue;
                }
                if (c != '%' || i + 2 >= text.Length || !Char.IsAsciiHexDigit(text[i + 1]) || !Char.IsAsciiHexDigit(text[i + 2]))
                {
                    return false;
                }
                i += 2;
                continue;
            }
            int scalar = c;
            if (Char.IsSurrogate(c))
            {
                if (i + 1 >= text.Length || !Rune.TryCreate(c, text[i + 1], out Rune rune))
                {
                    return false;
                }
                scalar = rune.Value;
                i++;
            }
            bool unreserved = scalar is >= 0xA0 and <= 0xD7FF or >= 0xF900 and <= 0xFDCF or >= 0xFDF0 and <= 0xFFEF ||
                scalar is >= 0x10000 and <= 0xDFFFD && (scalar & 0xffff) <= 0xfffd || scalar is >= 0xE1000 and <= 0xEFFFD;
            bool privateQuery = privateAllowed && scalar is (>= 0xE000 and <= 0xF8FF or >= 0xF0000 and <= 0xFFFFD or >= 0x100000 and <= 0x10FFFD);
            if (!unreserved && !privateQuery)
            {
                return false;
            }
        }
        return true;
    }
}
