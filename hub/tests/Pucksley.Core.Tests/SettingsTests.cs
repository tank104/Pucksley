using System.Text.Json.Nodes;
using Pucksley.Core.Settings;
using Pucksley.Core.Tests.Support;
using Pucksley.Sdk;

namespace Pucksley.Core.Tests;

public enum DemoMode { Auto, Manual }

public sealed class DemoDevice
{
    [Setting("Name", Order = 1)] public string Name { get; set; } = "";
    [Setting("Password", Secret = true, Order = 2)] public string Password { get; set; } = "";
    /// <summary>Kept by the plugin, never on the form.</summary>
    [Secret] public string PairingKey { get; set; } = "";
    public string Notes { get; set; } = "";
    [Setting("Port", Order = 3)] public int Port { get; set; } = 80;
}

public sealed class DemoSettings
{
    [Setting("API key", Secret = true, Order = 2, Help = "From the vendor.")] public string ApiKey { get; set; } = "";
    [Setting("Host", Order = 1, Placeholder = "192.168.1.2")] public string Host { get; set; } = "";
    [Setting("Devices", Order = 3)] public List<DemoDevice> Devices { get; set; } = [];
    [Setting("Rooms", Order = 4)] public List<string> Rooms { get; set; } = [];
    [Setting("Mode", Order = 5)] public DemoMode Mode { get; set; }
    [Setting("Flavour", Order = 6, Options = ["sweet", "sour"])] public string Flavour { get; set; } = "sweet";
    [Setting("Notes", Order = 7, Multiline = true, Advanced = true)] public string Notes { get; set; } = "";
    [Setting("Volume", Order = 8)] public double Volume { get; set; } = 0.5;
    [Setting("Enabled", Order = 9)] public bool Enabled { get; set; } = true;
    [Setting("Latitude", Order = 10)] public double? Latitude { get; set; }
    /// <summary>An OAuth refresh token the plugin saves itself.</summary>
    [Secret] public string RefreshToken { get; set; } = "";
    public string AccountName { get; set; } = "";
}

/// <summary>SettingsStore: secrets encrypted at rest, decrypted on load, robust loading.</summary>
public sealed class SettingsStoreTests
{
    static string NewPath() => Path.Combine(TestEnvironment.NewTempDir("settings"), "settings.json");

    [Fact]
    public void Hub_secrets_and_puck_tokens_are_encrypted_at_rest_and_decrypted_on_load()
    {
        var path = NewPath();
        var store = new SettingsStore(path);
        store.Update(s =>
        {
            s.Assistant.AnthropicApiKey = "sk-ant-super-secret-123";
            s.Pucks.Add(new PuckRecord { Id = "a1b2c3d4e5f60708", Name = "Kitchen", Token = "0123456789abcdef0123456789abcdef" });
        });

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("sk-ant-super-secret-123", raw);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", raw);
        var json = JsonNode.Parse(raw)!;
        Assert.StartsWith("enc:", json["assistant"]!["anthropicApiKey"]!.GetValue<string>());
        Assert.StartsWith("enc:", json["pucks"]![0]!["token"]!.GetValue<string>());
        Assert.Equal("Kitchen", json["pucks"]![0]!["name"]!.GetValue<string>());   // only secrets are scrambled

        var reloaded = new SettingsStore(path);
        Assert.Equal("sk-ant-super-secret-123", reloaded.Current.Assistant.AnthropicApiKey);
        Assert.Equal("0123456789abcdef0123456789abcdef", reloaded.Current.Pucks.Single().Token);
        // …and saving again doesn't double-encrypt
        reloaded.Update(s => s.Port = 8800);
        Assert.Equal("sk-ant-super-secret-123", new SettingsStore(path).Current.Assistant.AnthropicApiKey);
    }

