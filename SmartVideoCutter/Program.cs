using SmartVideoCutter.Views;

namespace SmartVideoCutter
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetColorMode(SystemColorMode.System);

            ApplicationConfiguration.Initialize();

            Application.Run(new MainForm());
        }
    }
}
