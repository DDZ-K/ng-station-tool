using System.Threading;

namespace NgStationTool;

internal static class Program
{
    private const string MutexName = "Global\\NgStationTool_SingleInstance_v1";

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(SelfTest.Run());
            return;
        }

        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            MessageBox.Show("工位工具已在运行（单实例）。", "NgStationTool", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
