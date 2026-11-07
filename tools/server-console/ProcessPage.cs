using System.ComponentModel;
using System.Windows.Media;

namespace TGame.ServerConsole;

internal sealed class ProcessPage(ServerInstance instance) : INotifyPropertyChanged
{
    public ServerInstance Instance { get; } = instance;
    public string Role => Instance.Role;
    public int? Pid { get; private set; } = instance.Pid;
    public string Status { get; private set; } = instance.Status;
    public string PidLabel => $"PID {Pid?.ToString() ?? "—"}";
    public string Description => $"{Status}  ·  {PidLabel}";
    public Brush Dot => Status switch
    {
        "就绪" => Brushes.SeaGreen,
        "启动中" or "初始化中" => Brushes.SteelBlue,
        "连接不全" or "停止中" => Brushes.DarkGoldenrod,
        "异常退出" => Brushes.IndianRed,
        _ => Brushes.SlateGray
    };
    public bool CanStop => Instance.Alive;
    public long ClearedThrough { get; set; }
    public long? TopLine { get; set; }
    public long? SelectedLine { get; set; }
    public string Keyword { get; set; } = "";
    public bool MatchesOnly { get; set; }
    public bool Following { get; set; } = true;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Refresh()
    {
        bool changedInstance = Instance.Pid != Pid;
        if (changedInstance)
        {
            ClearedThrough = 0;
            TopLine = SelectedLine = null;
        }
        if (changedInstance || Instance.Status != Status)
        {
            Pid = Instance.Pid;
            Status = Instance.Status;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
        return changedInstance;
    }
}
