using System.Windows;
using AutoSizeScan.Services;
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
        LoadScanners();
    }
    
    private void LoadScanners()
    {
        try
        {
            var scanners = _scannerService.GetAvailableScanners();
            ScannerComboBox.ItemsSource = scanners;
            
            if (scanners.Count > 0)
            {
                ScannerComboBox.SelectedIndex = 0;
                StatusText.Text = $"Found {scanners.Count} communicable scanner(s)";
            }
            else
            {
                StatusText.Text = "No communicable scanners found";
                ScanButton.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error loading scanners: {ex.Message}";
            ScanButton.IsEnabled = false;
        }
    }
    
    private void ScannerComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ScannerComboBox.SelectedItem == null)
        {
            return;
        }
        
        var scannerName = ScannerComboBox.SelectedItem.ToString()!;
        
        // Check if scanner is in use by another application
        if (_scannerService.IsScannerInUse(scannerName))
        {
            MessageBox.Show(
                "This scanner is currently in use by another application. Please close the other application or wait for it to finish.",
                "Scanner In Use",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            ScanButton.IsEnabled = false;
        }
        else
        {
            ScanButton.IsEnabled = true;
            StatusText.Text = $"Selected: {scannerName}";
        }
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