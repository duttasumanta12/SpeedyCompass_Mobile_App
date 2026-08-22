// Models/Rider.cs

using SpeedyCompass.Shared.Constants;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SpeedyCompass.Models
{
    public class Rider : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string GoogleId { get; set; } // Needed for targeting
        public bool IsAdmin { get; set; }
        public string Role { get; set; } = RiderRole.Rider.ToString(); // Lead, Tail, Marshal, Rider

        public string RoleDisplay => IsAdmin ? RiderRole.Admin.ToString() : Role;

        // Dynamically color code the badges
        public Color RoleColor
        {
            get
            {
                if (IsAdmin) return Colors.DodgerBlue;

                var parsedRole = RiderRoleParser.ParseOrDefault(Role);
                return parsedRole switch
                {
                    RiderRole.Lead => Colors.MediumSeaGreen,
                    RiderRole.Tail => Colors.DarkOrange,
                    RiderRole.Marshal => Colors.DarkOrchid,
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

        private string _statusStr = TelemetryStatus.Nearby;
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