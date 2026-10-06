using System.Text;
using System.Text.RegularExpressions;

namespace FluentDesktop.Core.Infrastructure;

/// <summary>Windows argv quoting (CommandLineToArgvW rules, optionally cmd-escaped).</summary>
public static partial class WindowsCommandLine
{
    public static string Join(IEnumerable<string> args, bool forCmd = false) =>
        string.Join(' ', args.Select(a => Quote(a, forCmd)));

    public static string Quote(string arg, bool forCmd = false)
    {
        string quoted;
        if (arg.Length > 0 && !NeedsQuotes().IsMatch(arg))
        {
            quoted = arg;
        }
        else
        {
            var sb = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1).Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes).Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2).Append('"');
            quoted = sb.ToString();
        }

        // cmd.exe metacharacters must be ^-escaped (including the quotes).
        return forCmd ? CmdMeta().Replace(quoted, "^$0") : quoted;
    }

    /// <summary>Splits a user-entered argument string, honouring double quotes.</summary>
    public static List<string> Split(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return result;
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in input.Trim())
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken) result.Add(current.ToString());
                current.Clear();
                hasToken = false;
                continue;
            }
            current.Append(c);
            hasToken = true;
        }
        if (hasToken) result.Add(current.ToString());
        return result;
    }

    [GeneratedRegex("[\\s\"]")]
    private static partial Regex NeedsQuotes();

    [GeneratedRegex("[()%!^\"<>&|]")]
    private static partial Regex CmdMeta();
}
