namespace SpeedyCompass.Services;

public class AppTierService
{
    // Toggle this to TRUE in the future to turn on the paid Google features
    public bool IsProTierEnabled { get; set; } = false;

    // Feature Flags - Easy to mix and match later!
    public bool UseImmersiveTbt => IsProTierEnabled;
    public bool UseLiveTraffic => IsProTierEnabled;
    public bool UseGoogleRoutesApi => IsProTierEnabled;
    public bool UseAlgorithmicMeetups => IsProTierEnabled;

    // Free Tier Features
    public bool UseMapboxOverview => !UseGoogleRoutesApi;
    public bool UseStraightLineSpiderwebs => !UseGoogleRoutesApi;
    public bool UsePttVoice => IsProTierEnabled;
}