# AutoSize Scan

A simple application that scans photos using a scanner and automatically saves the image based on the scanned size, without requiring the user to specify a paper size.

## Features

- Automatic document size detection
- Support for WIA-compatible scanners
- Saves images with actual scanned dimensions
- No manual paper size specification required
- Scanner connectivity testing to filter non-responsive devices
- Device in-use detection with user warnings
- Async scanning with timeout protection

## Technology Stack

- .NET 8
- WPF
- WIA (Windows Image Acquisition) API

## Known Limitations

- **LAN/Network Scanners**: Some network scanners may not work properly with WIA due to driver limitations. The application uses WIA (Windows Image Acquisition) which is the standard Windows scanning API. If your network scanner doesn't appear or fails to scan, it may require TWAIN support or manufacturer-specific drivers.

## Getting Started

1. Ensure your scanner is properly installed and recognized by Windows
2. Run the application
3. Select your scanner from the dropdown
4. Click "Scan Document"
5. The scanned image will be saved to your Desktop with a timestamp

## Scanner Compatibility

The application works best with:
- USB-connected scanners with WIA drivers
- WSD (Web Services for Devices) scanners
- Locally connected flatbed scanners

Network scanners may have limited compatibility depending on their driver implementation.
