using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Clowd.Localization;
using FluentAvalonia.UI.Controls;
using PropertyChanged.SourceGenerator;
using ReactiveUI;

namespace Clowd.Avalonia.ViewModels;

internal partial class AppUpdateViewModel : CultureAwareViewModel
{
    public ReactiveCommand<Unit, Unit> PrimaryActionCommand { get; }

    public bool ProgressIndeterminate => Working && ProgressPercent < 1;

    public string Title => Working ? Strings.SettingsUpdate_CheckingForUpdates : LocalVersionReady != null
        ? Strings.SettingsUpdate_UpdateAvailableTitle : Strings.SettingsUpdate_UpToDate;

    public string Description => Working ? Strings.SettingsUpdate_Working(ProgressPercent) : LocalVersionReady != null
        ? Strings.SettingsUpdate_UpdateAvailableDesc(LocalVersionReady) : Strings.SettingsUpdate_NoAvailableUpdates;

    public string ActionButtonText => LocalVersionReady != null ? Strings.SettingsUpdate_RestartBtn : Strings.SettingsUpdate_CheckUpdatesBtn;

    public Symbol IconSymbol => Working ? Symbol.CloudSync : LocalVersionReady != null ? Symbol.CloudBackupFilled : Symbol.CloudSyncComplete;

    [Notify(Setter.Private)] private int _progressPercent;
    [Notify(Setter.Private)] private string _localVersionReady;
    [Notify(Setter.Private)] private bool _working;

    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

    public AppUpdateViewModel()
    {
        PrimaryActionCommand = ReactiveCommand.Create(PrimaryActionExecuted, this.WhenAnyValue(x => x.Working).Select(x => !x));
    }

    private async void PrimaryActionExecuted()
    {
        await _semaphore.WaitAsync();
        try
        {
            Working = true;
            await Task.Delay(2000);

            for (int i = 0; i < 100; i++)
            {
                ProgressPercent = i;
                await Task.Delay(20);
            }

            LocalVersionReady = "3.4.710";
        }
        finally
        {
            _semaphore.Release();
            Working = false;
            ProgressPercent = 0;
        }
    }
}