    [Fact]
    public void Plugin_secrets_are_encrypted_including_hidden_ones_and_list_rows()
    {
        var path = NewPath();
        var store = new SettingsStore(path);
        store.SavePluginSettings("demo", new DemoSettings
        {
            Host = "10.0.0.5",
            ApiKey = "vendor-key-1",
            RefreshToken = "refresh-xyz",
            AccountName = "Sam",
            Devices = [new DemoDevice { Name = "Lamp", Password = "lamp-pw", PairingKey = "pair-1", Notes = "by the couch" }],
        });

        var raw = File.ReadAllText(path);
        foreach (var secret in new[] { "vendor-key-1", "refresh-xyz", "lamp-pw", "pair-1" }) Assert.DoesNotContain(secret, raw);
        foreach (var plain in new[] { "10.0.0.5", "Sam", "Lamp", "by the couch" }) Assert.Contains(plain, raw);

        var loaded = (DemoSettings)new SettingsStore(path).PluginSettings("demo", typeof(DemoSettings));
        Assert.Equal("vendor-key-1", loaded.ApiKey);
        Assert.Equal("refresh-xyz", loaded.RefreshToken);
        Assert.Equal("Sam", loaded.AccountName);
        var d = Assert.Single(loaded.Devices);
        Assert.Equal(("Lamp", "lamp-pw", "pair-1", "by the couch"), (d.Name, d.Password, d.PairingKey, d.Notes));
    }

    [Fact]
    public void Missing_plugin_settings_come_back_as_defaults()
    {
        var store = new SettingsStore(NewPath());
        var s = (DemoSettings)store.PluginSettings("nothing-saved", typeof(DemoSettings));
        Assert.Equal("sweet", s.Flavour);
        Assert.Empty(s.Devices);
    }

    [Fact]
    public void A_new_store_makes_ids_and_keeps_them()
    {
        var path = NewPath();
        var first = new SettingsStore(path).Current;
        Assert.Matches("^[0-9a-f]{16}$", first.HubId);
        Assert.Matches("^[0-9a-f]{24}$", first.AdminToken);
        Assert.Equal(Environment.MachineName, first.HubName);
        var second = new SettingsStore(path).Current;
        Assert.Equal((first.HubId, first.AdminToken), (second.HubId, second.AdminToken));
    }

    [Fact]
    public void A_broken_file_is_kept_aside_and_defaults_load()
    {
        var path = NewPath();
        File.WriteAllText(path, "{ this is not json");
        var store = new SettingsStore(path);
        Assert.Equal(8765, store.Current.Port);
        Assert.Equal("{ this is not json", File.ReadAllText(path + ".broken"));
    }

    [Fact]
    public void Old_or_partial_files_load_with_defaults_for_the_rest()
    {
        var path = NewPath();
        File.WriteAllText(path, """{"port": 9999, "somethingFromTheFuture": 1, "plugins": {"Agent-Tools": {"enabled": false}}}""");
        var s = new SettingsStore(path).Current;
        Assert.Equal(9999, s.Port);
        Assert.Equal("claude-opus-5-5", s.Assistant.Model);
        Assert.False(s.Plugins["agent-tools"].Enabled);   // plugin ids are case-insensitive
    }

    [Fact]
    public void A_secret_that_cant_be_decrypted_loads_as_empty()
    {
        // e.g. settings copied from another Windows user
        var path = NewPath();
        File.WriteAllText(path, """{"assistant": {"anthropicApiKey": "enc:d:bm90IHJlYWxseQ=="}}""");
        Assert.Equal("", new SettingsStore(path).Current.Assistant.AnthropicApiKey);
    }

    [Fact]
    public void Update_saves_and_raises_Changed()
    {
        var path = NewPath();
        var store = new SettingsStore(path);
        int changed = 0;
        store.Changed += () => changed++;
        store.Update(s => s.Household.Location = "Wellington");
        Assert.Equal(1, changed);
        Assert.Equal("Wellington", new SettingsStore(path).Current.Household.Location);
    }
}

/// <summary>SettingsSchema: the form description, masking for the page, and merging the page's values back.</summary>
public sealed class SettingsSchemaTests
{
    static JsonObject Field(JsonObject schema, string key) => schema["fields"]!.AsArray().Select(f => f!.AsObject()).Single(f => f["key"]!.GetValue<string>() == key);

