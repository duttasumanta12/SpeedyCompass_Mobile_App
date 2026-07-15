// Models/Rider.cs

namespace SpeedyCompass.Models
{
    public class Rider
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
    }
}