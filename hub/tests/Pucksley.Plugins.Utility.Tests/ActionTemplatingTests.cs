using System.Text.Json.Nodes;
using Pucksley.Plugins.Actions;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Tests;

public class ActionParameterTests
{
    [Fact]
    public void Parses_parameter_lines()
    {
        var parameters = ActionParameters.Parse(
            "room:string:Which room\r\nlevel?:integer:0-100\n\n# a comment\nloud:boolean\nnote\nurl:string:like http://x:80\nmode:the mode to use\nratio : number : 0 to 1");
        Assert.Equal(
            [
                new ActionParameter("room", ParameterType.String, "Which room", false),
                new ActionParameter("level", ParameterType.Integer, "0-100", true),
                new ActionParameter("loud", ParameterType.Boolean, null, false),
                new ActionParameter("note", ParameterType.String, null, false),
                new ActionParameter("url", ParameterType.String, "like http://x:80", false),
                new ActionParameter("mode", ParameterType.String, "the mode to use", false),
                new ActionParameter("ratio", ParameterType.Number, "0 to 1", false),
            ],
            parameters);
    }

    [Theory]
    [InlineData("lev el:string", "parameter line 1: 'lev el' isn't a valid name")]
    [InlineData("9lives:number", "parameter line 1: '9lives' isn't a valid name")]
    [InlineData("a:string\nA:number", "parameter line 2: 'A' is listed twice")]
    public void Rejects_bad_lines(string text, string message) =>
        Assert.StartsWith(message, Assert.Throws<FormatException>(() => ActionParameters.Parse(text)).Message);

    [Fact]
    public void Builds_the_json_schema()
    {
        var schema = ActionParameters.Schema(ActionParameters.Parse("room:string:Which room\nlevel?:integer\nloud:bool"));
        Assert.Equal(
            """{"type":"object","properties":{"room":{"type":"string","description":"Which room"},"level":{"type":"integer"},"loud":{"type":"boolean"}},"required":["room","loud"]}""",
            schema.ToJsonString());
        Assert.Equal("""{"type":"object","properties":{}}""", ActionParameters.Schema([]).ToJsonString());
    }

    [Fact]
    public void Reads_arguments_as_text()
    {
        var parameters = ActionParameters.Parse("name\ncount:integer\nratio:number\nflag:boolean\nopt?:string");
        var values = ActionParameters.ReadArguments(parameters, Args("""{"Name":"Bob","count":"7","ratio":1.5,"flag":"yes"}"""));
        Assert.Equal(("Bob", "7", "1.5", "true", ""), (values["name"], values["count"], values["ratio"], values["flag"], values["opt"]));

        Assert.Equal("3", ActionParameters.ReadArguments(parameters, Args("""{"name":5,"count":2.6,"ratio":"2","flag":false}"""))["count"]);
        Assert.Equal("5", ActionParameters.ReadArguments(parameters, Args("""{"name":5,"count":1,"ratio":1,"flag":true}"""))["name"]);

        Assert.Equal("Missing 'name'.", Assert.Throws<PluginException>(() => ActionParameters.ReadArguments(parameters, Args("{}"))).Message);
        Assert.Equal("'count' should be a whole number.",
            Assert.Throws<PluginException>(() => ActionParameters.ReadArguments(parameters, Args("""{"name":"a","count":"many","ratio":1,"flag":true}"""))).Message);
        Assert.Equal("'flag' should be true or false.",
            Assert.Throws<PluginException>(() => ActionParameters.ReadArguments(parameters, Args("""{"name":"a","count":1,"ratio":1,"flag":"maybe"}"""))).Message);
    }

    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();
}

public class ActionTemplateTests
{
    static readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["q"] = "a b&c/d",
        ["n"] = "5",
        ["msg"] = "He said \"hi\"\n",
        ["empty"] = "",
        ["name"] = "Bob",
        ["flag"] = "true",
        ["evil"] = "1, \"admin\": true",
    };

    [Fact]
    public void Url_values_are_url_encoded() =>
        Assert.Equal("http://h/api?q=a%20b%26c%2Fd&n=5&x={{unknown}}", ActionTemplates.Url(" http://h/api?q={{q}}&n={{ n }}&x={{unknown}} ", Values));

    [Fact]
    public void Json_body_values_are_escaped_or_turned_into_literals()
    {
        var body = ActionTemplates.Body("""{"text": "{{msg}}", "count": {{n}}, "on": {{flag}}, "who": {{name}}, "none": {{empty}}}""", Values, null);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("He said \"hi\"\n", (string)json["text"]!);
        Assert.Equal(5, (int)json["count"]!);
        Assert.True((bool)json["on"]!);
        Assert.Equal("Bob", (string)json["who"]!);
        Assert.Null(json["none"]);
    }

    [Fact]
    public void A_value_cannot_change_a_json_body_structure()
    {
        var json = JsonNode.Parse(ActionTemplates.Body("""{"level": {{evil}}, "note": "{{evil}}"}""", Values, null))!.AsObject();
        Assert.Equal(["level", "note"], json.Select(p => p.Key));
        Assert.Equal("1, \"admin\": true", (string)json["level"]!);
        Assert.Equal("1, \"admin\": true", (string)json["note"]!);
    }

    [Fact]
    public void Other_bodies_are_raw_or_form_encoded()
    {
        Assert.Equal("level=a b&c/d", ActionTemplates.Body("level={{q}}", Values, "text/plain"));
        Assert.Equal("q=a+b%26c%2Fd&n=5", ActionTemplates.Body("q={{q}}&n={{n}}", Values, "application/x-www-form-urlencoded"));
    }

    [Fact]
    public void Header_values_cannot_add_lines()
    {
        var headers = ActionTemplates.Headers("X-Msg: {{msg}}\nnot a header\nX-N: {{n}}\n# X-Off: 1", Values);
        Assert.Equal([new("X-Msg", "He said \"hi\" "), new("X-N", "5")], headers);
    }

    [Fact]
    public void Responses_are_tidied_for_claude()
    {
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": [\n    true\n  ]\n}", ActionTemplates.ResponseBody(""" {"a":1,"b":[true]} """));
        var big = "[" + string.Join(",", Enumerable.Range(0, 400).Select(i => $"{{\"i\":{i}}}")) + "]";
        var shown = ActionTemplates.ResponseBody(big);
        Assert.StartsWith("[{\"i\":0},{\"i\":1}", shown); // compacted, not pretty
        Assert.Contains("characters in all)", shown);
        Assert.Equal("plain text", ActionTemplates.ResponseBody("  plain text \n"));
        Assert.Equal("", ActionTemplates.ResponseBody("  "));
    }
}
