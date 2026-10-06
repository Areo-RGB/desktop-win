using FluentDesktop.Core.GitHub;
using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Helpers;
using FluentDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluentDesktop.Views;

public sealed partial class GithubPage : Page
{
    private static readonly string[] GitignoreTemplates =
    [
        "None", "Flutter", "Dart", "Python", "Node", "Go", "Rust", "Java", "C++", "C", "Swift", "Kotlin", "Ruby",
        "Terraform", "VisualStudio", "macOS", "Linux", "Windows",
    ];

    private static readonly (string Label, string Value)[] LicenseTemplates =
    [
        ("None", "none"), ("MIT", "mit"), ("Apache 2.0", "apache-2.0"), ("GPL 3.0", "gpl-3.0"),
        ("BSD 3-Clause", "bsd-3-clause"), ("MPL 2.0", "mpl-2.0"), ("Unlicense", "unlicense"),
    ];

    private bool _busyCreate;
    private bool _busyConnect;

    public GithubPage()
    {
        InitializeComponent();
        CreateGitignore.ItemsSource = GitignoreTemplates;
        CreateGitignore.SelectedIndex = 0;
        CreateLicense.ItemsSource = LicenseTemplates.Select(l => l.Label).ToList();
        CreateLicense.SelectedIndex = 0;
        ActualThemeChanged += (_, _) => UpdateLogo();
        Loaded += async (_, _) =>
        {
            UpdateLogo();
            await ViewModel.InitAsync();
            UpdateButtons();
            UpdateRemoteHelp();
        };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ViewModel.IsAuthed) or nameof(ViewModel.IsOperating)) UpdateButtons();
            if (e.PropertyName == nameof(ViewModel.TotalCount)) UpdateRemoteHelp();
        };
    }

    public GithubViewModel ViewModel { get; } = Services.Github;

    private void UpdateLogo() =>
        GithubLogo.Source = Images.FromAsset(ActualTheme == ElementTheme.Dark ? "Assets/GitHub_Invertocat_White.png" : "Assets/GitHub_Invertocat_Black.png");

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (CreatePanel == null) return; // raised while InitializeComponent is still building the page
        var index = sender.Items.IndexOf(sender.SelectedItem);
        ReposPanel.Visibility = Bind.Visible(index == 0);
        CreatePanel.Visibility = Bind.Visible(index == 1);
    }

    // ── Repositories ────────────────────────────────────────────

    private async void ReloadCached_Click(object sender, RoutedEventArgs e) => await ViewModel.ReloadLocalAsync();

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.FetchRemoteAsync();
        Services.Notifier.Show(ViewModel.StatusMessage);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ViewModel.SetSelectAll(SelectAllBox.IsChecked == true);

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => ViewModel.ClearSelection();

    private void RepoCheck_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GithubRepoItem item) ViewModel.ToggleSelect(item);
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GithubRepoItem { Repo.Url.Length: > 0 } item) ProcessRunner.Open(item.Repo.Url);
    }

    private void Page_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PageButton { IsEllipsis: false } p) ViewModel.GoToPage(p.Index);
    }

    private void FirstPage_Click(object sender, RoutedEventArgs e) => ViewModel.GoToPage(0);

    private void PrevPage_Click(object sender, RoutedEventArgs e) => ViewModel.GoToPage(ViewModel.CurrentPage - 1);

    private void NextPage_Click(object sender, RoutedEventArgs e) => ViewModel.GoToPage(ViewModel.CurrentPage + 1);

    private void LastPage_Click(object sender, RoutedEventArgs e) => ViewModel.GoToPage(ViewModel.TotalPages - 1);

    private async void Batch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag || !Enum.TryParse<GithubBatchAction>(tag, out var action)) return;
        var names = ViewModel.SelectedNames.ToList();
        if (names.Count == 0) return;
        var danger = action == GithubBatchAction.Delete;

        var content = new StackPanel { Spacing = 8, MinWidth = 420 };
        content.Children.Add(new TextBlock
        {
            Text = $"Are you sure you want to run \"{action.Label()}\" on {names.Count} selected repository(ies)?",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        if (danger)
        {
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
                Message = "Permanent deletion cannot be undone. This will delete the repositories on GitHub!",
            });
        }
        content.Children.Add(new TextBlock { Text = "Selected repositories:" });
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 140,
            Content = new TextBlock
            {
                Text = string.Join('\n', names.Select(n => $"• {n}")),
                FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
                FontSize = 12,
            },
        });

        var confirm = Dialogs.Create($"Confirm {action.Label()}", content, $"Run {action.Label()}", danger: danger);
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunBatchAsync(action, names.Count);
        await ViewModel.FetchRemoteAsync();
    }

    private async Task RunBatchAsync(GithubBatchAction action, int total)
    {
        var status = new TextBlock { Text = $"Processing 0 of {total}..." };
        var current = new TextBlock { FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold };
        var bar = new ProgressBar { Maximum = Math.Max(1, total) };
        var log = new StackPanel { Spacing = 2 };
        var panel = new StackPanel { Spacing = 8, Width = 500 };
        panel.Children.Add(status);
        panel.Children.Add(current);
        panel.Children.Add(bar);
        panel.Children.Add(new TextBlock { Text = "Execution Log:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new Border
        {
            Height = 160,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(4),
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Child = new ScrollViewer { Content = log },
        });

        var dialog = Dialogs.Create($"Running {action.Label()}", panel, "Close & Refresh", close: "");
        dialog.IsPrimaryButtonEnabled = false;
        var shown = dialog.ShowAsync();

        await foreach (var progress in ViewModel.ExecuteBatch(action))
        {
            status.Text = $"Processing {progress.CurrentIndex} of {progress.TotalCount}...";
            current.Text = progress.IsCompleted ? "" : $"Current: {progress.CurrentRepo}";
            bar.Value = progress.CurrentIndex;
            if (progress.Results.Count > log.Children.Count)
            {
                var r = progress.Results[^1];
                log.Children.Add(new TextBlock
                {
                    Text = $"{(r.Success ? "✓" : "✗")} {r.NameWithOwner}: {r.Message}",
                    FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = r.Success ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] : Bind.Danger,
                });
            }
        }
        dialog.IsPrimaryButtonEnabled = true;
        await shown;
    }

    // ── Create & Connect ────────────────────────────────────────

    private void UpdateButtons()
    {
        var authed = ViewModel.IsAuthed;
        CreateButton.IsEnabled = authed && !_busyCreate && !ViewModel.IsOperating;
        ConnectButton.IsEnabled = authed && !_busyConnect && !ViewModel.IsOperating;
        CreateRing.IsActive = _busyCreate;
        ConnectRing.IsActive = _busyConnect;
        CreateButtonText.Text = _busyCreate ? "Creating…" : "Create repository";
        ConnectButtonText.Text = _busyConnect ? "Connecting…" : ConnectMode.SelectedIndex == 0 ? "Link & push" : "Create from folder";
        GitInitButton.IsEnabled = !ViewModel.IsOperating;
    }

    private void UpdateRemoteHelp()
    {
        var count = ViewModel.AllRepos.Count;
        ExistingRemote.PlaceholderText = count == 0 ? "e.g. octocat/hello-world" : $"Search {count} repos, newest first…";
        ExistingRemoteHelp.Text = count == 0
            ? "No cached repos — Sync from GitHub on the Repositories tab, or type owner/repo manually."
            : $"{count} repos • newest first • type to filter, or type a custom owner/repo";
    }

    private static string? ValidateRepoName(string value)
    {
        var name = value.Trim();
        if (name.Length == 0) return "Name is required";
        return GithubRules.IsValidRepoName(name) ? null : "Letters, digits, \".\", \"-\" or \"_\" only (max 100, no leading/trailing \".\")";
    }

    private static void ShowResult(InfoBar bar, TextBlock logs, GithubOperationResult result)
    {
        bar.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        bar.Title = result.Message;
        logs.Text = string.Join('\n', result.Logs);
        logs.Visibility = Bind.Visible(result.Logs.Count > 0);
        bar.IsOpen = true;
    }

    private void CreateClone_Click(object sender, RoutedEventArgs e) => CloneDirRow.Visibility = Bind.Visible(CreateClone.IsChecked == true);

    private async void BrowseCloneDir_Click(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFolderAsync() is { } path) CloneDir.Text = path;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (ValidateRepoName(CreateName.Text) is { } error)
        {
            Services.Notifier.Error(error);
            return;
        }
        _busyCreate = true;
        CreateResult.IsOpen = false;
        UpdateButtons();
        var license = LicenseTemplates[Math.Max(0, CreateLicense.SelectedIndex)].Value;
        var gitignore = CreateGitignore.SelectedItem as string ?? "None";
        var request = new CreateRepoRequest(
            CreateName.Text.Trim(),
            CreateDesc.Text.Trim(),
            IsPrivate: CreateVisibility.SelectedIndex == 1,
            AddReadme: CreateReadme.IsChecked == true,
            GitignoreTemplate: gitignore == "None" ? null : gitignore,
            LicenseTemplate: license == "none" ? null : license,
            CloneAfterCreate: CreateClone.IsChecked == true,
            CloneDirectory: CloneDir.Text.Trim().Length == 0 ? null : CloneDir.Text.Trim());
        var result = await ViewModel.CreateRepoAsync(request);
        _busyCreate = false;
        UpdateButtons();
        ShowResult(CreateResult, CreateResultLogs, result);
        Services.Notifier.Show(result.Message, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (result.Success) CreateName.Text = "";
    }

    private async void BrowseLocal_Click(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFolderAsync() is not { } path) return;
        LocalPath.Text = path;
        await InspectAsync();
    }

    private async void Inspect_Click(object sender, RoutedEventArgs e) => await InspectAsync();

    private async void LocalPath_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) await InspectAsync();
    }

    private async Task InspectAsync()
    {
        var path = LocalPath.Text.Trim();
        if (path.Length == 0)
        {
            Services.Notifier.Error("Pick a local folder first.");
            return;
        }
        InspectButton.IsEnabled = false;
        LocalInfo.IsOpen = false;
        var info = await ViewModel.InspectLocalDirAsync(path);
        InspectButton.IsEnabled = true;

        GitInitButton.Visibility = Visibility.Collapsed;
        if (!info.Exists)
        {
            LocalInfo.Severity = InfoBarSeverity.Error;
            LocalInfo.Message = "Directory does not exist.";
        }
        else if (!info.IsGitRepo)
        {
            LocalInfo.Severity = InfoBarSeverity.Warning;
            LocalInfo.Message = "Not a git repo yet — init git here, or connecting will run `git init` for you.";
            GitInitButton.Visibility = Visibility.Visible;
        }
        else
        {
            var parts = new List<string> { "Git repo" };
            if (info.Branch != null) parts.Add(info.Branch);
            if (info.HasUncommittedChanges) parts.Add("uncommitted changes");
            parts.Add(info.RemoteMap.Count == 0
                ? "no remotes"
                : string.Join(", ", info.RemoteMap.Select(r => $"{r.Key} → {GithubRules.ShortRemote(r.Value)}")));
            LocalInfo.Severity = InfoBarSeverity.Success;
            LocalInfo.Message = string.Join(" • ", parts);
        }
        LocalInfo.IsOpen = true;
    }

    private async void GitInit_Click(object sender, RoutedEventArgs e)
    {
        var path = LocalPath.Text.Trim();
        if (path.Length == 0)
        {
            Services.Notifier.Error("Pick a local folder first.");
            return;
        }
        GitInitButton.IsEnabled = false;
        var result = await ViewModel.InitLocalRepoAsync(path);
        GitInitButton.IsEnabled = true;
        Services.Notifier.Show(result.Message, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (result.Success) await InspectAsync();
    }

    private void ConnectMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExistingRemote == null) return; // fires during InitializeComponent
        var existing = ConnectMode.SelectedIndex == 0;
        ExistingRemote.Visibility = Bind.Visible(existing);
        ExistingRemoteHelp.Visibility = Bind.Visible(existing);
        FreshPanel.Visibility = Bind.Visible(!existing);
        UpdateButtons();
    }

    private void ExistingRemote_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var query = sender.Text.Trim();
        var matches = ViewModel.AllRepos
            .OrderByDescending(r => r.PushedAt).ThenByDescending(r => r.UpdatedAt)
            .Where(r => query.Length == 0 ||
                r.NameWithOwner.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(100)
            .Select(r => new GithubRepoItem(r))
            .ToList();
        sender.ItemsSource = matches;
    }

    private void ExistingRemote_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is GithubRepoItem item) sender.Text = item.NameWithOwner;
    }

    private async void ExistingRemote_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => await ConnectAsync();

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async Task ConnectAsync()
    {
        var path = LocalPath.Text.Trim();
        if (path.Length == 0)
        {
            Services.Notifier.Error("Pick a local folder first.");
            return;
        }
        var existing = ConnectMode.SelectedIndex == 0;
        if (existing)
        {
            var remote = ExistingRemote.Text.Trim();
            if (remote.Length == 0 || !remote.Contains('/'))
            {
                Services.Notifier.Error("Enter the existing remote as \"owner/repo\".");
                return;
            }
        }
        else if (ValidateRepoName(FreshName.Text) is { } error)
        {
            Services.Notifier.Error(error);
            return;
        }

        _busyConnect = true;
        ConnectResult.IsOpen = false;
        UpdateButtons();
        var request = new ConnectLocalRequest(
            path,
            ExistingRemoteName: existing ? ExistingRemote.Text.Trim() : null,
            NewRepoName: existing ? null : FreshName.Text.Trim(),
            NewRepoPrivate: FreshVisibility.SelectedIndex == 1,
            PushAfterConnect: PushAfter.IsChecked == true,
            RemoteName: RemoteName.Text.Trim().Length == 0 ? "origin" : RemoteName.Text.Trim(),
            Branch: Branch.Text.Trim().Length == 0 ? null : Branch.Text.Trim());
        var result = await ViewModel.ConnectAsync(request);
        _busyConnect = false;
        UpdateButtons();
        ShowResult(ConnectResult, ConnectResultLogs, result);
        Services.Notifier.Show(result.Message, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (result.Success) await InspectAsync();
    }
}

public static class GithubBind
{
    public static Brush AuthBrush(bool authed) => authed ? Bind.Success : Bind.Warning;

    public static string AuthGlyph(bool authed) => authed ? "" : "";

    public static string AuthLabel(bool authed) => authed ? "gh CLI Authed" : "gh CLI Not Authed";

    public static Style PageButtonStyle(bool current) =>
        (Style)Application.Current.Resources[current ? "AccentButtonStyle" : "DefaultButtonStyle"];
}
