using System.Windows;
using System.Windows.Media.Imaging;
using AutoSizeScan.Services;
using AutoSizeScan.Models;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AutoSizeScan;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly ScannerService _scannerService;
    private BitmapImage? _lastScannedImage;
    
    public MainWindow()
    {
        InitializeComponent();
        _scannerService = new ScannerService();
        Loaded += async (_, _) => await LoadScannersAsync();
    }
    
    private async Task LoadScannersAsync()
    {
        try
        {
            StatusText.Text = "Detecting scanners...";
            ScanButton.IsEnabled = false;

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
            StatusText.Text = $"{scanner.Name} is {scanner.StatusReason ?? "not reachable"}";
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
        
        try
        {
            StatusText.Text = "Scanning...";
            ScanButton.IsEnabled = false;
            
            var scannerName = ScannerComboBox.SelectedItem.ToString()!;
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // 60 second timeout
            
            var (image, width, height) = await _scannerService.ScanDocumentAsync(scannerName, cts.Token);
            
            _lastScannedImage = image;
            PreviewImage.Source = image;
            DimensionsText.Text = $"Scanned dimensions: {width} x {height} pixels";
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
            StatusText.Text = $"Saved to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
    }
}