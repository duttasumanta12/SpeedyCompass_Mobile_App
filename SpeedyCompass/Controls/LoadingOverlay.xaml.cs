namespace SpeedyCompass.Controls;

public partial class LoadingOverlay : ContentView
{
    public static readonly BindableProperty MessageProperty = BindableProperty.Create(
        nameof(Message), typeof(string), typeof(LoadingOverlay), "Loading...");

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public LoadingOverlay()
    {
        InitializeComponent();
    }

    // Helper methods for incredibly clean C# calls
    public void Show(string message = "Loading...")
    {
        Message = message;
        IsVisible = true;
    }

    public void Hide()
    {
        IsVisible = false;
    }
}