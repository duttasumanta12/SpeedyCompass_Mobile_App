namespace SpeedyCompass
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();
            Routing.RegisterRoute("ContactUsPage", typeof(ContactUsPage));
        }
    }
}
