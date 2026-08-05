// Models/Rider.cs

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SpeedyCompass.Models
{
    public class Rider : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string GoogleId { get; set; } // Needed for targeting
        public bool IsAdmin { get; set; }
        public string Role { get; set; } // Lead, Tail, Marshal, Rider
                                         // Dynamically set the badge label
        public string RoleDisplay => IsAdmin ? "Admin" : Role;

        // Dynamically color code the badges
        public Color RoleColor
        {
            get
            {
                if (IsAdmin) return Colors.DodgerBlue;
                return Role switch
                {
                    "Lead" => Colors.MediumSeaGreen,
                    "Tail" => Colors.DarkOrange,
                    "Marshal" => Colors.DarkOrchid,
                    _ => Colors.Gray // Default Rider
                };
            }
        }
        public bool IsOnline { get; set; }
        // NEW: Dynamic background color based on Online Status and Device Theme
        public Color CardBackgroundColor
        {
            get
            {
                bool isDark = Application.Current?.RequestedTheme == AppTheme.Dark;

                if (IsOnline)
                {
                    // Standard colors for active riders
                    return isDark ? Color.FromArgb("#2C2C2C") : Color.FromArgb("#F9F9F9");
                }
                else
                {
                    // Faded red/danger tint for offline riders
                    return isDark ? Color.FromArgb("#4A2A2A") : Color.FromArgb("#FFEEEE");
                }
            }
        }
        // NEW: Real-time Telemetry Properties
        private string _speedStr = "Standby";
        public string SpeedStr
        {
            get => _speedStr;
            set { _speedStr = value; OnPropertyChanged(); }
        }
        // Add these right below your SpeedStr property
        private string _statusStr = "Nearby";
        public string StatusStr
        {
            get => _statusStr;
            set { _statusStr = value; OnPropertyChanged(); }
        }
        private Color _statusColor = Colors.MediumSeaGreen;
        public Color StatusColor
        {
            get => _statusColor;
            set { _statusColor = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}