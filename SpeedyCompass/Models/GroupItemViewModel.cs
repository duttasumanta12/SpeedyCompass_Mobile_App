namespace SpeedyCompass;

public class GroupItemViewModel
{
    public string GroupName { get; set; }
    public int MemberCount { get; set; }
    public int MaxGroupSize { get; set; }
    public bool IsMyAdmin { get; set; }
    public bool IsMember { get; set; }

    public string MemberCountDisplay => $"{MemberCount} / {MaxGroupSize} Riders";
    public bool CanJoin => IsMyAdmin || IsMember || MemberCount < MaxGroupSize;

    public string JoinButtonText => IsMyAdmin ? "Resume" : (IsMember ? "Enter" : "Join");
    public Color JoinButtonColor => IsMyAdmin || IsMember ? Colors.DodgerBlue : Colors.MediumSeaGreen;
}
