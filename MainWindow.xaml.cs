using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AutoSizeScan.Services;
using AutoSizeScan.Models;
using System.IO;
using Microsoft.Win32;
using System.Reflection;

namespace AutoSizeScan;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly ScannerService _scannerService;
    private BitmapSource? _lastScannedImage;
    private (int Width, int Height)? _lastRawSize;
    private bool _hasUnsavedScan;

    // Preview zoom state.
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;
    private const double ZoomStep = 1.2;
    private double _zoom = 1.0;
    
    public MainWindow()
    {
        InitializeComponent();
        _scannerService = new ScannerService();
        BuildInfoText.Text = BuildInfoHelper();
        Loaded += async (_, _) => await LoadScannersAsync();
    }
    
    private async Task LoadScannersAsync()
    {
        try
        {
            StatusText.Text = "Detecting scanners...";
            ScanButton.IsEnabled = false;
            RefreshButton.IsEnabled = false;

            var scanners = await _scannerService.GetAvailableScannersAsync();
            ScannerComboBox.ItemsSource = scanners;

            if (scanners.Count == 0)
            {
                StatusText.Text = "No scanners found";
                ScanButton.IsEnabled = false;
                return;
            }

            var reachableCount = scanners.Count(s => s.IsReachable);

            // Auto-select the first reachable scanner, if any.
            var firstReachable = scanners.FindIndex(s => s.IsReachable);
            if (firstReachable >= 0)
            {
                ScannerComboBox.SelectedIndex = firstReachable;
            }

            StatusText.Text = reachableCount > 0
                ? $"{scanners.Count} scanner(s) ({reachableCount} reachable)"
                : "No reachable scanners found";

            ScanButton.IsEnabled = reachableCount > 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error loading scanners: {ex.Message}";
            ScanButton.IsEnabled = false;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadScannersAsync();
    }
    
    private void ScannerComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ScannerComboBox.SelectedItem is not ScannerDevice scanner)
        {
            return;
        }

        if (!scanner.IsReachable)
        {
            ScanButton.IsEnabled = false;
            StatusText.Text = scanner.StatusReason == "in use"
                ? $"{scanner.Name} is in use by another app — close it and click Refresh"
                : $"{scanner.Name} is {scanner.StatusReason ?? "not reachable"}";
            return;
        }

        ScanButton.IsEnabled = true;
        StatusText.Text = $"Selected: {scanner.Name}";
    }
    
    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (ScannerComboBox.SelectedItem == null)
        {
            StatusText.Text = "Please select a scanner";
            return;
        }

        // Warn before discarding an unsaved scan.
        if (_hasUnsavedScan)
        {
            var choice = MessageBox.Show(
                "You have a scan that hasn't been saved. Discard it and start a new scan?",
                "Discard last scan?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (choice != MessageBoxResult.Yes)
            {
                return;
            }
        }

        // Clear the canvas before the new scan starts.
        ClearScan();

        try
        {
            StatusText.Text = "Scanning (full bed attempt)...";
            ScanButton.IsEnabled = false;
            
            var scannerName = ScannerComboBox.SelectedItem.ToString()!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            
            var progress = new Progress<string>(message => StatusText.Text = message);
            var (image, rawWidth, rawHeight) = await _scannerService.ScanDocumentAsync(scannerName, cts.Token, progress);

            // Auto-crop the empty bed area around the photo.
            var cropped = ImageProcessor.AutoCropToContent(image);

            _lastScannedImage = cropped;
            _lastRawSize = (rawWidth, rawHeight);
            _hasUnsavedScan = true;
            PreviewImage.Source = cropped;
            FitToWindow();
            StatusText.Text = "Scan complete - click Save to store the photo";
            SaveButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan timed out - scanner may not be responding";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastScannedImage == null)
        {
            StatusText.Text = "Nothing to save - scan a photo first";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save Scanned Photo",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            FileName = $"scan_{DateTime.Now:yyyyMMdd_HHmmss}",
            DefaultExt = "jpg",
            AddExtension = true,
            Filter = "JPEG Image (*.jpg)|*.jpg|PNG Image (*.png)|*.png|TIFF Image (*.tiff)|*.tiff"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var format = Path.GetExtension(dialog.FileName).TrimStart('.').ToLower();
            if (string.IsNullOrEmpty(format))
            {
                format = "jpg";
            }

            _scannerService.SaveImage(_lastScannedImage, dialog.FileName, format);
            _hasUnsavedScan = false;
            StatusText.Text = $"Saved to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    private void ClearScan()
    {
        _lastScannedImage = null;
        _lastRawSize = null;
        _hasUnsavedScan = false;
        PreviewImage.Source = null;
        PreviewImage.Width = double.NaN;
        PreviewImage.Height = double.NaN;
        _zoom = 1.0;
        DimensionsText.Text = string.Empty;
        SaveButton.IsEnabled = false;
    }

    // ---- Preview zoom / scroll ----

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.OemPlus:
            case Key.Add:
                SetZoom(_zoom * ZoomStep);
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                SetZoom(_zoom / ZoomStep);
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                FitToWindow();
                e.Handled = true;
                break;
        }
    }

    private void PreviewScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_lastScannedImage == null)
        {
            return;
        }

        var factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
        SetZoom(_zoom * factor);
        e.Handled = true;
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ApplyZoom();
    }

    /// <summary>
    /// Picks the largest zoom (capped at 100%) that lets the whole photo fit
    /// inside the preview viewport, then applies it.
    /// </summary>
    private void FitToWindow()
    {
        if (_lastScannedImage == null)
        {
            return;
        }

        // Account for the image margin (10 on each side).
        double viewW = PreviewScrollViewer.ActualWidth - 24;
        double viewH = PreviewScrollViewer.ActualHeight - 24;

        if (viewW <= 0 || viewH <= 0)
        {
            _zoom = 1.0;
        }
        else
        {
            double fit = Math.Min(viewW / _lastScannedImage.PixelWidth,
                                  viewH / _lastScannedImage.PixelHeight);
            _zoom = Math.Clamp(Math.Min(1.0, fit), MinZoom, MaxZoom);
        }

        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_lastScannedImage == null)
        {
            return;
        }

        PreviewImage.Width = _lastScannedImage.PixelWidth * _zoom;
        PreviewImage.Height = _lastScannedImage.PixelHeight * _zoom;

        string rawSuffix = _lastRawSize.HasValue
            ? $" (raw {_lastRawSize.Value.Width} x {_lastRawSize.Value.Height})"
            : string.Empty;

        DimensionsText.Text =
            $"Photo dimensions: {_lastScannedImage.PixelWidth} x {_lastScannedImage.PixelHeight} pixels{rawSuffix}  ·  Zoom {_zoom:P0}";
    }

    private static string BuildInfoHelper()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            var timestamp = File.GetLastWriteTime(assembly.Location);
            var versionString = version != null ? version.ToString(3) : "?";
            return $"Build {versionString} · {timestamp:yyyy-MM-dd HH:mm}";
        }
        catch
        {
            return "Build unknown";
        }
    }
}