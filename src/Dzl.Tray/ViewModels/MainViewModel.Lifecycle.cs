using CommunityToolkit.Mvvm.Input;
using Dzl.Core.App;

namespace Dzl.Tray.ViewModels;

public partial class MainViewModel
{
    // --- Server / client ops (background; call the tray's LauncherService) -

    private void RunOp(Action op) => Task.Run(() =>
    {
        try { op(); }
        catch (Exception ex) { _dispatcher.BeginInvoke(() => System.Windows.MessageBox.Show(ex.Message, "DayZ Labs — operation failed")); }
        finally { _dispatcher.BeginInvoke(() => _ = RefreshStatusAsync()); }
    });

    private void RunLifecycleOp(Func<OpResult> op) => Task.Run(() =>
    {
        try
        {
            var result = op();
            if (!result.Ok) _dispatcher.BeginInvoke(() => System.Windows.MessageBox.Show(result.Message, "DayZ Labs — operation failed"));
        }
        catch (Exception ex) { _dispatcher.BeginInvoke(() => System.Windows.MessageBox.Show(ex.Message, "DayZ Labs — operation failed")); }
        finally { _dispatcher.BeginInvoke(() => _ = RefreshStatusAsync()); }
    });

    // Buttons follow the live state (Start disabled while up, Stop/Restart while down). The
    // status poll lags ~1.5s behind reality, so LauncherService's AlreadyUpGuard still backstops
    // a double-click in that window.

    [RelayCommand(CanExecute = nameof(CanStartServer))]
    private void StartServer() => RunLifecycleOp(() => _svc.StartTarget("server", Mode));
    private bool CanStartServer() => !ServerUp && !ServerOwnershipUnknown;

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private void StopServer() => RunLifecycleOp(() => _svc.StopTarget("server"));
    private bool CanStopServer() => ServerUp;

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private void RestartServer() => RunLifecycleOp(() => _svc.RestartTarget("server", Mode));

    [RelayCommand(CanExecute = nameof(CanStartClient))]
    private void StartClient() => RunLifecycleOp(() => _svc.StartTarget("client", Mode));
    private bool CanStartClient() => !ClientUp && !ClientOwnershipUnknown;

    /// <summary>Start the client without <c>-connect</c>: mods + mission load, the game
    /// stays in the main menu (diag boots the mission offline) instead of auto-joining.</summary>
    [RelayCommand(CanExecute = nameof(CanStartClient))]
    private void StartClientNoConnect() => RunLifecycleOp(() => _svc.StartTarget("client", Mode, connect: false));

    [RelayCommand(CanExecute = nameof(CanStopClient))]
    private void StopClient() => RunLifecycleOp(() => _svc.StopTarget("client"));
    private bool CanStopClient() => ClientUp;

    [RelayCommand(CanExecute = nameof(CanStopClient))]
    private void RestartClient() => RunLifecycleOp(() => _svc.RestartTarget("client", Mode));

    partial void OnServerUpChanged(bool value)
    {
        StartServerCommand.NotifyCanExecuteChanged();
        StopServerCommand.NotifyCanExecuteChanged();
        RestartServerCommand.NotifyCanExecuteChanged();
    }

    partial void OnServerOwnershipUnknownChanged(bool value) => StartServerCommand.NotifyCanExecuteChanged();
    partial void OnClientOwnershipUnknownChanged(bool value) => StartClientCommand.NotifyCanExecuteChanged();

    partial void OnClientUpChanged(bool value)
    {
        StartClientCommand.NotifyCanExecuteChanged();
        StopClientCommand.NotifyCanExecuteChanged();
        RestartClientCommand.NotifyCanExecuteChanged();
        StartClientNoConnectCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ToggleMode()
    {
        Mode = Mode == "debug" ? "normal" : "debug";
        Persist();
    }
}
