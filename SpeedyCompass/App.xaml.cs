using Microsoft.Extensions.DependencyInjection;
using SpeedyCompass.Services;

namespace SpeedyCompass
{
    public partial class App : Application
    {
        private readonly RideStateService rideStateService;

        public App(RideStateService rideStateService)
        {
            InitializeComponent();
            this.rideStateService = rideStateService;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell());

            // Fires when the app goes to the background or the screen locks
            window.Deactivated += (sender, e) =>
            {
                rideStateService.RunningInBackground = true;
            };

            // Fires when the app returns to the foreground
            window.Activated += (sender, e) =>
            {
                rideStateService.RunningInBackground = false;
            };

            return window;
        }
    }
}