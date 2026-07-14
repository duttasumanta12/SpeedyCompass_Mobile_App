namespace SpeedyCompass.Models;

public class ActiveGroupDto
{
    public string GroupName { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public string AdminGoogleId { get; set; } = string.Empty;
    public bool IsNavigating { get; set; }
    // NEW: Expose the max size to the Dashboard
    public int MaxGroupSize { get; set; }
}
