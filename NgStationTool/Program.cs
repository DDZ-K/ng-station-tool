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
                            var code = SelfTest.Run();
                            Environment.Exit(code);
                            return;
                        }
                        if (args.Any(a => string.Equals(a, "--sim-serial", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        var code = SerialOverlapSim.Run();
                                        Environment.Exit(code);
                                        return;
                                    }
                                    if (args.Any(a => string.Equals(a, "--sim-delayed-wait", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        var code = DelayedWaitSim.Run(args);
                                        Environment.Exit(code);
                                        return;
                                    }

            using var mutex = new Mutex(true, MutexName, out var created);
            if (!created)
            {
                MessageBox.Show("工位工具已在运行（单实例）。", "NgStationTool",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) =>
            {
                try
                {
                    MessageBox.Show("界面异常: " + e.Exception.Message, "NgStationTool",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { /* ignore */ }
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try
                {
                    var ex = e.ExceptionObject as Exception;
                    MessageBox.Show("严重异常: " + (ex?.Message ?? e.ExceptionObject?.ToString()),
                        "NgStationTool", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { /* ignore */ }
            };

            Application.Run(new MainForm());
        }
    }
