using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using YGORogueDuelistCardModInstaller.Avalonia.Services;
using YGORogueDuelistCardModInstaller.Avalonia.ViewModels;
using YGORogueDuelistCardModInstaller.Avalonia.Views;

namespace YGORogueDuelistCardModInstaller.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var dialogService = new AvaloniaDialogService(window);
            window.DataContext = new MainWindowViewModel(dialogService);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
