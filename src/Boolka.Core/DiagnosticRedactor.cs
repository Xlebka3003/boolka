using System.Text.RegularExpressions;

namespace Boolka;

internal sealed class DiagnosticRedactor
{
    private static Regex Pattern(string value) => new(value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static readonly Regex WindowsPath = Pattern("\\b[A-Z]:[\\\\/][^\\r\\n\"<>|]*");
    private static readonly Regex NetworkPath = Pattern("\\\\\\\\[^\\\\\\s\"<>|]+\\\\[^\\r\\n\"<>|]*");
    private static readonly Regex UnixHome = Pattern("(?<![\\w:/])/(?:home|Users|tmp|mnt|var)/[^\\s\"<>]*");
    private static readonly Regex Url = Pattern("https?://[^\\s\"<>]+");
    private static readonly Regex Email = Pattern("[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}");
    private static readonly Regex Secrets = Pattern("\\b(?:token|password|authorization|api[_-]?key|cookie)\\b\\s*[:=][^\\r\\n]*");
    private readonly string[] identities;

    public DiagnosticRedactor() => identities = new[] { Environment.UserName, Environment.MachineName, Environment.UserDomainName }
        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public string Filter(string text)
    {
        try
        {
            text = Secrets.Replace(text, "<secret>");
            text = Url.Replace(text, "<url>");
            text = WindowsPath.Replace(text, "<path>");
            text = NetworkPath.Replace(text, "<network-path>");
            text = UnixHome.Replace(text, "<path>");
            text = Email.Replace(text, "<email>");
            foreach (var identity in identities)
                text = Pattern("(?<![\\w])" + Regex.Escape(identity) + "(?![\\w])").Replace(text, "<user-or-device>");
            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            // При сбое фильтра не сохраняем исходный текст: приватность важнее полноты сообщения.
            return "<diagnostic text omitted: filtering timeout>";
        }
    }
}
