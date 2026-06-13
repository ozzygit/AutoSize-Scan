using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.IO;
using AutoSizeScan.Models;

namespace AutoSizeScan.Services;

public class ScannerService
{
    private const string WIA_DEVICE_MANAGER = "WIA.DeviceManager";
    private const string WIA_DEVICE_TYPE_SCANNER = "1";
    private const string WIA_FORMAT_JPEG = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";

    // WIA properties whose read forces the minidriver to poll the physical hardware.
    private const string WIA_DPA_CONNECT_STATUS = "1011";
    private const string WIA_DPS_DOCUMENT_HANDLING_STATUS = "3087";

    // Per-scanner time budget for the live communication probe.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Enumerates all OS-registered scanners and tags each with whether it can
    /// actually be reached via a live WIA hardware poll. Unreachable devices are
    /// returned too (so the UI can show them disabled), but flagged accordingly.
    /// </summary>
    public async Task<List<ScannerDevice>> GetAvailableScannersAsync()
    {
        // Collect names first (cheap, cached metadata only).
        var names = await Task.Run(() =>
        {
            var found = new List<string>();
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
                        found.Add(nameProperty.Value.ToString());
                    }
                }
            }
            finally
            {
                if (deviceManager != null) Marshal.ReleaseComObject(deviceManager);
            }
            return found;
        });

        // Probe each scanner in parallel so total wait stays ~ProbeTimeout.
        var probeTasks = names.Select(ProbeScannerAsync).ToList();
        var results = await Task.WhenAll(probeTasks);
        return results.ToList();
    }

    private async Task<ScannerDevice> ProbeScannerAsync(string scannerName)
    {
        var device = new ScannerDevice { Name = scannerName };

        var probe = Task.Run(() => ProbeDeviceCommunication(scannerName));
        var completed = await Task.WhenAny(probe, Task.Delay(ProbeTimeout));

        if (completed == probe && probe.Status == TaskStatus.RanToCompletion)
        {
            var reason = probe.Result;
            device.IsReachable = reason == null;
            device.StatusReason = reason;
        }
        else
        {
            // Timed out (likely waiting on an unreachable network device) or faulted.
            device.IsReachable = false;
            device.StatusReason = "not reachable";
        }

        return device;
    }

    /// <summary>
    /// Connects to the named scanner and reads a property that forces a live
    /// hardware poll. Returns null when the device is reachable, otherwise a
    /// short reason string describing why it is not.
    /// </summary>
    private string? ProbeDeviceCommunication(string scannerName)
    {
        dynamic? scanner = null;
        dynamic? deviceManager = null;

        try
        {
            var deviceManagerType = Type.GetTypeFromProgID(WIA_DEVICE_MANAGER);
            if (deviceManagerType == null)
            {
                return "WIA unavailable";
            }

            deviceManager = Activator.CreateInstance(deviceManagerType);

            dynamic? targetInfo = null;
            foreach (dynamic deviceInfo in deviceManager.DeviceInfos)
            {
                if (deviceInfo.Type.ToString() == WIA_DEVICE_TYPE_SCANNER)
                {
                    dynamic nameProperty = deviceInfo.Properties["Name"];
                    if (nameProperty.Value.ToString() == scannerName)
                    {
                        targetInfo = deviceInfo;
                        break;
                    }
                }
            }

            if (targetInfo == null)
            {
                return "not found";
            }

            // Connect() alone uses cached registry data for network devices, so we
            // must read a property that the driver refreshes from the hardware.
            scanner = targetInfo.Connect();

            if (TryReadLiveProperty(scanner))
            {
                return null; // reachable
            }

            return "not reachable";
        }
        catch (COMException ex)
        {
            if (ex.ErrorCode == unchecked((int)0x80210015))
            {
                return "in use";
            }
            return "not reachable";
        }
        catch (Exception)
        {
            return "not reachable";
        }
        finally
        {
            if (scanner != null) Marshal.ReleaseComObject(scanner);
            if (deviceManager != null) Marshal.ReleaseComObject(deviceManager);
        }
    }

    /// <summary>
    /// Reads a device property that requires a live hardware poll. Tries
    /// WIA_DPA_CONNECT_STATUS first, then WIA_DPS_DOCUMENT_HANDLING_STATUS.
    /// </summary>
    private bool TryReadLiveProperty(dynamic scanner)
    {
        foreach (var propId in new[] { WIA_DPA_CONNECT_STATUS, WIA_DPS_DOCUMENT_HANDLING_STATUS })
        {
            try
            {
                dynamic prop = scanner.Properties[propId];
                // Accessing .Value triggers the driver to poll the hardware.
                var _ = prop.Value;
                return true;
            }
            catch
            {
                // Property not implemented by this driver; try the next one.
            }
        }
        return false;
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
