// Models/Rider.cs

namespace SpeedyCompass.Models
{
    public class Rider
    {
        public string Name { get; set; }
        public bool IsAdmin { get; set; }
        public string RoleDisplay => IsAdmin ? "Admin" : "Rider";
        public Color RoleColor => IsAdmin ? Colors.DarkOrange : Colors.Gray;
    }
}