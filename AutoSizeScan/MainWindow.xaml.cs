using System.Windows;
using AutoSizeScan.Services;
using System.IO;

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
                StatusText.Text = $"Found {scanners.Count} scanner(s)";
            }
            else
            {
                StatusText.Text = "No scanners found";
                ScanButton.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error loading scanners: {ex.Message}";
            ScanButton.IsEnabled = false;
        }
    }
    
    private void ScanButton_Click(object sender, RoutedEventArgs e)
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
            var (image, width, height) = _scannerService.ScanDocument(scannerName);
            
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