    [Fact]
    public void Describe_lists_the_form_fields_in_order_with_their_types()
    {
        var schema = SettingsSchema.Describe(typeof(DemoSettings));
        var keys = schema["fields"]!.AsArray().Select(f => f!["key"]!.GetValue<string>()).ToList();
        Assert.Equal(["host", "apiKey", "devices", "rooms", "mode", "flavour", "notes", "volume", "enabled", "latitude"], keys);   // hidden ones absent

        Assert.Equal("string", Field(schema, "host")["type"]!.GetValue<string>());
        Assert.Equal("192.168.1.2", Field(schema, "host")["placeholder"]!.GetValue<string>());
        Assert.True(Field(schema, "apiKey")["secret"]!.GetValue<bool>());
        Assert.Equal("From the vendor.", Field(schema, "apiKey")["help"]!.GetValue<string>());
        Assert.Equal("lines", Field(schema, "rooms")["type"]!.GetValue<string>());
        Assert.Equal("select", Field(schema, "mode")["type"]!.GetValue<string>());
        Assert.Equal(["Auto", "Manual"], Field(schema, "mode")["options"]!.AsArray().Select(o => o!.GetValue<string>()));
        Assert.Equal(["sweet", "sour"], Field(schema, "flavour")["options"]!.AsArray().Select(o => o!.GetValue<string>()));
        Assert.Equal("text", Field(schema, "notes")["type"]!.GetValue<string>());
        Assert.True(Field(schema, "notes")["advanced"]!.GetValue<bool>());
        Assert.Equal("number", Field(schema, "volume")["type"]!.GetValue<string>());
        Assert.Equal("bool", Field(schema, "enabled")["type"]!.GetValue<string>());
        Assert.Equal("number", Field(schema, "latitude")["type"]!.GetValue<string>());

        var devices = Field(schema, "devices");
        Assert.Equal("list", devices["type"]!.GetValue<string>());
        var item = devices["item"]!.AsObject();
        Assert.Equal(["name", "password", "port"], item["fields"]!.AsArray().Select(f => f!["key"]!.GetValue<string>()));
        Assert.True(Field(item, "password")["secret"]!.GetValue<bool>());
        Assert.Equal("int", Field(item, "port")["type"]!.GetValue<string>());
    }

    [Fact]
    public void Describe_the_assistant_section()
    {
        var schema = SettingsSchema.Describe(typeof(AssistantSettings));
        Assert.True(Field(schema, "anthropicApiKey")["secret"]!.GetValue<bool>());
        Assert.Equal(["low", "medium", "high"], Field(schema, "effort")["options"]!.AsArray().Select(o => o!.GetValue<string>()));
        Assert.Equal("text", Field(schema, "extraInstructions")["type"]!.GetValue<string>());
        Assert.True(Field(schema, "maxToolRounds")["advanced"]!.GetValue<bool>());
    }

    static DemoSettings Current() => new()
    {
        Host = "10.0.0.5",
        ApiKey = "vendor-key-1",
        RefreshToken = "refresh-xyz",
        AccountName = "Sam",
        Devices =
        [
            new DemoDevice { Name = "Lamp", Password = "lamp-pw", PairingKey = "pair-1", Notes = "couch", Port = 81 },
            new DemoDevice { Name = "Fan", Password = "fan-pw", PairingKey = "pair-2", Notes = "bedroom", Port = 82 },
        ],
    };

    [Fact]
    public void ToUi_masks_secrets_and_never_sends_hidden_values()
    {
        var ui = SettingsSchema.ToUi(Current());
        Assert.Equal("10.0.0.5", ui["host"]!.GetValue<string>());
        Assert.Equal(SettingsSchema.SecretMask, ui["apiKey"]!.GetValue<string>());
        Assert.False(ui.ContainsKey("refreshToken"));
        Assert.False(ui.ContainsKey("accountName"));
        var row = ui["devices"]![0]!.AsObject();
        Assert.Equal("Lamp", row["name"]!.GetValue<string>());
        Assert.Equal(SettingsSchema.SecretMask, row["password"]!.GetValue<string>());
        Assert.False(row.ContainsKey("pairingKey"));
        Assert.False(row.ContainsKey("notes"));
        var text = ui.ToJsonString();
        foreach (var secret in new[] { "vendor-key-1", "refresh-xyz", "lamp-pw", "pair-1", "fan-pw" }) Assert.DoesNotContain(secret, text);

        var empty = SettingsSchema.ToUi(new DemoSettings());
        Assert.Equal("", empty["apiKey"]!.GetValue<string>());   // an empty secret isn't shown as set
    }

