using System.Diagnostics;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using TGame;
using TGame.ServerConsole;

internal static class PreferenceChecks
{
    public static void Run(string checkout)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                CheckDuplicateDisplayNames(checkout);
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            throw new InvalidOperationException("Console preferences must follow launch slots, not display names.", failure);
        }

        Console.WriteLine("PASS stable preferences with duplicate display names.");
    }

    private static void CheckDuplicateDisplayNames(string checkout)
    {
        var root = Directory.CreateDirectory(Path.Combine(checkout, "preferences")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(root, "tools", "server-console")).FullName;
        File.WriteAllText(Path.Combine(project, "project.json"), """{"environmentName":"设置检查","commands":["Start"]}""");
        var configuration = Path.Combine(root, ServerConfiguration.FileName);
        var counts = Enum.GetValues<ProcessType>().Select(type => $"  {type}: {(type == ProcessType.Router ? 2 : 0)}");
        var connections = Enum.GetValues<ProcessType>().Select(type => $"  {type}: []");
        File.WriteAllText(configuration, """
            environment: preferences
            loggingMinimumLevel: DEBUG
            runDirectory: run
            zooKeeperOptions:
              endpoint: 127.0.0.1:2181
              discoveryRoot: /preferences
              workerRootPath: /preferences-workers
              sessionTimeoutMilliseconds: 10000
              connectTimeoutMilliseconds: 10000
            """ + "\nprocesses:\n" + string.Join("\n", counts) + "\nconnections:\n" + string.Join("\n", connections));

        var previous = Environment.GetEnvironmentVariable(EngineEnvironment.Config);
        Environment.SetEnvironmentVariable(EngineEnvironment.Config, configuration);
        try
        {
            using var session = new ConsoleSession(root);
            using var current = Process.GetCurrentProcess();
            var run = Directory.CreateDirectory(Path.Combine(root, "run")).FullName;
            var ticks = current.StartTime.ToUniversalTime().Ticks;
            var key = ServerRunFiles.ProcessKey(current.Id, ticks);
            File.WriteAllText(Path.Combine(run, "Router1.json"), JsonSerializer.Serialize(new { Pid = current.Id, StartedUtcTicks = ticks }));
            File.WriteAllText(Path.Combine(run, ServerRunFiles.IdentityPrefix + key), "Router2");
            session.Instances[0].Open(run, run, run);

            var path = Path.Combine(root, "user.json");
            var settings = UserSettings.Load(path);
            settings.SelectedSlotName = "Router2";
            settings.Logs["Router1"] = new("first slot", true, false);
            settings.Logs["Router2"] = new("second slot", false, true);
            var window = new MainWindow(session, settings) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            var tabs = (ListBox)window.FindName("ProcessTabs");
            var pages = tabs.Items.Cast<ProcessPage>().ToArray();
            var selected = tabs.SelectedItem;
            Exception? dispatcherFailure = null;
            window.Dispatcher.UnhandledException += (_, args) =>
            {
                dispatcherFailure = args.Exception;
                args.Handled = true;
            };
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            window.Close();
            if (dispatcherFailure != null)
            {
                throw new InvalidOperationException("Saving console preferences failed.", dispatcherFailure);
            }

            var restored = UserSettings.Load(path);
            if (pages.Any(page => page.Name != "Router2") ||
                pages[0].Keyword != "first slot" ||
                pages[1].Keyword != "second slot" ||
                selected != pages[1] ||
                restored.SelectedSlotName != "Router2" ||
                restored.Logs["Router1"].Keyword != "first slot" ||
                restored.Logs["Router2"].Keyword != "second slot")
            {
                throw new InvalidOperationException("Duplicate display names mixed up saved or restored slot preferences.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(EngineEnvironment.Config, previous);
        }
    }
}
