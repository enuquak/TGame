using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace TGame.ServerConsole;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is not ([_] or [_, "--check"]))
        {
            Console.Error.WriteLine("Usage: TGame.ServerConsole <repository> [--check]");
            return 2;
        }

        bool check = args.Length == 2;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[0]));
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
            using var mutex = new Mutex(true, @"Local\TGame.ServerConsole-" + key, out var first);
            if (!first)
            {
                const string message = "这个工作目录的服务器控制台已打开。";
                if (check)
                {
                    Console.Error.WriteLine(message);
                }
                else
                {
                    MessageBox.Show(message, "服务器控制台");
                }

                return 1;
            }

            var app = new Application();
            using var session = new ConsoleSession(root);
            var preferences = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TGame", "ServerConsole", key, "user.json");
            var window = new MainWindow(session, UserSettings.Load(preferences));
            if (!check)
            {
                return app.Run(window);
            }

            session.Refresh();
            window.Measure(new Size(1280, 800));
            window.Arrange(new Rect(0, 0, 1280, 800));
            Console.WriteLine($"PASS 服务器控制台已加载，{session.Instances.Count} 个进程页签。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            if (!check)
            {
                MessageBox.Show(ex.Message, "服务器控制台", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return 1;
        }
    }
}
