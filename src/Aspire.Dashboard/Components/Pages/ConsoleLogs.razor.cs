// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Pages;

public sealed partial class ConsoleLogs : ComponentBase, IAsyncDisposable, IPageWithSessionAndUrlState<ConsoleLogs.ConsoleLogsViewModel, ConsoleLogs.ConsoleLogsPageState>
{
    [Inject]
    public required IResourceService ResourceService { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required ProtectedSessionStorage SessionStorage { get; set; }

    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    [Parameter]
    public string? ResourceName { get; set; }

    private readonly TaskCompletionSource _whenDomReady = new();
    private readonly CancellationTokenSource _resourceSubscriptionCancellation = new();
    private readonly CancellationSeries _logSubscriptionCancellationSeries = new();
    private readonly ConcurrentDictionary<string, ResourceViewModel> _resourceByName = new(StringComparers.ResourceName);
    private List<Option<string>>? _resources;

    // UI
    private FluentSelect<Option<string>>? _resourceSelectComponent;
    private Option<string> _noSelection = null!;
    private LogViewer _logViewer = null!;

    // State
    public ConsoleLogsViewModel ViewModel { get; set; } = null!;

    public string BasePath => "ConsoleLogs";
    public string SessionStorageKey => "ConsoleLogs_PageState";

    protected override void OnInitialized()
    {
        _noSelection = new() { Value = null, Text = Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsSelectAResource)] };

        TrackResources();

        void TrackResources()
        {
            var (snapshot, subscription) = ResourceService.SubscribeResources();

            foreach (var resource in snapshot)
            {
                var added = _resourceByName.TryAdd(resource.Name, resource);
                Debug.Assert(added, "Should not receive duplicate resources in initial snapshot data.");
            }

            UpdateResourcesList();

            _ = Task.Run(async () =>
            {
                await foreach (var (changeType, resource) in subscription.WithCancellation(_resourceSubscriptionCancellation.Token))
                {
                    await OnResourceChanged(changeType, resource);
                }
            });
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        await this.InitializeViewModelAsync(hasComponentRendered: false);

        if (ViewModel.SelectedResource is not null)
        {
            await LoadLogsAsync();
        }
        else
        {
            await StopWatchingLogsAsync();
            await ClearLogsAsync();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Let anyone waiting know that the render is complete, so we have access to the underlying log viewer.
            _whenDomReady.SetResult();

            await this.InitializeViewModelAsync(hasComponentRendered: true);
        }
    }

    private void UpdateResourcesList()
    {
        _resources ??= new(_resourceByName.Count + 1);
        _resources.Clear();
        _resources.Add(_noSelection);
        _resources.AddRange(_resourceByName.Values
            .OrderBy(c => c.Name)
            .Select(ToOption));

        Option<string> ToOption(ResourceViewModel resource)
        {
            return new Option<string>
            {
                Value = resource.Name,
                Text = GetDisplayText()
            };

            string GetDisplayText()
            {
                var resourceName = ResourceViewModel.GetResourceName(resource, _resourceByName.Values);

                return resource.State switch
                {
                    null or { Length: 0 } => $"{resourceName} ({Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsUnknownState)]})",
                    "Running" => resourceName,
                    _ => $"{resourceName} ({resource.State})"
                };
            }
        }
    }

    private Task ClearLogsAsync()
    {
        return _logViewer is not null ? _logViewer.ClearLogsAsync() : Task.CompletedTask;
    }

