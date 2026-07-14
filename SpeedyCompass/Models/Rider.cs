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
        public bool IsOnline { get; internal set; } = true;
    }
}