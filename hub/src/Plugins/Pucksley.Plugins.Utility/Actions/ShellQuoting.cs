using System.Text;
using System.Text.RegularExpressions;

namespace Pucksley.Plugins.Actions;

internal enum ShellKind { PowerShell, Pwsh, Cmd, Bash, Sh }

/// <summary>
/// Fills <c>{{param}}</c> placeholders in a script action's command so that every value reaches the
/// program as literal text, whatever it contains (quotes, <c>$(…)</c>, <c>;</c>, <c>&amp;</c>, <c>%VAR%</c>…).
/// The quoting depends on where the placeholder sits — bare, inside '…', inside "…", inside $(…) — so
/// natural templates such as <c>echo "Hello {{name}}"</c> are safe too. A placeholder straight after an
/// escape character, or after a here-document, is rejected (use the <c>PUCK_*</c> environment variables there).
/// </summary>
internal static partial class ShellQuoting
{
    /// <summary>What surrounds a placeholder.</summary>
    internal enum Context { Code, Single, Double, AnsiC }

    public static bool TryParseShell(string? name, out ShellKind shell)
    {
        switch ((name ?? "").Trim().ToLowerInvariant())
        {
            case "": shell = OperatingSystem.IsWindows() ? ShellKind.PowerShell : ShellKind.Sh; return true;
            case "powershell" or "powershell.exe" or "windows powershell": shell = ShellKind.PowerShell; return true;
            case "pwsh" or "pwsh.exe": shell = ShellKind.Pwsh; return true;
            case "cmd" or "cmd.exe": shell = ShellKind.Cmd; return true;
            case "bash": shell = ShellKind.Bash; return true;
            case "sh": shell = ShellKind.Sh; return true;
            default: shell = default; return false;
        }
    }

