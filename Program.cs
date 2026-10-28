using System;
using Velopack;

namespace RAM;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must run first: handles the installer/updater hooks (install, update, uninstall) and exits when it does.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
