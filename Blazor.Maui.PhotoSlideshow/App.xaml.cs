namespace Blazor.Maui.PhotoSlideshow
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            MainPage = new MainPage();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = base.CreateWindow(activationState);

#if WINDOWS
        window.Width = 1920;
        window.Height = 1080;
        window.MinimumWidth = 800;
        window.MinimumHeight = 600;
#endif

            return window;
            return new Window(new MainPage()) { Title = "PhotoSlideshow" };
        }
    }
}