    /// <summary>The command with values filled in. Throws <see cref="FormatException"/> when a placeholder can't be quoted safely where it is.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values, ShellKind shell)
    {
        var matches = ActionTemplates.Placeholder().Matches(template).ToList();
        if (shell == ShellKind.Cmd) return RenderCmd(template, matches, values);

        bool powerShell = shell is ShellKind.PowerShell or ShellKind.Pwsh;
        var contexts = powerShell ? PowerShellContexts(template, matches) : PosixContexts(template, matches, bash: shell == ShellKind.Bash);
        var sb = new StringBuilder(template.Length + 32);
        int pos = 0;
        for (int k = 0; k < matches.Count; k++)
        {
            var m = matches[k];
            sb.Append(template, pos, m.Index - pos);
            pos = m.Index + m.Length;
            if (!values.TryGetValue(m.Groups[1].Value, out var value)) sb.Append(m.Value);
            else
            {
                value = value.Replace("\0", ""); // can't be passed on a command line
                sb.Append(powerShell ? PowerShellQuote(value, contexts[k]) : PosixQuote(value, contexts[k]));
            }
        }
        sb.Append(template, pos, template.Length - pos);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ sh / bash

    /// <summary>A value for sh/bash: '…' when bare; escaped to fit inside '…', "…" or $'…'.</summary>
    internal static string PosixQuote(string value, Context context) => context switch
    {
        Context.Single => value.Replace("'", @"'\''"), // close, escaped quote, reopen
        Context.Double => Escape(value, c => c is '\\' or '"' or '$' or '`', '\\'),
        Context.AnsiC => Escape(value, c => c is '\\' or '\'', '\\'),
        _ => "'" + value.Replace("'", @"'\''") + "'",
    };

    /// <summary>The quoting context of each placeholder in an sh/bash command.</summary>
    internal static List<Context> PosixContexts(string t, IReadOnlyList<Match> matches, bool bash)
    {
        var contexts = new List<Context>(matches.Count);
        var stack = new Stack<Frame>();
        stack.Push(new Frame(Context.Code));
        int m = 0;
        for (int i = 0; i < t.Length; i++)
        {
            if (m < matches.Count && matches[m].Index == i)
            {
                contexts.Add(stack.Peek().Kind);
                i += matches[m++].Length - 1;
                continue;
            }
            char c = t[i], next = i + 1 < t.Length ? t[i + 1] : '\0';
            var top = stack.Peek();
            switch (top.Kind)
            {
                case Context.Single:
                    if (c == '\'') stack.Pop();
                    break;
                case Context.AnsiC:
                    if (c == '\\') i = SkipEscaped(i, matches, m);
                    else if (c == '\'') stack.Pop();
                    break;
                case Context.Double:
                    if (c == '\\') i = SkipEscaped(i, matches, m);
                    else if (c == '"') stack.Pop();
                    else if (c == '$' && next == '(') { stack.Push(new Frame(Context.Code, paren: true)); i++; }
                    else if (c == '`') stack.Push(new Frame(Context.Code, backtick: true));
                    break;
                default:
                    if (c == '\\') i = SkipEscaped(i, matches, m);
                    else if (c == '\'') stack.Push(new Frame(Context.Single));
                    else if (c == '"') stack.Push(new Frame(Context.Double));
                    else if (c == '$' && next == '\'' && bash) { stack.Push(new Frame(Context.AnsiC)); i++; }
                    else if (c == '$' && next == '(') { stack.Push(new Frame(Context.Code, paren: true)); i++; }
                    else if (c == '`') { if (top.Backtick) stack.Pop(); else stack.Push(new Frame(Context.Code, backtick: true)); }
                    else if (c == '(' && top.Paren) top.Depth++;
                    else if (c == ')' && top.Paren) { if (top.Depth == 0) stack.Pop(); else top.Depth--; }
                    else if (c == '#' && (i == 0 || char.IsWhiteSpace(t[i - 1]) || t[i - 1] is ';' or '&' or '|' or '('))
                        i = SkipToLineEnd(t, i, matches, ref m, contexts);
                    else if (c == '<' && next == '<' && (i + 2 >= t.Length || t[i + 2] != '<') && m < matches.Count)
                        throw new FormatException($"{Name(matches[m])} comes after a here-document (<<); use ${EnvName(matches[m])} there instead");
                    break;
            }
        }
        return contexts;
    }

    // ------------------------------------------------------------------ PowerShell

    static bool IsSingleQuote(char c) => c is '\'' or '‘' or '’' or '‚' or '‛'; // PowerShell treats curly quotes as quotes too
    static bool IsDoubleQuote(char c) => c is '"' or '“' or '”' or '„';

    /// <summary>A value for PowerShell: '…' when bare; escaped to fit inside '…' or "…".</summary>
    internal static string PowerShellQuote(string value, Context context) => context switch
    {
        Context.Single => DoubleUp(value, IsSingleQuote),
        Context.Double => Escape(value, c => c is '`' or '$' || IsDoubleQuote(c), '`'),
        _ => "'" + DoubleUp(value, IsSingleQuote) + "'",
    };

    /// <summary>The quoting context of each placeholder in a PowerShell command.</summary>
    internal static List<Context> PowerShellContexts(string t, IReadOnlyList<Match> matches)
    {
        var contexts = new List<Context>(matches.Count);
        var stack = new Stack<Frame>();
        stack.Push(new Frame(Context.Code));
        int m = 0;
        for (int i = 0; i < t.Length; i++)
        {
            if (m < matches.Count && matches[m].Index == i)
            {
                contexts.Add(stack.Peek().Kind);
                i += matches[m++].Length - 1;
                continue;
            }
            char c = t[i], next = i + 1 < t.Length ? t[i + 1] : '\0';
            var top = stack.Peek();
            switch (top.Kind)
            {
                case Context.Single:
                    if (IsSingleQuote(c)) { if (IsSingleQuote(next)) i++; else stack.Pop(); } // '' is an escaped quote
                    break;
                case Context.Double:
                    if (c == '`') i = SkipEscaped(i, matches, m);
                    else if (IsDoubleQuote(c)) { if (IsDoubleQuote(next)) i++; else stack.Pop(); }
                    else if (c == '$' && next == '(') { stack.Push(new Frame(Context.Code, paren: true)); i++; }
                    break;
                default:
                    if (c == '`') i = SkipEscaped(i, matches, m);
                    else if (c == '@' && (IsSingleQuote(next) || IsDoubleQuote(next)) && RestOfLineIsBlank(t, i + 2))
                    {
                        if (m < matches.Count)
                            throw new FormatException($"{Name(matches[m])} comes after a here-string (@' or @\"); use $env:{EnvName(matches[m])} there instead");
                        return contexts;
                    }
                    else if (IsSingleQuote(c)) stack.Push(new Frame(Context.Single));
                    else if (IsDoubleQuote(c)) stack.Push(new Frame(Context.Double));
                    else if (c == '<' && next == '#') i = SkipBlockComment(t, i, matches, ref m, contexts);
                    else if (c == '#' && (i == 0 || char.IsWhiteSpace(t[i - 1]) || t[i - 1] is ';' or '|' or '&' or '(' or '{' or '}'))
                        i = SkipToLineEnd(t, i, matches, ref m, contexts);
                    else if (c == '$' && next == '(') { stack.Push(new Frame(Context.Code, paren: true)); i++; }
                    else if (c == '(' && top.Paren) top.Depth++;
                    else if (c == ')' && top.Paren) { if (top.Depth == 0) stack.Pop(); else top.Depth--; }
                    break;
            }
        }
        return contexts;
    }

    // ------------------------------------------------------------------ cmd

    /// <summary>call / for / cmd where a command starts (line start, or after &amp;, ||, ( or @).</summary>
    [GeneratedRegex(@"(^|[&(@]|\|\|)\s*(call|for|cmd|cmd\.exe)(?=[\s/]|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CmdReparseRegex();

    /// <summary>
    /// cmd and the program it starts disagree about quotes, and cmd has no reliable escape inside quotes.
    /// So each value becomes a fully caret-escaped argument (<c>^"…^"</c>): cmd never sees a real quote or
    /// an unescaped <c>&amp; | &lt; &gt; ( ) % ^</c>, and the program's own argument parser gets the exact
    /// text back. A quoted string in the template that contains values is rewritten the same way.
    /// Escaping holds for one pass of cmd's parser, so commands that make cmd parse again (a pipe, call,
    /// for, a nested cmd) can't take values.
    /// </summary>
    static string RenderCmd(string t, IReadOnlyList<Match> matches, IReadOnlyDictionary<string, string> values)
    {
        if (matches.Count > 0)
        {
            var code = UnquotedCmdText(t);
            if (HasPipe(code) || CmdReparseRegex().IsMatch(code))
                throw new FormatException($"{Name(matches[0])} can't be used in a cmd command with a pipe, call, for or a nested cmd (cmd reads those twice); use PowerShell for this one");
        }
        var sb = new StringBuilder(t.Length + 32);
        int m = 0, i = 0;
        while (i < t.Length)
        {
            if (m < matches.Count && matches[m].Index == i)
            {
                var match = matches[m++];
                if (values.TryGetValue(match.Groups[1].Value, out var v))
                {
                    sb.Append('\\', TrailingBackslashes(sb)); // backslashes just before a quote would escape it
                    sb.Append("^\"").Append(CaretEscape(ArgvEscape(v, atEnd: true), percent: true)).Append("^\"");
                }
                else sb.Append(match.Value);
                i = match.Index + match.Length;
                continue;
            }
            char c = t[i];
            if (c == '^')
            {
                if (m < matches.Count && matches[m].Index == i + 1)
                    throw new FormatException($"{Name(matches[m])} comes straight after ^; remove the ^");
                sb.Append(t, i, Math.Min(2, t.Length - i));
                i += 2;
                continue;
            }
            if (c != '"')
            {
                sb.Append(c);
                i++;
                continue;
            }

            int close = t.IndexOf('"', i + 1);
            int end = close < 0 ? t.Length : close; // the string's contents are t[i+1 .. end)
            int after = close < 0 ? t.Length : close + 1;
            if (m >= matches.Count || matches[m].Index >= end)
            {
                sb.Append(t, i, after - i);
                i = after;
                continue;
            }
            // A quoted string with values in it: rewrite it with escaped quotes. The template's own text in it
            // keeps its meaning (caret-escaped, except % so %VAR% still expands).
            sb.Append("^\"");
            int pos = i + 1;
            while (m < matches.Count && matches[m].Index < end)
            {
                var match = matches[m++];
                var literal = t[pos..match.Index];
                sb.Append(CaretEscape(literal, percent: false));
                if (values.TryGetValue(match.Groups[1].Value, out var v))
                {
                    bool last = match.Index + match.Length == end;
                    sb.Append(CaretEscape(ArgvEscape(v, last, TrailingBackslashes(literal)), percent: true));
                }
                else sb.Append(CaretEscape(match.Value, percent: false));
                pos = match.Index + match.Length;
            }
            sb.Append(CaretEscape(t[pos..end], percent: false));
            if (close >= 0) sb.Append("^\"");
            i = after;
        }
        return sb.ToString();
    }

    /// <summary>
    /// The inside of a quoted Windows command-line argument (CommandLineToArgvW / C runtime rules): backslashes
    /// before a quote are doubled and the quote escaped. <paramref name="precedingBackslashes"/> counts
    /// backslashes already written just before this text, which join the first run.
    /// </summary>
    internal static string ArgvEscape(string value, bool atEnd, int precedingBackslashes = 0)
    {
        var sb = new StringBuilder(value.Length + 8);
        int slashes = 0;
        bool firstRun = true;
        foreach (char c in value.Replace("\0", ""))
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            int joined = firstRun ? precedingBackslashes : 0;
            if (c == '"') sb.Append('\\', slashes * 2 + joined + 1).Append('"');
            else sb.Append('\\', slashes).Append(c);
            slashes = 0;
            firstRun = false;
        }
        // Trailing backslashes are followed by the closing quote when the value ends the argument.
        sb.Append('\\', atEnd ? slashes * 2 + (firstRun ? precedingBackslashes : 0) : slashes);
        return sb.ToString();
    }

    static string CaretEscape(string text, bool percent)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c is '\r' or '\n') { sb.Append(' '); continue; } // a line break would end the command
            if (c is '^' or '&' or '|' or '<' or '>' or '(' or ')' or '!' or '"' || (percent && c == '%')) sb.Append('^');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The template with quoted text and ^-escaped characters blanked out, leaving what cmd treats as syntax.</summary>
    static string UnquotedCmdText(string t)
    {
        var sb = new StringBuilder(t.Length);
        bool quoted = false;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '"') quoted = !quoted;
            if (quoted || c == '"') sb.Append(' ');
            else if (c == '^' && i + 1 < t.Length) { sb.Append("  "); i++; }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>A single | (a pipe), as opposed to || (or-else).</summary>
    static bool HasPipe(string code)
    {
        for (int i = 0; i < code.Length; i++)
        {
            if (code[i] != '|') continue;
            if (i + 1 < code.Length && code[i + 1] == '|') { i++; continue; }
            return true;
        }
        return false;
    }

    static int TrailingBackslashes(StringBuilder sb)
    {
        int n = 0;
        while (n < sb.Length && sb[sb.Length - 1 - n] == '\\') n++;
        return n;
    }

    static int TrailingBackslashes(string s)
    {
        int n = 0;
        while (n < s.Length && s[s.Length - 1 - n] == '\\') n++;
        return n;
    }

    // ------------------------------------------------------------------ shared lexer bits

    sealed class Frame(Context kind, bool paren = false, bool backtick = false)
    {
        public Context Kind { get; } = kind;
        public bool Paren { get; } = paren;       // opened by $( — ends at the matching )
        public bool Backtick { get; } = backtick; // opened by ` — ends at the next `
        public int Depth { get; set; }
    }

    /// <summary>Skips the character after an escape; a placeholder there can't be quoted.</summary>
    static int SkipEscaped(int i, IReadOnlyList<Match> matches, int m)
    {
        if (m < matches.Count && matches[m].Index == i + 1)
            throw new FormatException($"{Name(matches[m])} comes straight after an escape character; remove the escape");
        return i + 1;
    }

    /// <summary>A comment runs to the end of the line; placeholders in it are never run (quoted as code anyway).</summary>
    static int SkipToLineEnd(string t, int i, IReadOnlyList<Match> matches, ref int m, List<Context> contexts)
    {
        int end = t.IndexOf('\n', i);
        if (end < 0) end = t.Length;
        while (m < matches.Count && matches[m].Index < end)
        {
            contexts.Add(Context.Code);
            m++;
        }
        return end - 1;
    }

    static int SkipBlockComment(string t, int i, IReadOnlyList<Match> matches, ref int m, List<Context> contexts)
    {
        int close = t.IndexOf("#>", i + 2, StringComparison.Ordinal);
        int end = close < 0 ? t.Length : close + 2;
        while (m < matches.Count && matches[m].Index < end)
        {
            contexts.Add(Context.Code);
            m++;
        }
        return end - 1;
    }

    static bool RestOfLineIsBlank(string t, int from)
    {
        for (int i = from; i < t.Length && t[i] != '\n'; i++)
            if (!char.IsWhiteSpace(t[i])) return false;
        return true;
    }

    static string Escape(string value, Func<char, bool> special, char escape)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            if (special(c)) sb.Append(escape);
            sb.Append(c);
        }
        return sb.ToString();
    }

    static string DoubleUp(string value, Func<char, bool> quote)
    {
        var sb = new StringBuilder(value.Length + 4);
        foreach (char c in value)
        {
            sb.Append(c);
            if (quote(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    static string Name(Match m) => "{{" + m.Groups[1].Value + "}}";

    static string EnvName(Match m) => "PUCK_" + m.Groups[1].Value.ToUpperInvariant();
}
