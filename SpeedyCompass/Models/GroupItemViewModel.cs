namespace SpeedyCompass.Models;

public class GroupItemViewModel
{
    public string GroupName { get; set; }
    public int MemberCount { get; set; }
    public bool IsMyAdmin { get; set; }
    public string MemberCountDisplay => $"{MemberCount} / 5 Members";

    // Admin can always attempt to re-enter their own group
    public bool CanJoin => IsMyAdmin || MemberCount < 5;

    // Dynamically change the button text
    public string JoinButtonText => IsMyAdmin ? "Enter" : "Join";
}
