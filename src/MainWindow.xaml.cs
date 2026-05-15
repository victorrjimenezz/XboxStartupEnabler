using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace XboxStartupEnabler;

public partial class MainWindow : Window
{
    private static readonly Brush BrushOriginal = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA5));
    private static readonly Brush BrushPatched  = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush BrushUnknown  = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush BrushNeutral  = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x90));

    public sealed class SiteVm
    {
        public string Description { get; init; } = "";
        public string StateLabel { get; init; } = "";
        public Brush StateBrush { get; init; } = BrushNeutral;
    }

    public sealed class TargetVm
    {
        public string FileName { get; init; } = "";
        public string SubLine { get; init; } = "";
        public string StateLabel { get; init; } = "";
        public Brush StateBrush { get; init; } = BrushNeutral;
        public ObservableCollection<SiteVm> Sites { get; } = new();
    }

    public ObservableCollection<TargetVm> Targets { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        TargetsList.ItemsSource = Targets;
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            // Re-center on the final size, deferred to Background priority so the layout pass has completed by the time we read ActualWidth/ActualHeight.
            await Dispatcher.InvokeAsync(CenterOnWorkArea,
                System.Windows.Threading.DispatcherPriority.Background);
        };
    }

    private void CenterOnWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width  - ActualWidth ) / 2;
        Top  = area.Top  + (area.Height - ActualHeight) / 2;
    }

    // Handlers

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (!RequireElevation("apply")) return;
        var r = MessageBox.Show(this,
            "This will modify protected Windows system files (gamemode.dll, SettingsHandlers_Gaming.dll, and " +
            "twinui.pcshell.dll). Originals will be backed up automatically.\n\n" +
            "Proceed?",
            "Apply patches",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;

        await RunOperationAsync("Applying patches", () => Patcher.Apply(null, Log));
        await RefreshAsync();
        if (Targets.All(t => t.StateLabel == "PATCHED"))
        {
            MessageBox.Show(this,
                "Patches applied successfully.\n\n" +
                "Close and reopen the Settings app and the Xbox app, or reboot, " +
                "then go to Settings → Gaming and look for \"Choose your home app\".",
                "Done", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (!RequireElevation("restore")) return;
        var r = MessageBox.Show(this,
            "Restore the original Windows DLLs from the most recent backup?",
            "Restore originals",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;

        await RunOperationAsync("Restoring originals", () => Patcher.Restore(null, Log));
        await RefreshAsync();
    }

    // Inspection

    private async Task RefreshAsync()
    {
        SetBusy(true);
        Log("Refreshing status...");
        try
        {
            var summary = await Task.Run(() => Patcher.Inspect());
            Targets.Clear();
            foreach (var t in summary.Targets)
            {
                var vm = new TargetVm
                {
                    FileName = t.FileName,
                    SubLine = t.Exists
                        ? $"{t.Version}    {t.Size:N0} bytes"
                        : "missing on this system",
                    StateLabel = SummarizeState(t),
                    StateBrush = SummarizeBrush(t),
                };
                foreach (var s in t.Sites)
                    vm.Sites.Add(new SiteVm
                    {
                        Description = s.Description,
                        StateLabel = StateLabel(s.State),
                        StateBrush = StateBrush(s.State),
                    });
                Targets.Add(vm);
            }
            UpdateBanner(summary);
            Log($"Status: original={summary.Original} patched={summary.Patched} unknown={summary.Unknown}");
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            StatusHeadline.Text = "Couldn't inspect system files";
            StatusDetail.Text = ex.Message;
            StatusDot.Fill = BrushUnknown;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateBanner(SystemSummary summary)
    {
        ElevationBadge.Visibility = Patcher.IsElevated() ? Visibility.Collapsed : Visibility.Visible;

        if (!summary.AllExist)
        {
            StatusHeadline.Text = "One or more system DLLs are missing";
            StatusDetail.Text = "This patcher targets Windows 11 build 26100+. Your install may be older or unusual.";
            StatusDot.Fill = BrushUnknown;
            ApplyBtn.IsEnabled = false;
            RestoreBtn.IsEnabled = false;
            return;
        }

        if (summary.Unknown > 0)
        {
            StatusHeadline.Text = "DLL layout doesn't match a known build";
            StatusDetail.Text = $"{summary.Unknown} site(s) couldn't be identified. A Windows update may have changed the binaries — patching is disabled.";
            StatusDot.Fill = BrushUnknown;
            ApplyBtn.IsEnabled = false;
            RestoreBtn.IsEnabled = true;
            return;
        }

        if (summary.AllPatched)
        {
            StatusHeadline.Text = "Patched — \"Choose your home app\" is unlocked";
            StatusDetail.Text = "Reboot or restart the Settings + Xbox apps if the option still isn't showing.";
            StatusDot.Fill = BrushPatched;
            ApplyBtn.IsEnabled = false;
            RestoreBtn.IsEnabled = true;
            ApplyBtn.Content = "Already applied";
        }
        else if (summary.Patched > 0)
        {
            StatusHeadline.Text = $"Partially patched ({summary.Patched}/{summary.Total} sites)";
            StatusDetail.Text = "Click Apply to finish the patch.";
            StatusDot.Fill = BrushNeutral;
            ApplyBtn.IsEnabled = true;
            RestoreBtn.IsEnabled = true;
            ApplyBtn.Content = "Apply patches";
        }
        else
        {
            StatusHeadline.Text = "Not patched";
            StatusDetail.Text = "Click Apply to patch the DLLs and unlock the option.";
            StatusDot.Fill = BrushNeutral;
            ApplyBtn.IsEnabled = true;
            RestoreBtn.IsEnabled = false;
            ApplyBtn.Content = "Apply patches";
        }
    }

    // Operations

    private async Task RunOperationAsync(string title, Action op)
    {
        SetBusy(true);
        Log($"=== {title} ===");
        try
        {
            await Task.Run(op);
            Log($"=== {title}: done ===");
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            MessageBox.Show(this, ex.Message, $"{title} failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool RequireElevation(string verb)
    {
        if (Patcher.IsElevated()) return true;
        MessageBox.Show(this,
            $"This action ({verb}) modifies files under C:\\Windows\\System32 and needs Administrator rights." +
            "Close this window and relaunch the app with \"Run as administrator\".",
            "Administrator required",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void SetBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetBusy(busy)); return; }
        ApplyBtn.IsEnabled = !busy && ApplyBtn.IsEnabled;
        RestoreBtn.IsEnabled = !busy && RestoreBtn.IsEnabled;
        RefreshBtn.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void Log(string line)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(line)); return; }
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        LogBox.AppendText($"[{stamp}] {line}\n");
        LogScroll.ScrollToEnd();
        Debug.WriteLine(line);
    }

    // Presentation

    private static string SummarizeState(TargetSummary t)
    {
        if (!t.Exists) return "MISSING";
        int patched = t.Sites.Count(s => s.State == PatchStateValue.Patched);
        int unknown = t.Sites.Count(s => s.State == PatchStateValue.Unknown);
        if (unknown > 0) return "UNKNOWN";
        if (t.Sites.Count == 0) return "—";
        if (patched == t.Sites.Count) return "PATCHED";
        if (patched == 0) return "ORIGINAL";
        return $"PARTIAL ({patched}/{t.Sites.Count})";
    }

    private static Brush SummarizeBrush(TargetSummary t)
    {
        if (!t.Exists) return BrushUnknown;
        int patched = t.Sites.Count(s => s.State == PatchStateValue.Patched);
        int unknown = t.Sites.Count(s => s.State == PatchStateValue.Unknown);
        if (unknown > 0) return BrushUnknown;
        if (patched == t.Sites.Count) return BrushPatched;
        if (patched == 0) return BrushOriginal;
        return BrushNeutral;
    }

    private static string StateLabel(PatchStateValue v) => v switch
    {
        PatchStateValue.Patched => "patched",
        PatchStateValue.Original => "original",
        _ => "unknown",
    };

    private static Brush StateBrush(PatchStateValue v) => v switch
    {
        PatchStateValue.Patched => BrushPatched,
        PatchStateValue.Original => BrushOriginal,
        _ => BrushUnknown,
    };
}
