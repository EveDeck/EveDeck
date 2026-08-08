using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using EveDeck.Services;
using EveDeck.ViewModels;

namespace EveDeck.Views;

public partial class ChangelogWindow : Window
{
    private readonly ChangelogViewModel _vm;
    private readonly Action? _onInstallUpdate;

    public ChangelogWindow(string? availableUpdateVersion, Action? onInstallUpdate, LogService? log)
    {
        InitializeComponent();
        _vm = new ChangelogViewModel(availableUpdateVersion, log);
        _onInstallUpdate = onInstallUpdate;
        DataContext = _vm;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        FitToWorkArea();
    }

    // Same fixed-dialog-on-a-small/scaled-screen safety net as SetupWizardWindow -- see that class
    // for the full rationale (Microsoft Store certification policy 10.1.2.10).
    private void FitToWorkArea()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == 0) return;
            var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea; // physical pixels
            var src = PresentationSource.FromVisual(this);
            double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            if (dpiX <= 0) dpiX = 1;
            if (dpiY <= 0) dpiY = 1;

            double workW = wa.Width / dpiX, workH = wa.Height / dpiY;
            double waLeft = wa.Left / dpiX, waTop = wa.Top / dpiY;

            if (Width > workW) Width = workW;
            if (Height > workH) Height = workH;

            if (Left < waLeft) Left = waLeft;
            if (Top < waTop) Top = waTop;
            if (Left + Width > waLeft + workW) Left = waLeft + workW - Width;
            if (Top + Height > waTop + workH) Top = waTop + workH - Height;
        }
        catch { /* if the screen can't be resolved, leave the declared size */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _onInstallUpdate?.Invoke();
    }

    private void ViewAll_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://github.com/EveDeck/EveDeck/releases");

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* best-effort; nothing sensible to do if the shell can't open a browser */ }
    }
}
