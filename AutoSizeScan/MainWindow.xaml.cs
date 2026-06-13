using System.Windows;
using AutoSizeScan.Services;
using AutoSizeScan.Models;
using System.IO;
using System.Runtime.InteropServices;

namespace AutoSizeScan;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly ScannerService _scannerService;
    
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
            
            PreviewImage.Source = image;
            DimensionsText.Text = $"Scanned dimensions: {width} x {height} pixels";
            StatusText.Text = "Scan complete";
            
            var savePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"scan_{DateTime.Now:yyyyMMdd_HHmmss}.jpg"
            );
            
            _scannerService.SaveImage(image, savePath);
            StatusText.Text = $"Scan complete - Saved to {Path.GetFileName(savePath)}";
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
}