    private async ValueTask LoadLogsAsync()
    {
        // Wait for the first render to complete so that the log viewer is available
        await _whenDomReady.Task;

        if (ViewModel.SelectedResource is null)
        {
            ViewModel.Status = Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsNoResourceSelected)];
        }
        else if (_logViewer is null)
        {
            ViewModel.Status = Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsInitializingLogViewer)];
        }
        else
        {
            var cancellationToken = await _logSubscriptionCancellationSeries.NextAsync();

            var subscription = ResourceService.SubscribeConsoleLogs(ViewModel.SelectedResource.Name, cancellationToken);

            if (subscription is not null)
            {
                var task = _logViewer.SetLogSourceAsync(
                    subscription,
                    convertTimestampsFromUtc: ViewModel.SelectedResource is ContainerViewModel);

                ViewModel.InitialisedSuccessfully = true;
                ViewModel.Status = Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsWatchingLogs)];

                // Indicate when logs finish (other than by cancellation).
                _ = task.ContinueWith(
                    _ => ViewModel.Status = Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsFinishedWatchingLogs)],
                    CancellationToken.None,
                    TaskContinuationOptions.NotOnCanceled,
                    TaskScheduler.Current);
            }
            else
            {
                ViewModel.InitialisedSuccessfully = false;
                ViewModel.Status = Loc[ViewModel.SelectedResource is ContainerViewModel
                    ? nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsFailedToInitialize)
                    : nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsLogsNotYetAvailable)];
            }
        }
    }

    private async Task HandleSelectedOptionChangedAsync()
    {
        await StopWatchingLogsAsync();
        await ClearLogsAsync();

        await this.AfterViewModelChangedAsync();
    }

    private async Task OnResourceChanged(ResourceChangeType changeType, ResourceViewModel resource)
    {
        if (changeType == ResourceChangeType.Upsert)
        {
            _resourceByName[resource.Name] = resource;

            if (string.Equals(ViewModel.SelectedResource?.Name, resource.Name, StringComparison.Ordinal))
            {
                // The selected resource was updated
                ViewModel.SelectedResource = resource;

                if (ViewModel.InitialisedSuccessfully is false)
                {
                    await LoadLogsAsync();
                }
                else if (!string.Equals(ViewModel.SelectedResource.State, "Running", StringComparison.Ordinal))
                {
                    ViewModel.Status = Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsFinishedWatchingLogs)];
                }
            }
        }
        else if (changeType == ResourceChangeType.Delete)
        {
            var removed = _resourceByName.TryRemove(resource.Name, out _);
            Debug.Assert(removed, "Cannot remove unknown resource.");

            if (string.Equals(ViewModel.SelectedResource?.Name, resource.Name, StringComparison.Ordinal))
            {
                // The selected resource was deleted
                ViewModel.SelectedOption = _noSelection;
                await HandleSelectedOptionChangedAsync();
            }
        }

        UpdateResourcesList();

        await InvokeAsync(StateHasChanged);

        // Workaround for issue in fluent-select web component where the display value of the
        // selected item doesn't update automatically when the item changes
        if (_resourceSelectComponent is not null && JS is not null)
        {
            await JS.InvokeVoidAsync("updateFluentSelectDisplayValue", _resourceSelectComponent.Element);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _resourceSubscriptionCancellation.CancelAsync();
        _resourceSubscriptionCancellation.Dispose();

        await StopWatchingLogsAsync();

        if (_logViewer is { } logViewer)
        {
            await logViewer.DisposeAsync();
        }
    }

    private async Task StopWatchingLogsAsync()
    {
        await _logSubscriptionCancellationSeries.ClearAsync();
    }

    public class ConsoleLogsViewModel
    {
        public required string Status { get; set; }
        public Option<string>? SelectedOption { get; set; }
        public required ResourceViewModel? SelectedResource { get; set; }
        public bool? InitialisedSuccessfully { get; set; }
    }

    public class ConsoleLogsPageState
    {
        public string? SelectedResource { get; set; }
    }

    public ConsoleLogsViewModel GetViewModelFromQuery()
    {
        if (_resources is not null && ResourceName is not null)
        {
            var selectedOption = _resources.FirstOrDefault(c => string.Equals(ResourceName, c.Value, StringComparisons.ResourceName)) ?? _noSelection;

            return new ConsoleLogsViewModel
            {
                SelectedOption = selectedOption,
                SelectedResource = selectedOption?.Value is null ? null : _resourceByName[selectedOption.Value],
                Status = Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsLogsNotYetAvailable)]
            };
        }
        else
        {
            return new ConsoleLogsViewModel
            {
                SelectedOption = _noSelection,
                SelectedResource = null,
                Status = Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsNoResourceSelected)]
            };
        }
    }

    public (string Path, Dictionary<string, string?>? QueryParameters) GetUrlFromSerializableViewModel(ConsoleLogsPageState serializable)
    {
        NavigationManager.NavigateTo($"/ConsoleLogs/{ViewModel.SelectedOption?.Value}");

        if (ViewModel.SelectedOption?.Value is { } selectedOption)
        {
            return ($"{BasePath}/{selectedOption}", null);
        }

        return ($"/{BasePath}", null);
    }

    public ConsoleLogsPageState ConvertViewModelToSerializable()
    {
        return new ConsoleLogsPageState
        {
            SelectedResource = ViewModel.SelectedResource?.Name
        };
    }
}
