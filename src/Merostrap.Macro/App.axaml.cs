using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Merostrap.Macros;
using Merostrap.UI.Elements.Macros;

namespace Merostrap;

public sealed partial class App : Application
{
    internal static bool SmokeTest { get; set; }
    private MacroWorkspace? _workspace;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            _workspace = new MacroWorkspace(Path.Combine(StandalonePaths.ConfigRoot, "Macros.json"),
                engineFactory: SmokeTest ? () => new MacroEngine(new InactiveInput()) : null,
                singleInstance: !SmokeTest);
            desktop.Exit += (_, _) => { _workspace?.Dispose(); _workspace = null; };
            _workspace.OpenWindow();
            desktop.MainWindow = desktop.Windows.OfType<MacroWindow>().FirstOrDefault();
            // A second instance asks the existing owner to show its window, then exits.
            if (desktop.MainWindow is null) desktop.Shutdown();
            if (SmokeTest)
                Dispatcher.UIThread.Post(() => desktop.Shutdown(desktop.MainWindow?.IsVisible == true ? 0 : 1),
                    DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Startup checks load the real UI without installing hooks or sending input.
    private sealed class InactiveInput : IMacroInput
    {
        public event Action<MacroInputEvent>? Input { add { } remove { } }
        public bool IsAvailable => false;
        public bool IsForegroundRoblox => false;
        public string PrimaryButton => "mouse1";
        public (int Width, int Height) ScreenSize => (1920, 1080);
        public void Start(Func<MacroInputEvent, bool> suppress) { }
        public void Stop() { }
        public void Key(string key, bool down) => throw new InvalidOperationException("Inactive input.");
        public void Move(int x, int y) => throw new InvalidOperationException("Inactive input.");
        public void Character(char character) => throw new InvalidOperationException("Inactive input.");
        public void Dispose() { }
    }
}
