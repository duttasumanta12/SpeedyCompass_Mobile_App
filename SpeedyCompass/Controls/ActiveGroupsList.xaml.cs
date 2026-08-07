using System.Collections;
using SpeedyCompass.Shared.Models; // Ensure this matches your namespace for GroupItemViewModel

namespace SpeedyCompass.Controls;

public partial class ActiveGroupsList : ContentView
{
    // Define the list data property so MainPage can pass data into this component
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

    // Custom Events for the Parent Page
    public event EventHandler RefreshRequested;
    public event EventHandler<GroupItemViewModel> JoinClicked;
    public event EventHandler<string> DeleteClicked;

    public ActiveGroupsList()
    {
        InitializeComponent();
    }

    // Helper to stop the loading spinner
    public void EndRefresh()
    {
        GroupsRefreshView.IsRefreshing = false;
    }

    // --- Internal Event Routing ---
    private void OnRefreshRequested(object sender, EventArgs e)
    {
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
}