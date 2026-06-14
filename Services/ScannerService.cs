using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
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

    // WIA item (scan) property IDs that define the scan area and resolution.
    private const int WIA_IPS_XRES = 6147;     // Horizontal resolution (DPI)
    private const int WIA_IPS_YRES = 6148;     // Vertical resolution (DPI)
    private const int WIA_IPS_XEXTENT = 6149;  // Width of scan region (pixels)
    private const int WIA_IPS_YEXTENT = 6150;  // Height of scan region (pixels)
    private const int WIA_IPS_XPOS = 6151;     // Left start of scan region (pixels)
    private const int WIA_IPS_YPOS = 6152;     // Top start of scan region (pixels)

    // Resolution used for scans. Photo-friendly default; change here if needed.
    private const int DefaultScanDpi = 300;

    // Per-scanner time budget for the live communication probe.
    // Increased from 4s to 10s to allow time for scanners to wake from sleep mode.
    // The refresh button exists to re-probe after waking a sleeping scanner.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

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
                if (deviceManager == null)
                {
                    throw new Exception("WIA Device Manager not available");
                }

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
            if (deviceManager == null)
            {
                return "WIA unavailable";
            }

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

            // Connect() uses cached registry data, so it succeeds even for an
            // unplugged network scanner. Some drivers (e.g. Brother) also return
            // cached values for status properties, so a property read isn't a
            // reliable signal either.
            try
            {
                scanner = targetInfo.Connect();
            }
            catch (COMException ex) when (ex.ErrorCode == unchecked((int)0x80210015))
            {
                return "in use";
            }

            // For network scanners we can find an IP in the WIA properties; the
            // most reliable test is whether that host actually responds.
            var ip = ExtractIpAddress(targetInfo, scanner);
            if (ip != null)
            {
                return IsHostReachable(ip) ? null : "not reachable";
            }

            // No IP exposed (USB / WSD): fall back to a live hardware-poll property.
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

    private static readonly Regex Ipv4Regex = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);

    /// <summary>
    /// Scans the device's WIA property values for an IPv4 address. Network
    /// scanners typically expose their address in a port/connection property.
    /// Returns null for USB/WSD devices that have no routable IP.
    /// </summary>
    private string? ExtractIpAddress(dynamic? deviceInfo, dynamic? scanner)
    {
        var candidates = new List<string>();
        CollectPropertyStrings(deviceInfo != null ? deviceInfo.Properties : null, candidates);
        CollectPropertyStrings(scanner != null ? scanner.Properties : null, candidates);

        foreach (var text in candidates)
        {
            foreach (Match match in Ipv4Regex.Matches(text))
            {
                var ip = match.Value;
                if (IPAddress.TryParse(ip, out var addr)
                    && !IPAddress.IsLoopback(addr)
                    && ip != "0.0.0.0")
                {
                    return ip;
                }
            }
        }
        return null;
    }

    private void CollectPropertyStrings(dynamic? properties, List<string> sink)
    {
        if (properties == null) return;
        try
        {
            foreach (dynamic prop in properties)
            {
                try
                {
                    var value = prop.Value;
                    if (value != null) sink.Add(value.ToString());
                }
                catch
                {
                    // Some properties throw on read; ignore them.
                }
            }
        }
        catch
        {
            // Property collection not enumerable; ignore.
        }
    }

    /// <summary>
    /// Returns true if the host responds on the network. A successful connection
    /// OR an active "connection refused" both prove the host is alive; only a
    /// timeout / no-route means unreachable. Common scanner/MFD ports are tried.
    /// </summary>
    private bool IsHostReachable(string ip)
    {
        // Brother scan, raw print, LPD, http(s), and WSD device-host (WSDAPI)
        // ports. 5357/5358 cover WSD scanners, which expose an IP but do not
        // listen on the print/scan ports above.
        int[] ports = { 54921, 9100, 515, 80, 443, 5357, 5358 };
        var tasks = ports.Select(port => TryConnectAsync(ip, port)).ToList();

        while (tasks.Count > 0)
        {
            var index = Task.WaitAny(tasks.ToArray());
            var finished = tasks[index];
            if (finished.Status == TaskStatus.RanToCompletion && finished.Result)
            {
                return true;
            }
            tasks.RemoveAt(index);
        }
        return false;
    }

    private static async Task<bool> TryConnectAsync(string ip, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(ip, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromMilliseconds(1500)));

            if (completed != connectTask)
            {
                return false; // timed out on this port
            }

            await connectTask; // observe any exception
            return client.Connected;
        }
        catch (SocketException ex)
        {
            // The host actively rejected the port, which still proves it is online.
            return ex.SocketErrorCode == SocketError.ConnectionRefused;
        }
        catch
        {
            return false;
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
                if (deviceManager == null)
                {
                    throw new Exception("WIA Device Manager not available");
                }
                
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

                // Attempt 1: configure full-bed properties (correct for most scanners).
                ConfigureFullBedScan(item);
                BitmapImage bitmap = TransferToBitmap(item);

                // Some drivers (e.g. Brother WSD network scanners) silently ignore WIA
                // property writes via SubTypeMax, producing a tiny image (e.g. 128x126 px)
                // that reflects stale driver-internal defaults rather than the full bed.
                // Detect this by checking whether the shorter dimension is implausibly
                // small: even 75 DPI on the shortest side of A4/Letter yields ~620 px,
                // so 500 px is a safe floor.
                if (Math.Min(bitmap.PixelWidth, bitmap.PixelHeight) < MinValidDimension)
                {
                    // Attempt 2: force an explicit A4 300 DPI extent via direct value write.
                    // Brother drivers reject SubTypeMax but do accept direct integer writes.
                    item = scanner.Items[1];
                    ConfigureHardcodedA4Scan(item);
                    bitmap = TransferToBitmap(item);

                    if (Math.Min(bitmap.PixelWidth, bitmap.PixelHeight) < MinValidDimension)
                    {
                        // Attempt 3: bare transfer with no WIA property configuration.
                        // WSD drivers (e.g. Brother registered as '[deviceid]') ignore all
                        // property writes but work correctly with their own internal defaults
                        // once those defaults have been reset via the manufacturer's app
                        // (e.g. iPrint&Scan). This is the path that works for those drivers.
                        item = scanner.Items[1];
                        bitmap = TransferToBitmap(item);

                        if (Math.Min(bitmap.PixelWidth, bitmap.PixelHeight) < MinValidDimension)
                        {
                            // All three attempts failed. Driver defaults are also invalid —
                            // typically a fresh WSD registration with factory defaults.
                            // Fix: open the manufacturer's scan app (e.g. iPrint&Scan),
                            // perform one scan to reset the driver state, then retry here.
                            throw new Exception(
                                $"Scanner returned a {bitmap.PixelWidth}x{bitmap.PixelHeight} image after three attempts. " +
                                "The driver defaults may need resetting. Open the manufacturer's scan app " +
                                "(e.g. iPrint&Scan), perform one scan, then try again.");
                        }
                    }
                }

                return (bitmap, bitmap.PixelWidth, bitmap.PixelHeight);
            }
            catch (OperationCanceledException)
            {
                throw; // preserve cancellation so the UI can show the timeout message
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

    // If the shorter pixel dimension of a scan result is below this value we treat it
    // as a driver-ignored-properties failure and retry with driver defaults.
    private const int MinValidDimension = 500;

    /// <summary>
    /// Transfers a WIA item to a BitmapImage via a temp JPEG file.
    /// </summary>
    private static BitmapImage TransferToBitmap(dynamic item)
    {
        dynamic imageFile = item.Transfer(WIA_FORMAT_JPEG);
        var tempPath = Path.Combine(Path.GetTempPath(), $"scan_{Guid.NewGuid()}.jpg");
        imageFile.SaveFile(tempPath);

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // copies pixels to memory before EndInit
            bitmap.UriSource = new Uri(tempPath);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // A4 page at 300 DPI: 8.27" × 11.69" = 2480 × 3508 pixels.
    // Used as an explicit fallback extent for drivers that ignore SubTypeMax.
    private const int A4Width300Dpi = 2480;
    private const int A4Height300Dpi = 3508;

    /// <summary>
    /// Fallback scan configuration for drivers (e.g. Brother WSD) that ignore
    /// SubTypeMax extent writes. Sets an explicit A4 300 DPI extent via direct
    /// integer value assignment, which these drivers do accept.
    /// </summary>
    private static void ConfigureHardcodedA4Scan(dynamic item)
    {
        dynamic properties = item.Properties;

        TrySetWiaProperty(properties, WIA_IPS_XRES, DefaultScanDpi);
        TrySetWiaProperty(properties, WIA_IPS_YRES, DefaultScanDpi);
        TrySetWiaProperty(properties, WIA_IPS_XPOS, 0);
        TrySetWiaProperty(properties, WIA_IPS_YPOS, 0);
        TrySetWiaProperty(properties, WIA_IPS_XEXTENT, A4Width300Dpi);
        TrySetWiaProperty(properties, WIA_IPS_YEXTENT, A4Height300Dpi);
    }

    /// <summary>
    /// Forces the scan to capture the full flatbed at a known resolution.
    /// Without this, WIA reuses whatever item defaults the driver currently
    /// holds, which on some devices is a small extent (or a leftover high-DPI
    /// setting) and produces a zoomed-in partial scan of the top-left corner.
    /// Each property is set defensively so drivers (e.g. some WSD devices) that
    /// don't expose a given property are simply skipped.
    /// </summary>
    private static void ConfigureFullBedScan(dynamic item)
    {
        dynamic properties = item.Properties;

        // Resolution first: the driver rescales the extent maxima to match.
        TrySetWiaProperty(properties, WIA_IPS_XRES, DefaultScanDpi);
        TrySetWiaProperty(properties, WIA_IPS_YRES, DefaultScanDpi);

        // Origin at the top-left corner of the bed.
        TrySetWiaProperty(properties, WIA_IPS_XPOS, 0);
        TrySetWiaProperty(properties, WIA_IPS_YPOS, 0);

        // Capture the whole bed by maxing out the extents at this resolution.
        TrySetWiaPropertyToMax(properties, WIA_IPS_XEXTENT);
        TrySetWiaPropertyToMax(properties, WIA_IPS_YEXTENT);
    }

    /// <summary>
    /// Locates a WIA property in a collection by its numeric PropertyID.
    /// Returns null when the property is not present.
    /// </summary>
    private static object? FindWiaProperty(dynamic properties, int propertyId)
    {
        foreach (dynamic prop in properties)
        {
            try
            {
                if ((int)prop.PropertyID == propertyId)
                {
                    return prop;
                }
            }
            catch
            {
                // Property doesn't expose a readable ID; skip it.
            }
        }
        return null;
    }

    private static void TrySetWiaProperty(dynamic properties, int propertyId, int value)
    {
        try
        {
            object? found = FindWiaProperty(properties, propertyId);
            if (found == null)
            {
                return;
            }

            dynamic prop = found;
            if (!prop.IsReadOnly)
            {
                prop.Value = value;
            }
        }
        catch
        {
            // Driver rejected the value or doesn't support this property.
        }
    }

    private static void TrySetWiaPropertyToMax(dynamic properties, int propertyId)
    {
        try
        {
            object? found = FindWiaProperty(properties, propertyId);
            if (found == null)
            {
                return;
            }

            dynamic prop = found;
            if (!prop.IsReadOnly)
            {
                prop.Value = prop.SubTypeMax;
            }
        }
        catch
        {
            // Driver rejected the value or doesn't support this property.
        }
    }

    public void SaveImage(BitmapSource image, string outputPath, string format = "jpg")
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
