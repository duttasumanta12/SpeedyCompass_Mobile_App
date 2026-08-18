using System.Collections;
using SpeedyCompass.Shared.Models;

namespace SpeedyCompass.Controls;

public partial class ActiveGroupsList : ContentView
{
    // ==========================================
    // 1. DATA SOURCE
    // ==========================================
    public static readonly BindableProperty ItemsSourceProperty = BindableProperty.Create(
        propertyName: nameof(ItemsSource),
        returnType: typeof(IEnumerable),
        declaringType: typeof(ActiveGroupsList),
        defaultValue: null);

    public IEnumerable ItemsSource
    {
        get => (IEnumerable)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    // ==========================================
    // 2. UI STATE PROPERTIES (Bindable)
    // ==========================================
    public static readonly BindableProperty IsLoadingProperty = BindableProperty.Create(
        propertyName: nameof(IsLoading), returnType: typeof(bool), declaringType: typeof(ActiveGroupsList), defaultValue: false);

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public static readonly BindableProperty IsLoadingMoreProperty = BindableProperty.Create(
        propertyName: nameof(IsLoadingMore), returnType: typeof(bool), declaringType: typeof(ActiveGroupsList), defaultValue: false);

    public bool IsLoadingMore
    {
        get => (bool)GetValue(IsLoadingMoreProperty);
        set => SetValue(IsLoadingMoreProperty, value);
    }

    public static readonly BindableProperty EmptyMessageProperty = BindableProperty.Create(
        propertyName: nameof(EmptyMessage), returnType: typeof(string), declaringType: typeof(ActiveGroupsList), defaultValue: "No active convoys right now.");

    public string EmptyMessage
    {
        get => (string)GetValue(EmptyMessageProperty);
        set => SetValue(EmptyMessageProperty, value);
    }

    // ==========================================
    // 3. EVENTS
    // ==========================================
    public event EventHandler RefreshRequested;
    public event EventHandler LoadMoreRequested;
    public event EventHandler<string> SearchQueryChanged;
    public event EventHandler<GroupItemViewModel> JoinClicked;
    public event EventHandler<string> DeleteClicked;

    private CancellationTokenSource _searchDebounceCts;

    public ActiveGroupsList()
    {
        InitializeComponent();
    }

    public void EndRefresh()
    {
        GroupsRefreshView.IsRefreshing = false;
        GlobalLoadingOverlay.Hide();
    }

    // ==========================================
    // 4. EVENT HANDLERS
    // ==========================================
    private void OnRefreshRequested(object sender, EventArgs e)
    {
        GlobalLoadingOverlay.Show("Loading groups...");
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnJoinGroupClicked(object sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is GroupItemViewModel groupData)
        {
            JoinClicked?.Invoke(this, groupData);
        }
    }

    private void OnDeleteGroupClicked(object sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is string groupName)
        {
            DeleteClicked?.Invoke(this, groupName);
        }
    }

    // --- Search Handler (With Debounce) ---
    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        // Cancel the previous timer if the user keeps typing
        _searchDebounceCts?.Cancel();
        _searchDebounceCts = new CancellationTokenSource();
        var token = _searchDebounceCts.Token;

        try
        {
            // Wait 300ms. If they type another letter within this time, this task is cancelled.
            await Task.Delay(300, token);

            if (!token.IsCancellationRequested)
            {
                GlobalLoadingOverlay.Show("Loading groups...");
                SearchQueryChanged?.Invoke(this, e.NewTextValue);
            }
        }
        catch (TaskCanceledException) { /* Ignored */ }
    }

    // --- Pagination Handler ---
    private void OnRemainingItemsThresholdReached(object sender, EventArgs e)
    {
        // Prevent firing "Load More" multiple times concurrently, or if we are already doing a full reload
        if (IsLoading || IsLoadingMore) return;

        LoadMoreRequested?.Invoke(this, EventArgs.Empty);
    }
}