    [Fact]
    public void A_puck_record_on_the_page_has_no_token()
    {
        var ui = SettingsSchema.ToUi(new PuckRecord { Id = "a1b2c3d4e5f60708", Name = "Kitchen", Token = "secret-token-value" });
        Assert.DoesNotContain("secret-token-value", ui.ToJsonString());
        Assert.False(ui.ContainsKey("token"));
        Assert.Equal("Kitchen", ui["name"]!.GetValue<string>());
    }

    [Fact]
    public void FromUi_keeps_masked_secrets_and_hidden_values_and_takes_the_rest()
    {
        var ui = SettingsSchema.ToUi(Current());
        ui["host"] = "10.0.0.9";
        ui["mode"] = "Manual";
        var updated = (DemoSettings)SettingsSchema.FromUi(ui, typeof(DemoSettings), Current());
        Assert.Equal("10.0.0.9", updated.Host);
        Assert.Equal(DemoMode.Manual, updated.Mode);
        Assert.Equal("vendor-key-1", updated.ApiKey);
        Assert.Equal("refresh-xyz", updated.RefreshToken);
        Assert.Equal("Sam", updated.AccountName);
    }

    [Fact]
    public void FromUi_takes_a_newly_typed_secret_and_an_emptied_one()
    {
        var ui = SettingsSchema.ToUi(Current());
        ui["apiKey"] = "vendor-key-2";
        Assert.Equal("vendor-key-2", ((DemoSettings)SettingsSchema.FromUi(ui, typeof(DemoSettings), Current())).ApiKey);
        ui["apiKey"] = "";
        Assert.Equal("", ((DemoSettings)SettingsSchema.FromUi(ui, typeof(DemoSettings), Current())).ApiKey);
    }

    [Fact]
    public void FromUi_keeps_each_rows_masked_secret_and_hidden_values()
    {
        var ui = SettingsSchema.ToUi(Current());
        ui["devices"]![1]!["name"] = "Ceiling fan";
        // The page sends rows back from what it was given, plus a new row
        ui["devices"]!.AsArray().Add(new JsonObject { ["name"] = "Heater", ["password"] = "heater-pw" });

        var updated = (DemoSettings)SettingsSchema.FromUi(ui, typeof(DemoSettings), Current());

        Assert.Equal(3, updated.Devices.Count);
        Assert.Equal(("Lamp", "lamp-pw", "pair-1", "couch", 81), (updated.Devices[0].Name, updated.Devices[0].Password, updated.Devices[0].PairingKey, updated.Devices[0].Notes, updated.Devices[0].Port));
        Assert.Equal(("Ceiling fan", "fan-pw", "pair-2", "bedroom"), (updated.Devices[1].Name, updated.Devices[1].Password, updated.Devices[1].PairingKey, updated.Devices[1].Notes));
        Assert.Equal(("Heater", "heater-pw", "", 80), (updated.Devices[2].Name, updated.Devices[2].Password, updated.Devices[2].PairingKey, updated.Devices[2].Port));
    }

    [Fact]
    public void FromUi_ignores_hidden_values_sent_by_the_page()
    {
        var ui = SettingsSchema.ToUi(Current());
        ui["refreshToken"] = SettingsSchema.SecretMask;    // an older page echoing a masked hidden secret
        ui["accountName"] = "someone else";
        ui["devices"]![0]!["pairingKey"] = "evil";
        var updated = (DemoSettings)SettingsSchema.FromUi(ui, typeof(DemoSettings), Current());
        Assert.Equal("refresh-xyz", updated.RefreshToken);
        Assert.Equal("Sam", updated.AccountName);
        Assert.Equal("pair-1", updated.Devices[0].PairingKey);
    }

    [Fact]
    public void FromUi_with_a_partial_body_changes_only_what_was_sent()
    {
        var updated = (AssistantSettings)SettingsSchema.FromUi(new JsonObject { ["model"] = "claude-sonnet-5-5" }, typeof(AssistantSettings),
            new AssistantSettings { AnthropicApiKey = "k", Effort = "high" });
        Assert.Equal(("claude-sonnet-5-5", "k", "high"), (updated.Model, updated.AnthropicApiKey, updated.Effort));
    }
}
