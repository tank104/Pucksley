using Pucksley.Plugins.Actions;

namespace Pucksley.Plugins.Tests;

public class ShellQuotingTests
{
    static string Render(string template, string value, ShellKind shell) =>
        ShellQuoting.Render(template, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x"] = value }, shell);

    // ------------------------------------------------------------------ bash / sh

    [Fact]
    public void Bash_bare_value_is_single_quoted() =>
        Assert.Equal("""echo 'it'\''s'""", Render("echo {{x}}", "it's", ShellKind.Bash));

    [Fact]
    public void Bash_value_inside_double_quotes_is_backslash_escaped()
    {
        Assert.Equal("""
            echo "Hi \$(id) \"q\" \`x\` \\"
            """, Render("""echo "Hi {{x}}" """.TrimEnd(), """$(id) "q" `x` \""", ShellKind.Bash));
    }

    [Fact]
    public void Bash_value_inside_single_quotes_closes_and_reopens() =>
        Assert.Equal("""echo 'Hi it'\''s'""", Render("echo 'Hi {{x}}'", "it's", ShellKind.Bash));

    [Fact]
    public void Bash_command_substitution_inside_double_quotes_is_code() =>
        Assert.Equal("""echo "$(printf %s 'a b')" """.TrimEnd(), Render("""echo "$(printf %s {{x}})" """.TrimEnd(), "a b", ShellKind.Bash));

    [Fact]
    public void Bash_ansi_c_quotes_but_not_in_sh()
    {
        Assert.Equal("""echo $'a\'b\\c'""", Render("echo $'{{x}}'", """a'b\c""", ShellKind.Bash));
        Assert.Equal("""echo $'a'\''b\c'""", Render("echo $'{{x}}'", """a'b\c""", ShellKind.Sh)); // sh has no $'…'
    }

    [Fact]
    public void Bash_comment_with_an_apostrophe_does_not_confuse_it() =>
        Assert.Equal("# don't panic\necho 'a b'", Render("# don't panic\necho {{x}}", "a b", ShellKind.Bash));

    // ------------------------------------------------------------------ PowerShell

    [Fact]
    public void PowerShell_bare_value_is_single_quoted() =>
        Assert.Equal("Write-Output 'it''s $env:USERNAME'", Render("Write-Output {{x}}", "it's $env:USERNAME", ShellKind.PowerShell));

    [Fact]
    public void PowerShell_value_inside_double_quotes_is_backtick_escaped()
    {
        Assert.Equal("""
            Write-Output "Hi `$x ``n `"q`""
            """, Render("""
            Write-Output "Hi {{x}}"
            """, """$x `n "q" """.TrimEnd(), ShellKind.Pwsh));
    }

    [Fact]
    public void PowerShell_curly_quotes_are_doubled_too() =>
        Assert.Equal("Write-Output 'it\u2019\u2019s'", Render("Write-Output {{x}}", "it\u2019s", ShellKind.PowerShell));

    [Fact]
    public void PowerShell_comments_and_subexpressions_are_tracked()
    {
        Assert.Equal("# don't\nWrite-Output 'a b'", Render("# don't\nWrite-Output {{x}}", "a b", ShellKind.PowerShell));
        Assert.Equal("<# it's #> Write-Output 'a b'", Render("<# it's #> Write-Output {{x}}", "a b", ShellKind.PowerShell));
        Assert.Equal("""Write-Output "Total: $( 'a b' )" """.TrimEnd(), Render("""Write-Output "Total: $( {{x}} )" """.TrimEnd(), "a b", ShellKind.PowerShell));
    }

    // ------------------------------------------------------------------ cmd

    [Fact]
    public void Cmd_bare_value_is_a_caret_escaped_argument() =>
        Assert.Equal("""echo ^"a^&b^" """.TrimEnd(), Render("echo {{x}}", "a&b", ShellKind.Cmd));

    [Fact]
    public void Cmd_percent_signs_and_quotes_are_escaped() =>
        Assert.Equal("""echo ^"50^% \^"x\^"^" """.TrimEnd(), Render("echo {{x}}", "50% \"x\"", ShellKind.Cmd));

    [Fact]
    public void Cmd_quoted_string_with_a_value_is_rewritten() =>
        Assert.Equal("""echo ^"Hi a^&b^!^" """.TrimEnd(), Render("""echo "Hi {{x}}!" """.TrimEnd(), "a&b", ShellKind.Cmd));

    [Fact]
    public void Cmd_keeps_the_templates_own_variables() =>
        Assert.Equal("""type ^"%USERPROFILE%\notes.txt^" """.TrimEnd(), Render("""type "%USERPROFILE%\{{x}}" """.TrimEnd(), "notes.txt", ShellKind.Cmd));

    [Theory]
    [InlineData("echo {{x}} | more")]
    [InlineData("call run.bat {{x}}")]
    [InlineData("for /f %a in ('dir {{x}}') do echo %a")]
    [InlineData("cmd /c echo {{x}}")]
    public void Cmd_commands_that_parse_twice_cannot_take_values(string template) =>
        Assert.Contains("can't be used in a cmd command with a pipe", Assert.Throws<FormatException>(() => Render(template, "a&b", ShellKind.Cmd)).Message);

    [Fact]
    public void Cmd_or_else_and_quoted_pipes_are_fine()
    {
        Assert.Equal("""find ^"x^" || echo "a|b" """.TrimEnd(), Render("""find {{x}} || echo "a|b" """.TrimEnd(), "x", ShellKind.Cmd));
        Assert.Equal("echo cmd is fine | more", Render("echo cmd is fine | more", "x", ShellKind.Cmd)); // no values: nothing to protect
        Assert.Equal("""echo thanks for ^"x^" """.TrimEnd(), Render("echo thanks for {{x}}", "x", ShellKind.Cmd)); // "for" as a word, not a command
    }

    [Fact]
    public void Cmd_backslashes_before_a_value_are_doubled() =>
        Assert.Equal("""dir C:\temp\\^"a b^" """.TrimEnd(), Render("""dir C:\temp\{{x}}""", "a b", ShellKind.Cmd));

    [Theory]
    [InlineData(@"a\b", true, 0, @"a\b")]
    [InlineData(@"a\""b", false, 0, @"a\\\""b")]
    [InlineData(@"ends\", true, 0, @"ends\\")]
    [InlineData(@"ends\", false, 0, @"ends\")]
    [InlineData(@"""x", false, 1, @"\\""x")]
    public void Windows_argument_escaping(string value, bool atEnd, int preceding, string expected) =>
        Assert.Equal(expected, ShellQuoting.ArgvEscape(value, atEnd, preceding));

    // ------------------------------------------------------------------ rejected templates

    [Theory]
    [InlineData(@"echo \{{x}}", "bash")]
    [InlineData("echo `{{x}}", "powershell")]
    [InlineData("echo ^{{x}}", "cmd")]
    [InlineData("cat <<EOF\n{{x}}\nEOF", "sh")]
    [InlineData("$t = @'\n{{x}}\n'@", "pwsh")]
    public void Placeholders_that_cannot_be_quoted_safely_are_rejected(string template, string shellName)
    {
        Assert.True(ShellQuoting.TryParseShell(shellName, out var shell));
        Assert.Throws<FormatException>(() => Render(template, "x", shell));
    }

    [Fact]
    public void Unknown_placeholders_are_left_alone() =>
        Assert.Equal("echo {{other}} 'a'", Render("echo {{other}} {{x}}", "a", ShellKind.Bash));

    // ------------------------------------------------------------------ real shells

    static async Task<string[]> RunAsync(ShellKind shell, string template, string value)
    {
        var command = Render(template, value, shell);
        var launch = ScriptRunner.Prepare(shell, command, new Dictionary<string, string> { ["PUCK_X"] = value }, null);
        var result = await ScriptRunner.RunAsync(launch, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Error}\ncommand: {command}");
        return result.Output.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
    }

    [WindowsFact]
    public async Task Cmd_delivers_values_intact_to_a_program()
    {
        var program = $"\"{Program.DotNet}\" \"{Program.AssemblyPath}\" --echo-args";
        var lines = await RunAsync(ShellKind.Cmd, program + """ {{x}} "in quotes: {{x}}!" """.TrimEnd(), TestUtil.Nasty);
        Assert.Equal([$"[{TestUtil.Nasty}]", $"[in quotes: {TestUtil.Nasty}!]"], lines);

        var trailing = await RunAsync(ShellKind.Cmd, program + " {{x}}", @"C:\dir\");
        Assert.Equal([@"[C:\dir\]"], trailing);
    }

    [WindowsFact]
    public async Task PowerShell_prints_values_literally()
    {
        var lines = await RunAsync(ShellKind.PowerShell,
            "Write-Output {{x}}\nWrite-Output \"[{{x}}]\"\nWrite-Output '<{{x}}>'\n# it's a comment: {{x}}\nWrite-Output \"$( {{x}} )\"\nWrite-Output $env:PUCK_X",
            TestUtil.Nasty);
        Assert.Equal([TestUtil.Nasty, $"[{TestUtil.Nasty}]", $"<{TestUtil.Nasty}>", TestUtil.Nasty, TestUtil.Nasty], lines);
    }

    [BashFact]
    public async Task Bash_prints_values_literally()
    {
        var lines = await RunAsync(ShellKind.Bash,
            """printf '%s\n' {{x}} "[{{x}}]" '<{{x}}>' "$(printf '%s' {{x}})" "$PUCK_X" """.TrimEnd(),
            TestUtil.Nasty);
        Assert.Equal([TestUtil.Nasty, $"[{TestUtil.Nasty}]", $"<{TestUtil.Nasty}>", TestUtil.Nasty, TestUtil.Nasty], lines);
    }

    [BashFact]
    public async Task Bash_scripts_from_stdin_behave_like_dash_c()
    {
        // Windows line endings are fixed up, a command reading stdin gets EOF (not the rest of the script),
        // and the exit code is the script's.
        var lines = await RunAsync(ShellKind.Bash, "read -r line || echo \"no input\"\r\necho {{x}}\r\n", "after");
        Assert.Equal(["no input", "after"], lines);
        var launch = ScriptRunner.Prepare(ShellKind.Sh, "exit 7", new Dictionary<string, string>(), null);
        Assert.Equal(7, (await ScriptRunner.RunAsync(launch, TimeSpan.FromSeconds(30), CancellationToken.None)).ExitCode);
    }
}
