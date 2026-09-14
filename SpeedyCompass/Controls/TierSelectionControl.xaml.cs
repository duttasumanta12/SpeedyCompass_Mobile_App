using System.Collections.ObjectModel;
using SpeedyCompass.Models;

namespace SpeedyCompass.Controls;

public partial class TierSelectionControl : ContentView
{
    public ObservableCollection<AppTierInfo> AvailableTiers { get; set; } = new();

    // Expose the selected tier so the ProfilePage can read it
    public AppTierInfo SelectedTier { get; private set; }

    public TierSelectionControl()
    {
        InitializeComponent();
        LoadTiers();
        TiersCarousel.ItemsSource = AvailableTiers;
    }

    private void LoadTiers()
    {
        // 1. FREE TIER (Enabled)
        var freeTier = new AppTierInfo
        {
            Id = "tier_free",
            Name = "Basic Rider",
            PriceText = "Free Forever",
            Description = "Essential tools for casual group rides.",
            ThemeColor = "#2E7D32", // Forest Green
            Icon = "motorcycle",
            BadgeText = "CURRENT",
            IsAvailable = true,
            IsSelected = true,
            Features = new List<string>
            {
                "Up to 6 riders per Convoy",
                "Standard map tracking & routing",
                "Basic Crash Detection",
                "Straight-line meetup convergence",
                "Groups automatically expire after 3 days" // Explicitly mentioned constraint
            }
        };

        SelectedTier = freeTier;

        // 2. PRO TIER (Disabled / Coming Soon)
        var proTier = new AppTierInfo
        {
            Id = "tier_pro",
            Name = "Pro Convoy",
            PriceText = "$4.99 / month",
            Description = "Professional tools for touring and moto-vlogging.",
            ThemeColor = "#FBC02D", // Gold
            Icon = "local_police",
            BadgeText = "PREMIUM",
            IsAvailable = false, // Locks the UI card
            IsSelected = false,
            Features = new List<string>
            {
                "Unlimited riders per Convoy",
                "Real-time PTT Walkie-Talkie Mesh",
                "Algorithmic safe meetup routing",
                "Permanent groups (No auto-delete)",
                "Live weather radar & traffic alerts",
                "Voice Copilot Turn-by-Turn"
            }
        };

        AvailableTiers.Add(freeTier);
        AvailableTiers.Add(proTier);
    }

    private void OnTierCardTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is AppTierInfo tappedTier)
        {
            if (!tappedTier.IsAvailable)
            {
                // Play a tiny vibration to indicate it's locked
                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(50));
                return;
            }

            // Deselect all
            foreach (var tier in AvailableTiers)
            {
                tier.IsSelected = false;
            }

            // Select the tapped one
            tappedTier.IsSelected = true;
            SelectedTier = tappedTier;

            // HACK: Force UI refresh of the CollectionView
            TiersCarousel.ItemsSource = null;
            TiersCarousel.ItemsSource = AvailableTiers;
        }
    }
}