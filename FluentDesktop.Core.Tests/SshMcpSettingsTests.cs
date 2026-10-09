using System.Text.Json.Nodes;
using FluentDesktop.Core.Hub;

namespace FluentDesktop.Core.Tests;

public class SshMcpSettingsTests
{
    [Fact]
    public async Task SavesProfileAndRegistersLocalServerWithoutChangingOtherServers()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var hub = Path.Combine(root, "hub");
        var ssh = Path.Combine(root, "ssh");
        Directory.CreateDirectory(hub);
        Directory.CreateDirectory(Path.Combine(ssh, "build"));
        await File.WriteAllTextAsync(Path.Combine(ssh, "build", "index.js"), "// test fixture");
        var hubFile = Path.Combine(hub, "mcp_settings.json");
        await File.WriteAllTextAsync(hubFile, """{"mcpServers":{"other":{"command":"keep-me"}},"systemConfig":{"routing":{"skipAuth":true}}}""");
        var store = new SshMcpSettingsStore(hub, ssh, Path.Combine(root, "settings", "ssh_mcp.json"));
        var settings = new SshMcpSettings
        {
            Host = "127.0.0.1", Port = 2222, Username = "paul", Password = "quoted\"\\secret",
            SudoPassword = "sudo-secret", KeyPath = "C:\\Users\\paul\\.ssh\\id_ed25519",
            ApprovalPolicy = "auto", HostKeyMode = "insecure"
        };

        await store.SaveAsync(settings);

        Assert.Equal(settings, await store.LoadAsync());
        var document = JsonNode.Parse(await File.ReadAllTextAsync(hubFile))!;
        Assert.Equal("keep-me", document["mcpServers"]!["other"]!["command"]!.GetValue<string>());
        Assert.True(document["systemConfig"]!["routing"]!["skipAuth"]!.GetValue<bool>());
        var entry = document["mcpServers"]!["ssh-mcp"]!;
        Assert.Equal("node", entry["command"]!.GetValue<string>());
        Assert.Equal(Path.Combine(ssh, "build", "index.js"), entry["args"]![0]!.GetValue<string>());
        Assert.Equal("--config=" + store.ProfilePath, entry["args"]![1]!.GetValue<string>());
        Assert.Equal("--hostKeyMode=insecure", entry["args"]![2]!.GetValue<string>());
        Assert.Equal(settings.Password, entry["env"]!["SSH_MCP_PASSWORD"]!.GetValue<string>());
        Assert.Equal(settings.SudoPassword, entry["env"]!["SSH_MCP_SUDO_PASSWORD"]!.GetValue<string>());
        var profile = await File.ReadAllTextAsync(store.ProfilePath);
        Assert.Contains("port = 2222", profile);
        Assert.Contains("auth = \"key\"", profile);
        Assert.DoesNotContain("hostKeyMode =", profile);
        Assert.DoesNotContain(settings.Password, profile);
    }

    [Theory]
    [InlineData("", "paul", 22)]
    [InlineData("localhost", "", 22)]
    [InlineData("localhost", "paul", 0)]
    [InlineData("localhost", "paul", 65536)]
    public async Task RejectsInvalidConnectionBeforeWriting(string host, string user, int port)
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var store = new SshMcpSettingsStore(root, root, Path.Combine(root, "ssh_mcp.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new SshMcpSettings { Host = host, Username = user, Port = port }));
        Assert.False(File.Exists(store.SettingsPath));
    }
}
