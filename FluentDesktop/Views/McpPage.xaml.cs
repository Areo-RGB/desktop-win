using System.Text.Json.Nodes;
using FluentDesktop.Core.Mcp;
using FluentDesktop.Helpers;
using FluentDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentDesktop.Views;

public sealed partial class McpPage : Page
{
    public McpPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.EnsureLoadedAsync();
    }

    public McpViewModel ViewModel { get; } = Services.Mcp;

    private static T? TagOf<T>(object sender) where T : class => (sender as FrameworkElement)?.Tag as T;

    private async void Import_Click(object sender, RoutedEventArgs e) => await ViewModel.ImportAsync();

    private async void Rewrite_Click(object sender, RoutedEventArgs e) => await ViewModel.RewriteAsync();

    private void CopyAll_Click(object sender, RoutedEventArgs e) => ClipboardHelper.Copy(ViewModel.ExportJson(), "mcpServers JSON");

    private async void Column_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<McpColumn>(sender) is { } column) await ViewModel.ToggleColumnAsync(column.App);
    }

    private async void Cell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.Tag is McpCell cell) await ViewModel.ToggleAsync(cell, box.IsChecked == true);
    }

    private void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<McpRow>(sender) is { } row && ViewModel.RowJson(row.Id) is { } json) ClipboardHelper.Copy(json, $"“{row.Id}”");
    }

    private async void DisableAll_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<McpRow>(sender) is { } row) await ViewModel.DisableEverywhereAsync(row.Id);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<McpRow>(sender) is not { } row) return;
        if (await Dialogs.ConfirmAsync($"Delete “{row.Id}”?",
                "The server is removed from every app config it's enabled in, then from this list.", "Delete", danger: true))
        {
            await ViewModel.DeleteAsync(row.Id);
        }
    }

    private async void Add_Click(SplitButton sender, SplitButtonClickEventArgs args) => await ShowFormAsync(null);

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<McpRow>(sender) is { } row && ViewModel.Get(row.Id) is { } server) await ShowFormAsync(server);
    }

    /// <summary>Add/edit form, mirroring DesktopCC's: template, paste, transport fields, targets, metadata.</summary>
    private async Task ShowFormAsync(McpServer? editing)
    {
        var spec = editing?.Server ?? new JsonObject { ["type"] = "stdio" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 560 };

        var template = new ComboBox { Header = "Start from a template", PlaceholderText = "Choose a template…", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var p in McpPreset.All) template.Items.Add($"{p.Name} — {p.Description}");
        var paste = new TextBox { Header = "Paste a config", PlaceholderText = "mcpServers JSON, a {\"name\": …} map, OpenCode mcp, or Codex [mcp_servers.x] TOML", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
        var applyPaste = new Button { Content = "Apply paste" };
        var pasteMsg = new TextBlock { Style = (Style)Application.Current.Resources["SecondaryCaptionStyle"], TextWrapping = TextWrapping.Wrap };

        var transport = new ComboBox { Header = "Transport", ItemsSource = new[] { "Local command (stdio)", "HTTP (streamable)", "SSE" }, MinWidth = 220 };
        var name = new TextBox { Header = "Name *", PlaceholderText = "e.g. fetch", Text = editing?.Id ?? "", IsEnabled = editing == null };
        var command = new TextBox { Header = "Command *", PlaceholderText = "npx, uvx, node…" };
        var args = new TextBox { Header = "Arguments (one per line)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56 };
        var env = new TextBox { Header = "Environment variables (KEY=value per line)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56 };
        var cwd = new TextBox { Header = "Working directory" };
        var url = new TextBox { Header = "URL *", PlaceholderText = "https://mcp.example.com/mcp" };
        var headers = new TextBox { Header = "Headers (Name=value per line)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56 };
        var description = new TextBox { Header = "Description", Text = editing?.Description ?? "" };
        var tags = new TextBox { Header = "Tags (comma separated)", Text = string.Join(", ", editing?.Tags ?? []) };
        var homepage = new TextBox { Header = "Homepage", Text = editing?.Homepage ?? "" };
        var docs = new TextBox { Header = "Docs", Text = editing?.Docs ?? "" };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };

        var apps = new Dictionary<McpApp, CheckBox>();
        var appsPanel = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 130 };
        foreach (var app in McpAppInfo.All)
        {
            var box = new CheckBox { Content = app.Label(), IsChecked = editing?.Apps.Get(app) ?? app == McpApp.Claude };
            ToolTipService.SetToolTip(box, app.ConfigNote());
            apps[app] = box;
            appsPanel.Children.Add(box);
        }

        var stdioFields = new StackPanel { Spacing = 10, Children = { command, args, env, cwd } };
        var urlFields = new StackPanel { Spacing = 10, Children = { url, headers } };
        var extra = new JsonObject();

        void Fill(JsonObject s)
        {
            var t = McpSpec.TransportOf(s);
            transport.SelectedIndex = (int)t;
            command.Text = s.Str("command") ?? "";
            args.Text = s["args"] is JsonArray a ? string.Join("\n", a.OfType<JsonValue>().Select(v => v.ToString())) : "";
            env.Text = Pairs(s["env"]);
            cwd.Text = s.Str("cwd") ?? "";
            url.Text = s.Str("url") ?? "";
            headers.Text = Pairs(s["headers"]);
            extra = s.CloneObject();
            foreach (var k in new[] { "type", "command", "args", "env", "cwd", "url", "headers", "http_headers" }) extra.Remove(k);
        }

        void SyncTransport()
        {
            var stdio = transport.SelectedIndex <= 0;
            stdioFields.Visibility = stdio ? Visibility.Visible : Visibility.Collapsed;
            urlFields.Visibility = stdio ? Visibility.Collapsed : Visibility.Visible;
        }

        Fill(spec);
        SyncTransport();
        transport.SelectionChanged += (_, _) => SyncTransport();
        template.SelectionChanged += (_, _) =>
        {
            if (template.SelectedIndex < 0) return;
            var preset = McpPreset.All[template.SelectedIndex];
            Fill(preset.Spec);
            if (editing == null) name.Text = preset.Name;
            description.Text = preset.Description;
            tags.Text = string.Join(", ", preset.Tags);
            homepage.Text = preset.Homepage;
            docs.Text = preset.Docs;
        };
        applyPaste.Click += (_, _) =>
        {
            try
            {
                if (McpPaste.Recognize(paste.Text) is not { } doc) { pasteMsg.Text = "Nothing to apply"; return; }
                var first = doc.Items[0];
                Fill(first.Spec);
                if (editing == null && first.Name.Length > 0) name.Text = first.Name;
                pasteMsg.Text = doc.Items.Count == 1
                    ? $"Recognized {doc.Format}"
                    : $"Recognized {doc.Format}: {doc.Items.Count} servers, filled in “{first.Name}”; save and paste again for the rest";
            }
            catch (McpException ex)
            {
                pasteMsg.Text = ex.Message;
            }
        };

        var pasteRow = new StackPanel { Spacing = 6, Children = { paste, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { applyPaste, pasteMsg } } } };
        pasteMsg.VerticalAlignment = VerticalAlignment.Center;
        if (editing == null) { panel.Children.Add(template); panel.Children.Add(pasteRow); }
        panel.Children.Add(transport);
        panel.Children.Add(name);
        panel.Children.Add(stdioFields);
        panel.Children.Add(urlFields);
        panel.Children.Add(new TextBlock { Text = "Write to", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        panel.Children.Add(appsPanel);
        panel.Children.Add(new Expander
        {
            Header = "Description, tags and links",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 10, Children = { description, tags, homepage, docs } },
        });
        panel.Children.Add(error);

        var dialog = Dialogs.Create(editing == null ? "Add MCP server" : $"Edit “{editing.Id}”",
            new ScrollViewer { Content = panel, MaxHeight = 620, Padding = new Thickness(0, 0, 16, 0) },
            editing == null ? "Add" : "Save");
        McpServer? result = null;
        dialog.PrimaryButtonClick += (_, a) =>
        {
            try
            {
                result = Build();
                error.IsOpen = false;
            }
            catch (McpException ex)
            {
                error.Message = ex.Message;
                error.IsOpen = true;
                a.Cancel = true;
            }
        };

        McpServer Build()
        {
            var id = name.Text.Trim();
            var s = extra.CloneObject();
            if (transport.SelectedIndex <= 0)
            {
                s["type"] = "stdio";
                s["command"] = command.Text.Trim();
                var argList = args.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                if (argList.Count > 0) s["args"] = new JsonArray(argList.Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
                if (ParsePairs(env.Text, "Environment") is { } e) s["env"] = e;
                if (cwd.Text.Trim().Length > 0) s["cwd"] = cwd.Text.Trim();
            }
            else
            {
                s["type"] = transport.SelectedIndex == 1 ? "http" : "sse";
                s["url"] = url.Text.Trim();
                if (ParsePairs(headers.Text, "Headers") is { } h) s["headers"] = h;
            }
            McpValidate.Server(id, s, editing == null, ViewModel.ExistingIds);
            var flags = new McpApps();
            foreach (var (app, box) in apps)
            {
                flags.Set(app, box.IsChecked == true);
                if (box.IsChecked == true)
                {
                    try { McpValidate.AppSupport(app, s); }
                    catch (McpException ex) { throw new McpException($"{app.Label()}: {ex.Message}"); }
                }
            }
            string? Opt(TextBox b) => b.Text.Trim() is { Length: > 0 } v ? v : null;
            var tagList = tags.Text.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            return new McpServer
            {
                Id = id, Name = id, Server = s, Apps = flags,
                Description = Opt(description), Homepage = Opt(homepage), Docs = Opt(docs),
                Tags = tagList.Count > 0 ? tagList : null,
            };
        }

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && result != null)
        {
            await ViewModel.SaveAsync(result);
        }
    }

    private static string Pairs(JsonNode? node) =>
        node is JsonObject obj ? string.Join("\n", obj.Select(kv => $"{kv.Key}={kv.Value?.ToString()}")) : "";

    private static JsonObject? ParsePairs(string text, string label)
    {
        var map = new JsonObject();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) throw new McpException($"{label} line {i + 1}: expected KEY=value");
            var key = line[..eq].Trim();
            if (key.Length == 0) throw new McpException($"{label} line {i + 1}: empty key");
            map[key] = line[(eq + 1)..].Trim();
        }
        return map.Count == 0 ? null : map;
    }
}
