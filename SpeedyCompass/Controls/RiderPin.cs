using Microsoft.Maui.Controls.Maps;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;

namespace SpeedyCompass.Controls;

public class RiderPin : System.ComponentModel.INotifyPropertyChanged
{
    public static readonly BindableProperty UsernameProperty = BindableProperty.Create(nameof(Username), typeof(string), typeof(RiderPin), string.Empty);
    public static readonly BindableProperty SpeedProperty = BindableProperty.Create(nameof(Speed), typeof(string), typeof(RiderPin), string.Empty);

    // NEW: We pass the persistent random color to the handler!
    public static readonly BindableProperty PinColorProperty = BindableProperty.Create(nameof(PinColor), typeof(Color), typeof(RiderPin), Colors.Blue);
    private string username;
    private string speed;
    private Color pinColor;
    private string imageSource = string.Empty;
    private Location location;

    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }

    public string Username { get => username; set { username = value; OnPropertyChanged();  } }
    public string Speed { get => speed; set { speed = value; OnPropertyChanged(); } }
    public Color PinColor { get => pinColor; set { pinColor = value; OnPropertyChanged(); } }
    public string ImageSource { get => imageSource; set { imageSource = value; OnPropertyChanged(); } }
    public ICommand ClickedCommand { get; set; }
    public float ZIndex { get; set; }
    private double _heading;
    private bool _isFirstHeading = true; // Ensures the map rotates immediately on launch

    public double Heading
    {
        get => _heading;
        set
        {
            // 1. Calculate the shortest angular difference (handling the 359 to 1 degree wrap-around)
            double diff = Math.Abs(_heading - value);
            if (diff > 180.0) diff = 360.0 - diff;

            // 2. Only update if the turn is > 3 degrees, OR if it's the very first location fix
            if (diff > 3.0 || _isFirstHeading)
            {
                _heading = value;
                OnPropertyChanged(); // This now ONLY fires when it actually matters
                _isFirstHeading = false;
            }
        }
    }
    private bool _isAutoCentering = true;
    public bool IsAutoCentering
    {
        get => _isAutoCentering;
        set { _isAutoCentering = value; OnPropertyChanged(); }
    }
    public Location Location { get => location; set { location = value; OnPropertyChanged(); } }
    public RiderPin(Action<RiderPin> clicked)
    {
        ClickedCommand = new Command(() => clicked(this));
    }
}
// Defining the CustomMap so XAML can find <controls:CustomMap>
public class CustomMap : Microsoft.Maui.Controls.Maps.Map
{
    public ObservableCollection<RiderPin> CustomPins
    {
        get => (ObservableCollection<RiderPin>)GetValue(CustomPinsProperty);
        set => SetValue(CustomPinsProperty, value);
    }

    public static readonly BindableProperty CustomPinsProperty =
        BindableProperty.Create(
            nameof(CustomPins),
            typeof(ObservableCollection<RiderPin>),
            typeof(CustomMap),
            new ObservableCollection<RiderPin>());
}
