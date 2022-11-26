using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using DynamicData.Binding;
using FluentAvalonia.UI.Controls;
using PropertyChanged.SourceGenerator;
using ReactiveUI;

namespace Clowd.Avalonia.ViewModels;

internal partial class AppUpdateViewModel
{
    public ReactiveCommand<Unit, Unit> PrimaryActionCommand { get; }

    public bool ProgressIndeterminate => Working && ProgressPercent < 1;

    public string Title => Working ? "Checking for updates..." : LocalVersionReady != null ? "Update ready to be installed" : "You're up to date";

    public string Description => Working ? $"Working {ProgressPercent}%" : LocalVersionReady != null ? $"v{LocalVersionReady} will be installed when you restart" : "No updates are available";

    public string ActionButtonText => LocalVersionReady != null ? "Restart" : "Check for Updates";

    public Symbol IconSymbol => Working ? Symbol.CloudSync : LocalVersionReady != null ? Symbol.CloudBackupFilled : Symbol.CloudSyncComplete;

    [Notify] private int _progressPercent;
    [Notify] private string _localVersionReady;
    [Notify] private bool _working;

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
