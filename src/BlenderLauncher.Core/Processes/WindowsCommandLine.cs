using System.Text;

namespace BlenderLauncher.Core.Processes;

/// <summary>
/// Pure functions for Windows command lines: quoting by the MSVC CRT / <c>CommandLineToArgvW</c> rules (which
/// Blender and most programs use to split their command line), splitting by the same rules, and quoting for
/// <c>cmd.exe /C</c>, whose parsing differs. No Windows API is used, so this runs and is tested anywhere.
/// </summary>
public static class WindowsCommandLine
{
    // Characters that make cmd.exe end a token or act on it unless they are inside double quotes.
    private const string CmdSpecial = " \t\"&|<>^(),;=";

    /// <summary>
    /// Joins a program path and its arguments into a <c>CreateProcess</c> command line. The program is always
    /// quoted whole (the first token is read by different rules: up to the next quote, with no escapes, and a
    /// path cannot contain a quote); each argument is quoted by <see cref="Quote"/>.
    /// </summary>
    public static string Build(string fileName, IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        var sb = new StringBuilder();
        sb.Append('"').Append(fileName).Append('"');
        foreach (var argument in arguments)
        {
            sb.Append(' ');
            AppendQuoted(sb, argument);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Quotes one argument so <c>CommandLineToArgvW</c> reads it back unchanged: left as is when it is non-empty and
    /// has no space, tab, newline, vertical tab or quote; otherwise wrapped in quotes, with each quote escaped as
    /// <c>\"</c> and the backslashes before a quote (or before the closing quote) doubled.
    /// </summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var sb = new StringBuilder(argument.Length + 2);
        AppendQuoted(sb, argument);
        return sb.ToString();
    }

    /// <summary>
    /// Splits argument text by the <c>CommandLineToArgvW</c> rules for arguments after the program name: spaces
    /// and tabs separate arguments outside quotes; <c>2n</c> backslashes before a quote give <c>n</c> backslashes
    /// and the quote opens or closes a quoted part; <c>2n+1</c> backslashes before a quote give <c>n</c>
    /// backslashes and a literal quote; other backslashes are literal; inside a quoted part, <c>""</c> gives a
    /// literal quote and ends the quoted part (as shell32 does; CRT versions differ here). Text without
    /// quotes is simply split on runs of spaces and tabs.
    /// </summary>
    public static List<string> SplitArguments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<string>();
        SplitInto(text.AsSpan(), result);
        return result;
    }

    /// <summary>
    /// <c>CommandLineToArgvW</c> on a whole command line: the program name (argv[0]) is read first, up to the next
    /// quote if it starts with one or else up to the first space or tab, with no escapes; the rest is split by
    /// <see cref="SplitArguments"/>. An empty line gives an empty list (the real function returns the current
    /// executable's path instead).
    /// </summary>
    public static List<string> Parse(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var result = new List<string>();
        var s = commandLine.AsSpan();
        if (s.IsEmpty)
        {
            return result;
        }

        int end;
        if (s[0] == '"')
        {
            var close = s[1..].IndexOf('"');
            end = close < 0 ? s.Length : close + 2;
            result.Add(s[1..(close < 0 ? s.Length : close + 1)].ToString());
        }
        else
        {
            var stop = s.IndexOfAny(' ', '\t');
            end = stop < 0 ? s.Length : stop;
            result.Add(s[..end].ToString());
        }

        SplitInto(s[end..], result);
        return result;
    }

    /// <summary>
    /// Builds the text after <c>cmd /S /C</c> for running a batch file with arguments: each element is wrapped in
    /// double quotes when it is empty or contains a space, tab, quote or one of <c>&amp; | &lt; &gt; ^ ( ) , ; =</c>
    /// (inside quotes cmd.exe takes those literally, and a batch file sees the quoted text as one parameter);
    /// a quote inside an element is doubled. The whole is then wrapped in one more pair of quotes, which
    /// <c>/S</c> makes cmd.exe strip before it parses the rest, so the result is the same whatever the first
    /// element looks like.
    /// </summary>
    /// <remarks>
    /// <c>%NAME%</c> cannot be escaped on a <c>cmd /C</c> line: it expands when a variable of that name exists,
    /// quoted or not. Such text is passed as is.
    /// </remarks>
    public static string BuildCmdArgument(IEnumerable<string> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var sb = new StringBuilder();
        sb.Append('"');
        var first = true;
        foreach (var element in elements)
        {
            if (!first)
            {
                sb.Append(' ');
            }

            first = false;
            if (element.Length > 0 && element.AsSpan().IndexOfAny(CmdSpecial) < 0)
            {
                sb.Append(element);
            }
            else
            {
                sb.Append('"').Append(element.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static void AppendQuoted(StringBuilder sb, string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes before a quote are doubled, and the quote itself is escaped.
            sb.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }

        // Backslashes before the closing quote are doubled so they do not escape it.
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    // The argument loop of CommandLineToArgvW, as Wine documents and reimplements it (dlls/shell32/shell32_main.c).
    private static void SplitInto(ReadOnlySpan<char> s, List<string> result)
    {
        var i = 0;
        while (i < s.Length && s[i] is ' ' or '\t')
        {
            i++;
        }

        if (i == s.Length)
        {
            return;
        }

        var current = new StringBuilder();
        var backslashes = 0;
        var quotes = 0; // 1 inside a quoted part; also counts consecutive quotes after one.
        while (i < s.Length)
        {
            var c = s[i];
            if (c is ' ' or '\t' && quotes == 0)
            {
                result.Add(current.ToString());
                current.Clear();
                backslashes = 0;
                while (i < s.Length && s[i] is ' ' or '\t')
                {
                    i++;
                }

                if (i == s.Length)
                {
                    return;
                }

                continue;
            }

            if (c == '\\')
            {
                current.Append(c);
                backslashes++;
                i++;
                continue;
            }

            if (c == '"')
            {
                if (backslashes % 2 == 0)
                {
                    // 2n backslashes and a quote: n backslashes, and the quote opens or closes.
                    current.Length -= backslashes / 2;
                    quotes++;
                }
                else
                {
                    // 2n+1 backslashes and a quote: n backslashes and a literal quote.
                    current.Length -= (backslashes / 2) + 1;
                    current.Append('"');
                }

                i++;
                backslashes = 0;

                // Consecutive quotes: every third one is literal.
                while (i < s.Length && s[i] == '"')
                {
                    if (++quotes == 3)
                    {
                        current.Append('"');
                        quotes = 0;
                    }

                    i++;
                }

                if (quotes == 2)
                {
                    quotes = 0;
                }

                continue;
            }

            current.Append(c);
            backslashes = 0;
            i++;
        }

        result.Add(current.ToString());
    }
}
