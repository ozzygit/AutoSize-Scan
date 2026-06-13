using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.IO;

namespace AutoSizeScan.Services;

public class ScannerService
{
    private const string WIA_DEVICE_MANAGER = "WIA.DeviceManager";
    private const string WIA_DEVICE_TYPE_SCANNER = "1";
    
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
                    scanners.Add(nameProperty.get_Value().ToString());
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

    public (BitmapImage image, int width, int height) ScanDocument(string scannerName)
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
                    if (nameProperty.get_Value().ToString() == scannerName)
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
            dynamic imageFile = item.Transfer("{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}"); // JPEG format GUID
            
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
        catch (Exception ex)
        {
            throw new Exception("Error scanning document: " + ex.Message, ex);
        }
        finally
        {
            if (scanner != null) Marshal.ReleaseComObject(scanner);
            if (deviceManager != null) Marshal.ReleaseComObject(deviceManager);
        }
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
