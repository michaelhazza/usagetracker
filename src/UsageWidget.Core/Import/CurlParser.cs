using System.Text;

namespace UsageWidget.Core.Import;

/// <summary>The pieces extracted from a browser "Copy as cURL" blob.</summary>
public sealed record ParsedCurl(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? Body);

/// <summary>
/// Parses a "Copy as cURL" command (bash, cmd, or PowerShell flavor) into its method, URL, headers,
/// and body. This is what lets the user paste ONE thing instead of filling in a form.
/// </summary>
public static class CurlParser
{
    public static ParsedCurl Parse(string curl)
    {
        if (string.IsNullOrWhiteSpace(curl))
        {
            throw new FormatException("Nothing to parse — paste the 'Copy as cURL' text.");
        }

        var tokens = Tokenize(StripLineContinuations(curl));

        string? url = null;
        string? method = null;
        string? body = null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            switch (t)
            {
                case "curl":
                case "curl.exe":
                    continue;

                case "-X":
                case "--request":
                    if (++i < tokens.Count) method = tokens[i];
                    break;

                case "-H":
                case "--header":
                    if (++i < tokens.Count) AddHeader(headers, tokens[i]);
                    break;

                case "-b":
                case "--cookie":
                    if (++i < tokens.Count) headers["cookie"] = tokens[i];
                    break;

                case "-d":
                case "--data":
                case "--data-raw":
                case "--data-binary":
                case "--data-ascii":
                case "--data-urlencode":
                    if (++i < tokens.Count) body = tokens[i];
                    break;

                default:
                    // Bare URL token (Chrome puts it right after curl). Ignore other flags.
                    if (url is null && LooksLikeUrl(t)) url = t;
                    break;
            }
        }

        if (url is null)
        {
            throw new FormatException("Couldn't find a URL in the cURL — make sure you copied the whole command.");
        }

        method ??= body is null ? "GET" : "POST";
        return new ParsedCurl(method, url, headers, body);
    }

    private static void AddHeader(Dictionary<string, string> headers, string raw)
    {
        var idx = raw.IndexOf(':');
        if (idx <= 0) return;
        var name = raw[..idx].Trim();
        var value = raw[(idx + 1)..].Trim();
        if (name.Length > 0) headers[name] = value;
    }

    private static bool LooksLikeUrl(string t) =>
        t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        t.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string StripLineContinuations(string s)
    {
        // bash "\", cmd "^", PowerShell "`" at end of a line -> join lines.
        s = s
            .Replace("\\\r\n", " ").Replace("\\\n", " ")
            .Replace("^\r\n", " ").Replace("^\n", " ")
            .Replace("`\r\n", " ").Replace("`\n", " ");

        // cmd-style "Copy as cURL (cmd)" wraps args in ^" and escapes inner quotes as \^".
        // Once line-continuation carets are gone, the remaining carets are pure quote escaping —
        // dropping them turns ^" into " and \^" into \", which the double-quote tokenizer handles.
        if (s.Contains("^\"", StringComparison.Ordinal))
        {
            s = s.Replace("^", "");
        }

        return s;
    }

    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var i = 0;
        var inToken = false;

        void Flush()
        {
            if (inToken) { tokens.Add(sb.ToString()); sb.Clear(); inToken = false; }
        }

        while (i < s.Length)
        {
            var c = s[i];

            if (char.IsWhiteSpace(c))
            {
                Flush();
                i++;
            }
            else if (c == '\'')
            {
                // bash single-quote: literal until the next single quote.
                inToken = true;
                i++;
                while (i < s.Length && s[i] != '\'') sb.Append(s[i++]);
                i++; // skip closing quote
            }
            else if (c == '"')
            {
                // double-quote: handle \" and \\ escapes (cmd/PowerShell).
                inToken = true;
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '\\'))
                    {
                        sb.Append(s[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        sb.Append(s[i++]);
                    }
                }

                i++; // skip closing quote
            }
            else
            {
                inToken = true;
                sb.Append(c);
                i++;
            }
        }

        Flush();
        return tokens;
    }
}
