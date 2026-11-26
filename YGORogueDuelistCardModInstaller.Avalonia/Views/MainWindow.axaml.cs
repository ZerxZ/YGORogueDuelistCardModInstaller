using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using YGORogueDuelistCardModInstaller.Avalonia.ViewModels;

namespace YGORogueDuelistCardModInstaller.Avalonia.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _attachedVm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += (_, _) => _attachedVm?.Dispose();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_attachedVm is not null)
        {
            _attachedVm.Logs.CollectionChanged -= OnLogsCollectionChanged;
        }

        _attachedVm = DataContext as MainWindowViewModel;
        if (_attachedVm is not null)
        {
            _attachedVm.Logs.CollectionChanged += OnLogsCollectionChanged;
        }
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.UIThread.Post(() => LogScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
        }
    }
}
