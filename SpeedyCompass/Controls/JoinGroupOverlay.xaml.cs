namespace SpeedyCompass.Controls;

public class JoinGroupEventArgs : EventArgs
{
    public string GroupName { get; set; }
    public string PinCode { get; set; }
}

public partial class JoinGroupOverlay : ContentView
{
    public event EventHandler<JoinGroupEventArgs> JoinConfirmed;
    private string _targetGroupName;

    public JoinGroupOverlay()
    {
        InitializeComponent();
    }

    public void Show(string groupName)
    {
        _targetGroupName = groupName;
        JoinPinEntry.Text = string.Empty;
        IsVisible = true;

        // UX Polish: Auto-focus the keyboard
        JoinPinEntry.Focus();
    }

    public void Hide() => IsVisible = false;

    private async void OnConfirmClicked(object sender, EventArgs e)
    {
        string pinCode = JoinPinEntry.Text?.Trim();
        if (string.IsNullOrEmpty(pinCode) || pinCode.Length != 6)
        {
            if (Application.Current?.MainPage != null)
                await Application.Current.MainPage.DisplayAlert("Invalid PIN", "Please enter the full 6-digit code provided by the Admin.", "OK");
            return;
        }

        Hide();

        JoinConfirmed?.Invoke(this, new JoinGroupEventArgs
        {
            GroupName = _targetGroupName,
            PinCode = pinCode
        });
    }

    private void OnCancelClicked(object sender, EventArgs e) => Hide();
}