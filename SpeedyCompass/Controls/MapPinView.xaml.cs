namespace SpeedyCompass.Controls;

public partial class MapPinView : ContentView
{
    // --- BindableProperties ---

    public static readonly BindableProperty UsernameProperty =
        BindableProperty.Create(nameof(Username), typeof(string), typeof(MapPinView), default(string));

    public static readonly BindableProperty SpeedProperty =
        BindableProperty.Create(nameof(Speed), typeof(string), typeof(MapPinView), default(string));

    public static readonly BindableProperty PinColorProperty =
        BindableProperty.Create(nameof(PinColor), typeof(Color), typeof(MapPinView), Colors.DodgerBlue);

    // --- Properties ---

    public string Username
    {
        get => (string)GetValue(UsernameProperty);
        set => SetValue(UsernameProperty, value);
    }

    public string Speed
    {
        get => (string)GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    public Color PinColor
    {
        get => (Color)GetValue(PinColorProperty);
        set => SetValue(PinColorProperty, value);
    }

    public MapPinView()
    {
        InitializeComponent();
    }
}