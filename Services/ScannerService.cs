using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AutoSizeScan.Models;

namespace AutoSizeScan.Services;

public class ScannerService
{
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoSizeScan",
        "scanner-debug.log");

    private static readonly object LogSync = new();

    private const string WIA_DEVICE_MANAGER = "WIA.DeviceManager";
    private const string WIA_DEVICE_TYPE_SCANNER = "1";
    private const string WIA_FORMAT_JPEG = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";

    // WIA properties whose read forces the minidriver to poll the physical hardware.
    private const string WIA_DPA_CONNECT_STATUS = "1011";
    private const string WIA_DPS_DOCUMENT_HANDLING_STATUS = "3087";

    private const int WIA_DPS_DOCUMENT_HANDLING_SELECT = 3088;
    private const int WIA_DPS_DOCUMENT_HANDLING_SELECT_FEEDER = 0x00000001;
    private const int WIA_DPS_DOCUMENT_HANDLING_SELECT_FLATBED = 0x00000002;

    // WIA item (scan) property IDs that define the scan area and resolution.
    private const int WIA_IPS_XRES = 6147;     // Horizontal resolution (DPI)
    private const int WIA_IPS_YRES = 6148;     // Vertical resolution (DPI)
    private const int WIA_IPS_XEXTENT = 6149;  // Width of scan region (pixels)
    private const int WIA_IPS_YEXTENT = 6150;  // Height of scan region (pixels)
    private const int WIA_IPS_XPOS = 6151;     // Left start of scan region (pixels)
    private const int WIA_IPS_YPOS = 6152;     // Top start of scan region (pixels)
    private const int WIA_IPS_PREVIEW = 6157;  // 0 = final scan, 1 = preview thumbnail

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
    
    public async Task<(BitmapImage image, int width, int height)> ScanDocumentAsync(string scannerName, CancellationToken cancellationToken = default, IProgress<string>? progress = null)
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
                
                Report(progress, "Scanning (selecting flatbed)…");
                EnsureFlatbedSelected(scanner);
                DumpWiaProperties("Scanner properties (pre-scan)", scanner?.Properties);

                dynamic item = SelectScanItem(scanner);
                DumpWiaItemProperties("Attempt FullBed (pre-config)", item);

                // Attempt 1: configure full-bed properties (correct for most scanners).
                Report(progress, "Scanning (full bed)…");
                ConfigureFullBedScan(item);
                BitmapImage bitmap = TransferToBitmap(item);
                string lastAttempt = "FullBed";
                LogAttemptResult(lastAttempt, bitmap);

                if (IsBelowMinimum(bitmap))
                {
                    Log($"Attempt {lastAttempt} returned {DescribeDimensions(bitmap)}, below {MinValidDimension}. Trying HardcodedA4 fallback.");
                    Report(progress, "Scanning (forcing A4 extent)…");
                    lastAttempt = "HardcodedA4";
                    item = SelectScanItem(scanner);
                    DumpWiaItemProperties("Attempt HardcodedA4 (pre-config)", item);
                    ConfigureHardcodedA4Scan(item);
                    bitmap = TransferToBitmap(item);
                    LogAttemptResult(lastAttempt, bitmap);

                    if (IsBelowMinimum(bitmap))
                    {
                        Log($"Attempt {lastAttempt} returned {DescribeDimensions(bitmap)}, trying PreviewClear fallback.");
                        Report(progress, "Scanning (clearing preview flag)…");
                        lastAttempt = "PreviewClear";
                        item = SelectScanItem(scanner);
                        DumpWiaItemProperties("Attempt PreviewClear (pre-config)", item);
                        ConfigurePreviewOnlyScan(item);
                        bitmap = TransferToBitmap(item);
                        LogAttemptResult(lastAttempt, bitmap);

                        if (IsBelowMinimum(bitmap))
                        {
                            Log($"Attempt {lastAttempt} returned {DescribeDimensions(bitmap)}, trying DriverDefaults transfer.");
                            Report(progress, "Scanning (driver defaults)…");
                            lastAttempt = "DriverDefaults";
                            item = SelectScanItem(scanner);
                            DumpWiaItemProperties("Attempt DriverDefaults (pre-config)", item);
                            bitmap = TransferToBitmap(item);
                            LogAttemptResult(lastAttempt, bitmap);

                            if (IsBelowMinimum(bitmap))
                            {
                                throw new Exception(
                                    $"Scanner returned a {bitmap.PixelWidth}x{bitmap.PixelHeight} image after four attempts. " +
                                    "The driver defaults may need resetting. Open the manufacturer's scan app " +
                                    "(e.g. iPrint&Scan), perform one scan, then try again.");
                            }
                        }
                    }
                }

                Log($"Scan completed using attempt '{lastAttempt}' with final size {bitmap.PixelWidth}x{bitmap.PixelHeight}.");
                Report(progress, $"Scan complete ({bitmap.PixelWidth} x {bitmap.PixelHeight})");
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

        TrySetWiaProperty(properties, WIA_IPS_PREVIEW, 0);
        TrySetWiaProperty(properties, WIA_IPS_XRES, DefaultScanDpi);
        TrySetWiaProperty(properties, WIA_IPS_YRES, DefaultScanDpi);
        TrySetWiaProperty(properties, WIA_IPS_XPOS, 0);
        TrySetWiaProperty(properties, WIA_IPS_YPOS, 0);
        TrySetWiaProperty(properties, WIA_IPS_XEXTENT, A4Width300Dpi);
        TrySetWiaProperty(properties, WIA_IPS_YEXTENT, A4Height300Dpi);
    }

    /// <summary>
    /// Minimal scan configuration that only clears the preview flag (and reasserts
    /// resolution) for drivers that reject extent writes but do honour preview mode.
    /// </summary>
    private static void ConfigurePreviewOnlyScan(dynamic item)
    {
        dynamic properties = item.Properties;

        TrySetWiaProperty(properties, WIA_IPS_PREVIEW, 0);
        TrySetWiaProperty(properties, WIA_IPS_XRES, DefaultScanDpi);
        TrySetWiaProperty(properties, WIA_IPS_YRES, DefaultScanDpi);
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

        // Ensure we request a final (non-preview) scan and set resolution first; the driver
        // rescales the extent maxima to match the chosen DPI.
        TrySetWiaProperty(properties, WIA_IPS_PREVIEW, 0);
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

    private static bool TrySetWiaProperty(dynamic properties, int propertyId, int value)
    {
        try
        {
            object? found = FindWiaProperty(properties, propertyId);
            if (found == null)
            {
                Log($"WIA property {GetPropertyLabel(propertyId)} not found while applying {value}.");
                return false;
            }

            dynamic prop = found;
            if (!prop.IsReadOnly)
            {
                prop.Value = value;
                return true;
            }
            Log($"WIA property {GetPropertyLabel(propertyId)} is read-only and cannot be set to {value}.");
        }
        catch
        {
            Log($"WIA property {GetPropertyLabel(propertyId)} rejected value {value}.");
        }
        return false;
    }

    private static bool TrySetWiaPropertyToMax(dynamic properties, int propertyId)
    {
        try
        {
            object? found = FindWiaProperty(properties, propertyId);
            if (found == null)
            {
                Log($"WIA property {GetPropertyLabel(propertyId)} not found while applying SubTypeMax.");
                return false;
            }

            dynamic prop = found;
            if (!prop.IsReadOnly)
            {
                prop.Value = prop.SubTypeMax;
                return true;
            }
            Log($"WIA property {GetPropertyLabel(propertyId)} is read-only and cannot be set to SubTypeMax.");
        }
        catch
        {
            Log($"WIA property {GetPropertyLabel(propertyId)} rejected SubTypeMax assignment.");
        }
        return false;
    }

    private static dynamic SelectScanItem(dynamic scanner)
    {
        var candidates = new List<(object item, int area, string path)>();
        EnumerateCandidateItems(scanner, "root", candidates);

        if (candidates.Count > 0)
        {
            var best = candidates.OrderByDescending(c => c.area).First();
            Log($"Selected WIA item '{best.path}' with estimated area {best.area}.");
            return best.item;
        }

        Log("No candidate WIA items exposed extent properties; falling back to first child.");
        return GetFirstChild(scanner) ?? scanner;
    }

    private static void EnumerateCandidateItems(dynamic parent, string path, List<(object item, int area, string path)> sink)
    {
        try
        {
            int index = 0;
            foreach (dynamic child in parent.Items)
            {
                index++;
                string name = GetItemName(child, index);
                string childPath = path.Length == 0 ? name : $"{path}/{name}";

                int area = CalculateItemExtentArea(child);
                if (area > 0)
                {
                    sink.Add((child, area, childPath));
                    Log($"Candidate WIA item '{childPath}' reports extent area {area}.");
                }
                else
                {
                    Log($"WIA item '{childPath}' lacks usable extent data, skipping.");
                }

                EnumerateCandidateItems(child, childPath, sink);
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to enumerate WIA items under '{path}': {ex.Message}");
        }
    }

    private static dynamic? GetFirstChild(dynamic parent)
    {
        try
        {
            foreach (dynamic child in parent.Items)
            {
                Log($"Using first available WIA item '{GetItemName(child, 1)}'.");
                return child;
            }
        }
        catch (Exception ex)
        {
            Log($"Scanner exposes no child items: {ex.Message}");
        }

        return null;
    }

    private static int CalculateItemExtentArea(dynamic item)
    {
        int width = GetExtentBound(item, WIA_IPS_XEXTENT);
        int height = GetExtentBound(item, WIA_IPS_YEXTENT);

        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        return width * height;
    }

    private static int GetExtentBound(dynamic item, int propertyId)
    {
        try
        {
            dynamic properties = item.Properties;
            object? found = FindWiaProperty(properties, propertyId);
            if (found == null)
            {
                return 0;
            }

            dynamic prop = found;

            try
            {
                return Convert.ToInt32(prop.SubTypeMax);
            }
            catch
            {
                try
                {
                    return Convert.ToInt32(prop.Value);
                }
                catch
                {
                    return 0;
                }
            }
        }
        catch
        {
            return 0;
        }
    }

    private static string GetItemName(dynamic item, int ordinal)
    {
        try
        {
            dynamic prop = item.Properties["Item Name"];
            string? value = prop?.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        catch
        {
            // ignore and fall back to ordinal name
        }

        return $"Item#{ordinal}";
    }

    private static void EnsureFlatbedSelected(dynamic scanner)
    {
        try
        {
            dynamic properties = scanner.Properties;
            object? selectProp = FindWiaProperty(properties, WIA_DPS_DOCUMENT_HANDLING_SELECT);
            if (selectProp == null)
            {
                Log("WIA_DPS_DOCUMENT_HANDLING_SELECT not supported; assuming flatbed.");
                return;
            }

            dynamic prop = selectProp;
            if (prop.IsReadOnly)
            {
                Log("Document handling select property is read-only; cannot force flatbed.");
                return;
            }

            int current = Convert.ToInt32(prop.Value);
            if ((current & WIA_DPS_DOCUMENT_HANDLING_SELECT_FLATBED) != 0)
            {
                Log("Flatbed already selected.");
                return;
            }

            int desired = current & ~WIA_DPS_DOCUMENT_HANDLING_SELECT_FEEDER;
            desired |= WIA_DPS_DOCUMENT_HANDLING_SELECT_FLATBED;
            prop.Value = desired;
            Log($"Set document handling select to 0x{desired:X} to ensure flatbed is active.");
        }
        catch (Exception ex)
        {
            Log($"Failed to set document handling select: {ex.Message}");
        }
    }

    private static void LogAttemptResult(string attemptName, BitmapSource image)
    {
        Log($"Attempt {attemptName} produced {image.PixelWidth}x{image.PixelHeight} pixels.");
    }

    private static bool IsBelowMinimum(BitmapSource image)
    {
        return Math.Min(image.PixelWidth, image.PixelHeight) < MinValidDimension;
    }

    private static string DescribeDimensions(BitmapSource image)
    {
        return $"{image.PixelWidth}x{image.PixelHeight}";
    }

    private static void Report(IProgress<string>? progress, string message)
    {
        progress?.Report(message);
    }

    private static void DumpWiaItemProperties(string context, dynamic? item)
    {
        if (item == null)
        {
            Log($"[{context}] Item is null");
            return;
        }

        try
        {
            string name = TryRead(() => (string)item.ItemName, "<unknown>");
            string id = TryRead(() => (string)item.ItemID, "<unknown>");
            string category = TryRead(() => item.ItemCategory != null ? item.ItemCategory.ToString() : "<null>", "<unavailable>");
            string flags = TryRead(() => ((int)item.ItemFlags).ToString("X"), "<unavailable>");
            Log($"[{context}] Item summary: Name='{name}', ID='{id}', Flags=0x{flags}, Category={category}");
        }
        catch (Exception ex)
        {
            Log($"[{context}] Failed to read item summary: {ex.Message}");
        }

        try
        {
            DumpWiaProperties($"{context} properties", item.Properties);
        }
        catch (Exception ex)
        {
            Log($"[{context}] Failed to enumerate item properties: {ex.Message}");
        }
    }

    private static void DumpWiaProperties(string context, dynamic? properties)
    {
        Log($"--- WIA properties for {context} ---");
        if (properties == null)
        {
            Log($"[{context}] (no properties)");
            Log($"--- End of WIA properties for {context} ---");
            return;
        }

        try
        {
            foreach (dynamic prop in properties)
            {
                var parts = new List<string>();

                try
                {
                    int id = (int)prop.PropertyID;
                    parts.Add($"Id=0x{id:X}");
                }
                catch
                {
                    parts.Add("Id=<unavailable>");
                }

                try
                {
                    string name = prop.Name;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        parts.Add($"Name='{name}'");
                    }
                }
                catch
                {
                    parts.Add("Name=<unavailable>");
                }

                try
                {
                    bool isReadOnly = prop.IsReadOnly;
                    parts.Add($"ReadOnly={isReadOnly}");
                }
                catch
                {
                    parts.Add("ReadOnly=<unavailable>");
                }

                try
                {
                    object? value = prop.Value;
                    parts.Add($"Value={FormatWiaValue(value)}");
                }
                catch (Exception ex)
                {
                    parts.Add($"Value=<error: {ex.Message}>");
                }

                try
                {
                    parts.Add($"SubType={prop.SubType}");
                }
                catch
                {
                }

                try
                {
                    parts.Add($"Min={FormatWiaValue(prop.SubTypeMin)}");
                }
                catch
                {
                }

                try
                {
                    parts.Add($"Max={FormatWiaValue(prop.SubTypeMax)}");
                }
                catch
                {
                }

                try
                {
                    parts.Add($"Step={FormatWiaValue(prop.SubTypeStep)}");
                }
                catch
                {
                }

                try
                {
                    var values = prop.SubTypeValues;
                    if (values != null)
                    {
                        parts.Add($"SubValues={FormatWiaValue(values)}");
                    }
                }
                catch
                {
                }

                Log($"[{context}] {string.Join("; ", parts)}");
            }
        }
        catch (Exception ex)
        {
            Log($"[{context}] Failed to enumerate properties: {ex.Message}");
        }

        Log($"--- End of WIA properties for {context} ---");
    }

    private static string FormatWiaValue(object? value)
    {
        if (value == null)
        {
            return "null";
        }

        if (value is string s)
        {
            return $"\"{s}\"";
        }

        if (value is Array array)
        {
            var samples = new List<string>();
            int index = 0;
            foreach (var element in array)
            {
                if (index++ >= 5)
                {
                    samples.Add("…");
                    break;
                }
                samples.Add(element?.ToString() ?? "null");
            }

            return $"{array.GetType().Name}[{array.Length}] {{ {string.Join(", ", samples)} }}";
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var samples = new List<string>();
            int index = 0;
            foreach (var element in enumerable)
            {
                if (index++ >= 5)
                {
                    samples.Add("…");
                    break;
                }
                samples.Add(element?.ToString() ?? "null");
            }

            return $"{value.GetType().Name} {{ {string.Join(", ", samples)} }}";
        }

        return $"{value.GetType().Name}: {value}";
    }

    private static T TryRead<T>(Func<T> accessor, T fallback)
    {
        try
        {
            return accessor();
        }
        catch
        {
            return fallback;
        }
    }

    private static string GetPropertyLabel(int propertyId) => propertyId switch
    {
        WIA_IPS_XRES => nameof(WIA_IPS_XRES),
        WIA_IPS_YRES => nameof(WIA_IPS_YRES),
        WIA_IPS_XEXTENT => nameof(WIA_IPS_XEXTENT),
        WIA_IPS_YEXTENT => nameof(WIA_IPS_YEXTENT),
        WIA_IPS_XPOS => nameof(WIA_IPS_XPOS),
        WIA_IPS_YPOS => nameof(WIA_IPS_YPOS),
        _ => propertyId.ToString()
    };

    private static void Log(string message)
    {
        try
        {
            Debug.WriteLine($"[AutoSizeScan] {message}");
        }
        catch
        {
            // ignored
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
            lock (LogSync)
            {
                File.AppendAllText(LogFilePath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // ignored
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
