using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.IO;

namespace AutoSizeScan.Services;

public class ScannerService
{
    private const string WIA_DEVICE_MANAGER = "WIA.DeviceManager";
    private const string WIA_DEVICE_TYPE_SCANNER = "1";
    private const string WIA_FORMAT_JPEG = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";
    
    public List<string> GetAvailableScanners()
    {
        var scanners = new List<string>();
        
        try
        {
            var deviceManagerType = Type.GetTypeFromProgID(WIA_DEVICE_MANAGER);
            if (deviceManagerType == null)
            {
                throw new Exception("WIA Device Manager not available");
            }
            
            dynamic deviceManager = Activator.CreateInstance(deviceManagerType);
            
            foreach (dynamic deviceInfo in deviceManager.DeviceInfos)
            {
                if (deviceInfo.Type.ToString() == WIA_DEVICE_TYPE_SCANNER)
                {
                    dynamic nameProperty = deviceInfo.Properties["Name"];
                    var scannerName = nameProperty.Value.ToString();
                    
                    // Test if we can actually communicate with this scanner
                    if (TestScannerConnection(deviceInfo))
                    {
                        scanners.Add(scannerName);
                    }
                }
            }
            
            Marshal.ReleaseComObject(deviceManager);
        }
        catch (Exception ex)
        {
            throw new Exception("Error enumerating scanners: " + ex.Message, ex);
        }
        
        return scanners;
    }
    
    private bool TestScannerConnection(dynamic deviceInfo)
    {
        dynamic? scanner = null;
        
        try
        {
            scanner = deviceInfo.Connect();
            
            // Try to access the scanner's items to verify it can actually perform operations
            dynamic item = scanner.Items[1];
            
            // Try to get properties to ensure the scanner is responsive
            dynamic properties = item.Properties;
            
            return true;
        }
        catch (Exception)
        {
            // Cannot connect or access items - scanner may be in use, offline, or driver issues
            return false;
        }
        finally
        {
            if (scanner != null) Marshal.ReleaseComObject(scanner);
        }
    }
    
    public bool IsScannerInUse(string scannerName)
    {
        dynamic? scanner = null;
        dynamic? deviceManager = null;
        
        try
        {
            var deviceManagerType = Type.GetTypeFromProgID(WIA_DEVICE_MANAGER);
            if (deviceManagerType == null)
            {
                return false;
            }
            
            deviceManager = Activator.CreateInstance(deviceManagerType);
            
            foreach (dynamic deviceInfo in deviceManager.DeviceInfos)
            {
                if (deviceInfo.Type.ToString() == WIA_DEVICE_TYPE_SCANNER)
                {
                    dynamic nameProperty = deviceInfo.Properties["Name"];
                    if (nameProperty.Value.ToString() == scannerName)
                    {
                        try
                        {
                            scanner = deviceInfo.Connect();
                            // If we can connect, it's not in use
                            return false;
                        }
                        catch (COMException ex)
                        {
                            // Check for specific error codes that indicate device in use
                            // 0x80210015 = WIA_ERROR_DEVICE_BUSY
                            if (ex.ErrorCode == unchecked((int)0x80210015))
                            {
                                return true;
                            }
                            return false;
                        }
                    }
                }
            }
            
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (scanner != null) Marshal.ReleaseComObject(scanner);
            if (deviceManager != null) Marshal.ReleaseComObject(deviceManager);
        }
    }

    public async Task<(BitmapImage image, int width, int height)> ScanDocumentAsync(string scannerName, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            dynamic? scanner = null;
            dynamic? deviceManager = null;
            
            try
            {
                var deviceManagerType = Type.GetTypeFromProgID(WIA_DEVICE_MANAGER);
                if (deviceManagerType == null)
                {
                    throw new Exception("WIA Device Manager not available");
                }
                
                deviceManager = Activator.CreateInstance(deviceManagerType);
                
                foreach (dynamic deviceInfo in deviceManager.DeviceInfos)
                {
                    if (deviceInfo.Type.ToString() == WIA_DEVICE_TYPE_SCANNER)
                    {
                        dynamic nameProperty = deviceInfo.Properties["Name"];
                        if (nameProperty.Value.ToString() == scannerName)
                        {
                            scanner = deviceInfo.Connect();
                            break;
                        }
                    }
                }
                
                if (scanner == null)
                {
                    throw new Exception($"Scanner '{scannerName}' not found.");
                }
                
                dynamic item = scanner.Items[1];
                dynamic imageFile = item.Transfer(WIA_FORMAT_JPEG);
                
                var tempPath = Path.Combine(Path.GetTempPath(), $"scan_{Guid.NewGuid()}.jpg");
                imageFile.SaveFile(tempPath);
                
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(tempPath);
                bitmap.EndInit();
                bitmap.Freeze();
                
                int width = bitmap.PixelWidth;
                int height = bitmap.PixelHeight;
                
                return (bitmap, width, height);
            }
            catch (COMException ex)
            {
                throw new Exception($"Scan failed with COM error 0x{ex.ErrorCode:X8}: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new Exception("Error scanning document: " + ex.Message, ex);
            }
            finally
            {
                if (scanner != null) Marshal.ReleaseComObject(scanner);
                if (deviceManager != null) Marshal.ReleaseComObject(deviceManager);
            }
        }, cancellationToken);
    }

    public void SaveImage(BitmapImage image, string outputPath, string format = "jpg")
    {
        try
        {
            BitmapEncoder encoder = format.ToLower() switch
            {
                "png" => new PngBitmapEncoder(),
                "tiff" => new TiffBitmapEncoder(),
                _ => new JpegBitmapEncoder()
            };
            
            encoder.Frames.Add(BitmapFrame.Create(image));
            
            using var fileStream = new FileStream(outputPath, FileMode.Create);
            encoder.Save(fileStream);
        }
        catch (Exception ex)
        {
            throw new Exception("Error saving image: " + ex.Message, ex);
        }
    }
}
