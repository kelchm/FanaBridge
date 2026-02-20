using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace FanatecLedControl;

/// <summary>
/// HID communication layer for Fanatec wheel LED control
/// </summary>
public class FanatecHidDevice : IDisposable
{
    private const ushort FANATEC_VENDOR_ID = 0x0EB7;
    private const ushort CLUBSPORT_DD_PRODUCT_ID = 0x0020; // ClubSport DD+
    private const int LED_REPORT_LENGTH = 64;
    private const int OLED_INTERRUPT_LENGTH = 8;

    private HidDevice? _ledDevice;       // Interface with 64-byte output (for LED control)
    private HidStream? _ledStream;
    
    private HidDevice? _oledCtrlDevice;  // Interface for display/config - col01
    private HidStream? _col01Stream;      // HidStream for col01 (interrupt OUT)

    // Windows API for fallback WriteFile transport
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    private SafeFileHandle? _oledCtrlHandle;

    /// <summary>
    /// Attempts to connect to the Fanatec wheel
    /// </summary>
    public bool Connect()
    {
        try
        {
            // First, let's see ALL Fanatec devices (any PID)
            var allFanatec = DeviceList.Local.GetHidDevices()
                .Where(d => d.VendorID == FANATEC_VENDOR_ID)
                .ToList();
            
            Console.WriteLine($"\n=== ALL Fanatec HID devices (VID: 0x{FANATEC_VENDOR_ID:X4}) ===");
            foreach (var dev in allFanatec)
            {
                Console.WriteLine($"  PID=0x{dev.ProductID:X4}, MaxOut={dev.GetMaxOutputReportLength()}, MaxIn={dev.GetMaxInputReportLength()}");
                Console.WriteLine($"       Path: {dev.DevicePath}");
            }
            
            // Now filter to our specific PID
            var devices = allFanatec.Where(d => d.ProductID == CLUBSPORT_DD_PRODUCT_ID).ToList();
            
            Console.WriteLine($"\n=== Devices matching PID 0x{CLUBSPORT_DD_PRODUCT_ID:X4} ===");
            for (int i = 0; i < devices.Count; i++)
            {
                var dev = devices[i];
                Console.WriteLine($"  [{i}] MaxOut={dev.GetMaxOutputReportLength()}, MaxIn={dev.GetMaxInputReportLength()}, MaxFeat={dev.GetMaxFeatureReportLength()}");
                Console.WriteLine($"       Path: {dev.DevicePath}");
            }
            
            // Find interfaces by collection (from device path)
            // col01 = display/config (8-byte, interrupt OUT), col03 = LED control (64-byte)
            _ledDevice = devices.FirstOrDefault(d => d.DevicePath.Contains("col03"));
            _oledCtrlDevice = devices.FirstOrDefault(d => d.DevicePath.Contains("col01"));
            
            // Fallback: find by max output length
            if (_ledDevice == null)
            {
                _ledDevice = devices.FirstOrDefault(d => d.GetMaxOutputReportLength() == LED_REPORT_LENGTH || 
                                                          d.GetMaxOutputReportLength() == LED_REPORT_LENGTH + 1);
            }
            
            if (_ledDevice == null)
            {
                Console.WriteLine("No suitable LED control interface found");
                return false;
            }
            
            _ledStream = _ledDevice.Open();
            Console.WriteLine($"\nConnected to LED interface (col03): MaxOutput={_ledDevice.GetMaxOutputReportLength()}");

            // Open raw handle for col01 via Windows API (WriteFile fallback)
            if (_oledCtrlDevice != null)
            {
                _oledCtrlHandle = CreateFile(
                    _oledCtrlDevice.DevicePath,
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);
                
                if (_oledCtrlHandle.IsInvalid)
                {
                    Console.WriteLine($"Failed to open OLED control interface (col01): Error {Marshal.GetLastWin32Error()}");
                    _oledCtrlHandle = null;
                }
                else
                {
                    Console.WriteLine($"Opened col01 handle (WriteFile fallback): MaxOutput={_oledCtrlDevice.GetMaxOutputReportLength()}");
                }
            }

            // Open HidStream for col01 (interrupt OUT — primary transport)
            if (_oledCtrlDevice != null)
            {
                try
                {
                    _col01Stream = _oledCtrlDevice.Open();
                    Console.WriteLine($"Opened col01 stream (interrupt OUT)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to open col01 stream: {ex.Message}");
                }
            }

            Console.WriteLine($"\nConnected to: {_ledDevice.GetProductName()}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Connection error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sends a raw HID report via interrupt OUT (64-byte for LEDs)
    /// </summary>
    private bool SendReport(byte[] data)
    {
        if (_ledStream == null || !_ledStream.CanWrite)
        {
            Console.WriteLine("LED device not connected");
            return false;
        }

        if (data.Length != LED_REPORT_LENGTH)
        {
            throw new ArgumentException($"Report must be exactly {LED_REPORT_LENGTH} bytes");
        }

        try
        {
            _ledStream.Write(data);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Send error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sends an 8-byte report for display/config commands via interrupt OUT on col01.
    /// Testing confirmed: the wheel display only responds to interrupt OUT transfers,
    /// NOT control transfers (HidD_SetOutputReport). HidStream.Write on col01 is the
    /// correct transport method.
    /// </summary>
    private bool SendDisplayReport(byte[] data)
    {
        if (data.Length != OLED_INTERRUPT_LENGTH)
        {
            throw new ArgumentException($"Display report must be exactly {OLED_INTERRUPT_LENGTH} bytes");
        }

        // Primary: col01 HidStream (interrupt OUT) — confirmed working
        if (_col01Stream != null)
        {
            try
            {
                _col01Stream.Write(data);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"col01 stream write failed: {ex.Message}");
            }
        }

        // Fallback: col01 WriteFile (also interrupt OUT — confirmed working)
        if (_oledCtrlHandle != null && !_oledCtrlHandle.IsInvalid)
        {
            if (WriteFile(_oledCtrlHandle, data, (uint)data.Length, out _, IntPtr.Zero))
            {
                return true;
            }
            Console.WriteLine($"col01 WriteFile failed: Error {Marshal.GetLastWin32Error()}");
        }

        Console.WriteLine("No interface available for display report");
        return false;
    }

    /// <summary>
    /// Sets LED brightness (intensity) for the specified buttons
    /// </summary>
    /// <param name="intensities">Array of 16 intensity values (I0-I15), scale 0-7</param>
    /// <param name="commit">If true, applies the changes; if false, stages them</param>
    public bool SetIntensity(byte[] intensities, bool commit = true)
    {
        if (intensities.Length != 16)
            throw new ArgumentException("Must provide exactly 16 intensity values");

        var report = new byte[LED_REPORT_LENGTH];
        report[0] = 0xFF;
        report[1] = 0x01;
        report[2] = 0x03;

        // Copy intensity values
        Array.Copy(intensities, 0, report, 3, 16);

        // Apply flag: 0x00 = stage, 0x01 = commit
        report[18] = commit ? (byte)0x01 : (byte)0x00;

        return SendReport(report);
    }

    /// <summary>
    /// Sets LED colors (RGB565, big-endian) for the specified buttons
    /// </summary>
    /// <param name="colors">Array of 12 RGB565 colors (S0-S11)</param>
    /// <param name="commit">If true, applies the changes; if false, stages them</param>
    public bool SetColors(ushort[] colors, bool commit = true)
    {
        if (colors.Length != 12)
            throw new ArgumentException("Must provide exactly 12 color values");

        var report = new byte[LED_REPORT_LENGTH];
        report[0] = 0xFF;
        report[1] = 0x01;
        report[2] = 0x02;

        // Copy RGB565 colors (big-endian)
        for (int i = 0; i < 12; i++)
        {
            int offset = 3 + (i * 2);
            report[offset] = (byte)((colors[i] >> 8) & 0xFF);      // MSB
            report[offset + 1] = (byte)(colors[i] & 0xFF);         // LSB
        }

        // Apply flag: 0x00 = stage, 0x01 = commit
        report[27] = commit ? (byte)0x01 : (byte)0x00;

        return SendReport(report);
    }

    /// <summary>
    /// Convenience method to set a single LED's color and intensity
    /// </summary>
    /// <param name="ledIndex">LED index (0-11)</param>
    /// <param name="color">RGB565 color value</param>
    /// <param name="intensity">Brightness (0-7)</param>
    public bool SetLed(int ledIndex, ushort color, byte intensity)
    {
        if (ledIndex < 0 || ledIndex > 11)
            throw new ArgumentOutOfRangeException(nameof(ledIndex), "LED index must be 0-11");

        if (intensity > 7)
            throw new ArgumentOutOfRangeException(nameof(intensity), "Intensity must be 0-7");

        // Create color array with existing colors (or default to off)
        var colors = new ushort[12];
        colors[ledIndex] = color;

        // Create intensity array
        var intensities = new byte[16];
        for (int i = 0; i < 12; i++)
        {
            intensities[i] = (i == ledIndex) ? intensity : (byte)0;
        }
        // Keep global channels (I12-I15) at max
        for (int i = 12; i < 16; i++)
        {
            intensities[i] = 0x07;
        }

        // Stage and commit
        SetColors(colors, commit: false);
        SetColors(colors, commit: true);
        SetIntensity(intensities, commit: false);
        return SetIntensity(intensities, commit: true);
    }

    /// <summary>
    /// Update only the intensity of a single LED without affecting others
    /// Useful for animations like pulsing where color stays constant
    /// </summary>
    /// <param name="ledIndex">LED index (0-11)</param>
    /// <param name="intensity">Brightness (0-7)</param>
    public bool SetLedIntensity(int ledIndex, byte intensity)
    {
        if (ledIndex < 0 || ledIndex > 11)
            throw new ArgumentOutOfRangeException(nameof(ledIndex), "LED index must be 0-11");

        if (intensity > 7)
            throw new ArgumentOutOfRangeException(nameof(intensity), "Intensity must be 0-7");

        // Only update the specific LED's intensity
        var intensities = new byte[16];
        intensities[ledIndex] = intensity;
        
        // Keep global channels at max
        for (int i = 12; i < 16; i++)
        {
            intensities[i] = 0x07;
        }

        // Just update intensity, no staging needed for single updates
        return SetIntensity(intensities, commit: true);
    }

    // ===== DISPLAY CONTROL =====
    // Based on the Linux hid-fanatecff kernel driver (ftec_set_display):
    //   The display is a 3-digit 7-segment display controlled via SET_REPORT.
    //   Report format: [ReportID=0x01] [0xF8] [0x09] [0x01] [0x02] [seg1] [seg2] [seg3]
    //   No "overlay enable/disable" is needed — just send segments directly.
    //   Values are right-justified (single char goes to seg3, two chars to seg2+seg3).

    /// <summary>
    /// Sends a display update with up to 3 seven-segment digit codes.
    /// This matches the Linux kernel driver's ftec_set_display() exactly:
    ///   value[0]=0xF8, value[1]=0x09, value[2]=0x01, value[3]=0x02,
    ///   value[4]=seg1, value[5]=seg2, value[6]=seg3
    /// Sent as 8-byte SET_REPORT on col01 (report ID 0x01).
    /// </summary>
    public bool SetDisplay(byte seg1, byte seg2, byte seg3)
    {
        var report = new byte[OLED_INTERRUPT_LENGTH];
        report[0] = 0x01;  // Report ID
        report[1] = 0xF8;  // Command prefix
        report[2] = 0x09;  // Sub-command
        report[3] = 0x01;  // Sub-command
        report[4] = 0x02;  // Display sub-command
        report[5] = seg1;  // Left digit
        report[6] = seg2;  // Center digit
        report[7] = seg3;  // Right digit

        Console.WriteLine($"  SetDisplay: [{seg1:X2}] [{seg2:X2}] [{seg3:X2}]  raw: {BitConverter.ToString(report).Replace("-", " ")}");
        return SendDisplayReport(report);
    }

    /// <summary>
    /// Turns the display off by sending blank segments.
    /// The Linux kernel driver uses a negative value to turn off;
    /// we send all-blank segments which achieves the same visual result.
    /// </summary>
    public bool ClearDisplay()
    {
        return SetDisplay(SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank);
    }

    /// <summary>
    /// 7-segment display encoding. Matches the Linux kernel driver's segbits[] table.
    ///   seg 0
    ///  ───────
    /// │       │
    /// 5       1
    /// │       │
    ///  ───────  seg 6
    /// │       │
    /// 4       2
    /// │       │
    ///  ───────  • seg 7 (dot)
    ///   seg 3
    /// </summary>
    public static class SevenSegment
    {
        // Digits
        public const byte Digit0 = 0x3F; //  63
        public const byte Digit1 = 0x06; //   6
        public const byte Digit2 = 0x5B; //  91
        public const byte Digit3 = 0x4F; //  79
        public const byte Digit4 = 0x66; // 102
        public const byte Digit5 = 0x6D; // 109
        public const byte Digit6 = 0x7D; // 125
        public const byte Digit7 = 0x07; //   7
        public const byte Digit8 = 0x7F; // 127
        public const byte Digit9 = 0x6F; // 103

        // Symbols (verified against SDK FSCmdLedSevenSegmentDisplayThreeDigits)
        public const byte Dot    = 0x80; // 128  (decimal point only)
        public const byte Blank  = 0x00; //   0
        public const byte Dash   = 0x40; //  64  -  (not in SDK, but standard)
        public const byte Under  = 0x08; //   8  _
        public new const byte Equals = 0x48; //  72  =  (bottom + middle bars)
        public const byte Plus   = 0x70; // 112  +  (left verticals + middle)
        public const byte Colon  = 0x41; //  65  :  (top + middle bars)
        public const byte Exclam = 0x09; //   9  !  (top + bottom bars)
        public const byte Quest  = 0x53; //  83  ?  (top + right-top + left-bot + middle)
        public const byte Slash  = 0x52; //  82  /  (right-top + left-bot + middle)
        public const byte BSlash = 0x64; // 100  \  (right-bot + left-top + middle)
        public const byte Caret  = 0x03; //   3  ^  (top + right-top)
        public const byte LParen = 0x21; //  33  (  (top + left-top)
        public const byte RParen = 0x0C; //  12  )  (right-bot + bottom)
        public const byte LBrack = 0x39; //  57  [
        public const byte RBrack = 0x0F; //  15  ]
        public const byte Hash   = 0x46; //  70  #  (right verticals + middle)
        public const byte Star   = 0x02; //   2  *  (right-top only)
        public const byte Amper  = 0x62; //  98  &  (right-top + left-top + middle)
        public const byte Pct    = 0x0D; //  13  %  (top + right-bot + bottom)
        public const byte LAngle = 0x14; //  20  <  (right-bot + left-bot)
        public const byte RAngle = 0x62; //  98  >  (SDK: same as &)
        public const byte Semi   = 0x04; //   4  ;  (right-bot only)

        // Letters — verified against SDK SetChar2 (FSCmdLedSevenSegmentDisplayThreeDigits)
        // SDK uses lowercase-style representations for most letters.
        public const byte A = 0x77; // 119
        public const byte B = 0x7C; // 124
        public const byte C = 0x58; //  88  (lowercase c)
        public const byte D = 0x5E; //  94
        public const byte E = 0x79; // 121
        public const byte F = 0x71; // 113
        public const byte G = 0x3D; //  61  (SDK uses 0x7D = same as 6; ours is more distinct)
        public const byte H = 0x76; // 118
        public const byte I = 0x06; //   6  (right-side; aligned with SDK)
        public const byte J = 0x0E; //  14  (SDK uses 0x06 = same as I; ours adds bottom bar)
        public const byte K = 0x75; // 117  (top + right-bot + left-bot + left-top + middle)
        public const byte L = 0x38; //  56
        public const byte M = 0x37; //  55  (top + right verticals + left verticals, no middle/bottom)
        public const byte N = 0x54; //  84
        public const byte O = 0x5C; //  92  (lowercase o)
        public const byte P = 0x73; // 115
        public const byte Q = 0x67; // 103
        public const byte R = 0x50; //  80
        public const byte S = 0x6D; // 109  (same as 5)
        public const byte T = 0x78; // 120
        public const byte U = 0x3E; //  62
        public const byte V = 0x18; //  24  (bottom + left-bot; best effort)
        public const byte W = 0x7E; // 126  (everything except top; like U + middle)
        public const byte X = 0x76; // 118  (same as H; standard 7-seg compromise)
        public const byte Y = 0x6E; // 110
        public const byte Z = 0x5B; //  91  (same as 2)

        // Aliases for telemetry display
        public const byte Neutral = N;
        public const byte Reverse = R;

        /// <summary>
        /// Converts an ASCII character to its 7-segment code.
        /// Full A-Z, 0-9, and extended symbols. Verified against Fanatec SDK.
        /// A trailing '.' or ',' after a character OR's the dot bit onto the previous segment.
        /// </summary>
        public static byte CharToSegment(char ch)
        {
            return char.ToUpper(ch) switch
            {
                '0' => Digit0, '1' => Digit1, '2' => Digit2, '3' => Digit3, '4' => Digit4,
                '5' => Digit5, '6' => Digit6, '7' => Digit7, '8' => Digit8, '9' => Digit9,
                'A' => A, 'B' => B, 'C' => C, 'D' => D, 'E' => E, 'F' => F,
                'G' => G, 'H' => H, 'I' => I, 'J' => J, 'K' => K, 'L' => L,
                'M' => M, 'N' => N, 'O' => O, 'P' => P, 'Q' => Q, 'R' => R,
                'S' => S, 'T' => T, 'U' => U, 'V' => V, 'W' => W, 'X' => X,
                'Y' => Y, 'Z' => Z,
                '[' => LBrack, ']' => RBrack,
                '(' => LParen, ')' => RParen,
                '-' => Dash, '_' => Under, '=' => Equals, '+' => Plus,
                ':' => Colon, ';' => Semi,
                '!' => Exclam, '?' => Quest,
                '/' => Slash, '\\' => BSlash,
                '^' => Caret, '#' => Hash, '*' => Star,
                '&' => Amper, '%' => Pct,
                '<' => LAngle, '>' => RAngle,
                '.' => Dot, ',' => Dot,
                ' ' => Blank,
                _ => Blank
            };
        }
    }

    /// <summary>
    /// Displays a gear number (0=N, 1-9, -1=R) on the display.
    /// Single character, displayed centered.
    /// </summary>
    public bool DisplayGear(int gear)
    {
        byte segmentCode = gear switch
        {
            -1 => SevenSegment.Reverse,  // R
            0 => SevenSegment.Neutral,    // N
            1 => SevenSegment.Digit1,
            2 => SevenSegment.Digit2,
            3 => SevenSegment.Digit3,
            4 => SevenSegment.Digit4,
            5 => SevenSegment.Digit5,
            6 => SevenSegment.Digit6,
            7 => SevenSegment.Digit7,
            8 => SevenSegment.Digit8,
            9 => SevenSegment.Digit9,
            _ => SevenSegment.Neutral
        };

        string label = gear switch { -1 => "R", 0 => "N", _ => gear.ToString() };
        Console.WriteLine($"  DisplayGear: {label} (0x{segmentCode:X2})");

        // Display centered on the middle digit
        return SetDisplay(SevenSegment.Blank, segmentCode, SevenSegment.Blank);
    }

    /// <summary>
    /// Displays speed as a number on the 3-digit display (0-999).
    /// </summary>
    public bool DisplaySpeed(int speed)
    {
        if (speed < 0 || speed > 999)
            speed = 0;

        byte seg1 = GetDigitSegment(speed / 100);
        byte seg2 = GetDigitSegment((speed / 10) % 10);
        byte seg3 = GetDigitSegment(speed % 10);

        Console.WriteLine($"  DisplaySpeed: {speed:D3}");
        return SetDisplay(seg1, seg2, seg3);
    }

    /// <summary>
    /// Displays up to 3 characters of text. Handles dots/commas by OR'ing
    /// the dot bit onto the preceding character's segment code.
    /// </summary>
    public bool DisplayText(string text)
    {
        // Parse text into up to 3 segment bytes, folding dots onto previous char
        var segs = new List<byte>();
        foreach (char ch in text)
        {
            if ((ch == '.' || ch == ',') && segs.Count > 0)
            {
                // OR dot bit onto the previous segment
                segs[segs.Count - 1] |= SevenSegment.Dot;
            }
            else
            {
                segs.Add(SevenSegment.CharToSegment(ch));
            }
            if (segs.Count >= 3) break;
        }

        // Pad to 3 segments
        while (segs.Count < 3) segs.Add(SevenSegment.Blank);

        return SetDisplay(segs[0], segs[1], segs[2]);
    }

    /// <summary>
    /// Scrolls a message across the 3-character display.
    /// Pads with spaces so the message slides in from the right and out to the left.
    /// </summary>
    /// <param name="message">Text to scroll</param>
    /// <param name="delayMs">Milliseconds between each scroll step</param>
    /// <param name="loops">Number of complete scroll cycles</param>
    public void ScrollText(string message, int delayMs = 300, int loops = 1)
    {
        // Pre-encode all characters (handling dots)
        var encoded = new List<byte>();
        for (int i = 0; i < message.Length; i++)
        {
            char ch = message[i];
            if ((ch == '.' || ch == ',') && encoded.Count > 0)
            {
                encoded[encoded.Count - 1] |= SevenSegment.Dot;
            }
            else
            {
                encoded.Add(SevenSegment.CharToSegment(ch));
            }
        }

        // Pad with 3 blanks on each side for slide-in/slide-out
        var padded = new List<byte>();
        padded.Add(SevenSegment.Blank);
        padded.Add(SevenSegment.Blank);
        padded.Add(SevenSegment.Blank);
        padded.AddRange(encoded);
        padded.Add(SevenSegment.Blank);
        padded.Add(SevenSegment.Blank);
        padded.Add(SevenSegment.Blank);

        for (int loop = 0; loop < loops; loop++)
        {
            for (int pos = 0; pos <= padded.Count - 3; pos++)
            {
                SetDisplay(padded[pos], padded[pos + 1], padded[pos + 2]);
                Thread.Sleep(delayMs);
            }
        }
    }

    private byte GetDigitSegment(int digit)
    {
        return digit switch
        {
            0 => SevenSegment.Digit0,
            1 => SevenSegment.Digit1,
            2 => SevenSegment.Digit2,
            3 => SevenSegment.Digit3,
            4 => SevenSegment.Digit4,
            5 => SevenSegment.Digit5,
            6 => SevenSegment.Digit6,
            7 => SevenSegment.Digit7,
            8 => SevenSegment.Digit8,
            9 => SevenSegment.Digit9,
            _ => SevenSegment.Blank
        };
    }

    /// <summary>
    /// Iterates through every character in the SevenSegment font, displaying each
    /// centered on the display with its label shown in the console.
    /// </summary>
    public void TestCharacterChart(int holdMs = 600)
    {
        Console.WriteLine("=== 7-SEGMENT CHARACTER CHART ===");
        Console.WriteLine("Displaying each character on the wheel...\n");

        // Digits
        for (char c = '0'; c <= '9'; c++)
        {
            byte seg = SevenSegment.CharToSegment(c);
            Console.WriteLine($"  '{c}'  =>  0x{seg:X2}");
            SetDisplay(SevenSegment.Blank, seg, SevenSegment.Blank);
            Thread.Sleep(holdMs);
        }

        // Letters A-Z
        for (char c = 'A'; c <= 'Z'; c++)
        {
            byte seg = SevenSegment.CharToSegment(c);
            Console.WriteLine($"  '{c}'  =>  0x{seg:X2}");
            SetDisplay(SevenSegment.Blank, seg, SevenSegment.Blank);
            Thread.Sleep(holdMs);
        }

        // Symbols
        char[] symbols = { '-', '_', '=', '+', ':', ';', '!', '?', '/', '\\',
                           '^', '(', ')', '[', ']', '#', '*', '&', '%', '<', '>', '.' };
        foreach (char c in symbols)
        {
            byte seg = SevenSegment.CharToSegment(c);
            Console.WriteLine($"  '{c}'  =>  0x{seg:X2}");
            SetDisplay(SevenSegment.Blank, seg, SevenSegment.Blank);
            Thread.Sleep(holdMs);
        }

        ClearDisplay();
        Console.WriteLine("\nCharacter chart complete!");
    }

    /// <summary>
    /// Demo: showcases display features — scrolling text, static text, gears, speed.
    /// </summary>
    public void TestDisplayControl()
    {
        Console.WriteLine("=== DISPLAY DEMO ===\n");

        Console.WriteLine("Scrolling message...");
        ScrollText("HELLO WORLD  ", delayMs: 250, loops: 2);

        Console.WriteLine("\nText display demo...");
        DisplayText("Hi "); Thread.Sleep(1000);
        DisplayText("P1t"); Thread.Sleep(1000);
        DisplayText("F.3"); Thread.Sleep(1000);
        DisplayText("tc2"); Thread.Sleep(1000);

        Console.WriteLine("\nGear cycle...");
        int[] gears = { 0, 1, 2, 3, 4, 5, 6 };
        foreach (int gear in gears)
        {
            DisplayGear(gear);
            Thread.Sleep(800);
        }

        Console.WriteLine("\nSpeed demo 0 -> 200...");
        for (int spd = 0; spd <= 200; spd += 10)
        {
            DisplaySpeed(spd);
            Thread.Sleep(150);
        }

        Thread.Sleep(300);
        ClearDisplay();
        Console.WriteLine("\nDone!");
    }

    // ===== ITM (In-Tuning-Menu) PROTOCOL =====
    // ITM commands go over col03 (64-byte reports, report ID 0xFF).
    // Discovered via reverse engineering of EndorFanatecSdk32_VS2019.dll (FSCmdITM class).
    // Command structure: [FF] [05] [sub-cmd] [data...]
    //   Sub-cmd 01: ValueUpdate (parameter data, type 1)
    //   Sub-cmd 02: Enable/Subscribe toggle
    //   Sub-cmd 03: ValueUpdate (parameter data, type 2)
    //   Sub-cmd 04: PageSet (select tuning page)
    //   Sub-cmd 05 01: DisplayReset

    /// <summary>
    /// Sends an ITM command on col03. The report is 64 bytes: [FF, 05, sub-cmd, data...].
    /// </summary>
    private bool SendItmCommand(byte subCmd, params byte[] data)
    {
        var report = new byte[LED_REPORT_LENGTH];
        report[0] = 0xFF;  // Report ID
        report[1] = 0x05;  // ITM command class
        report[2] = subCmd;
        for (int i = 0; i < data.Length && (3 + i) < LED_REPORT_LENGTH; i++)
            report[3 + i] = data[i];

        Console.WriteLine($"  ITM cmd: {BitConverter.ToString(report, 0, Math.Min(3 + data.Length + 1, 16)).Replace("-", " ")}");
        return SendReport(report);
    }

    /// <summary>
    /// ITM DisplayReset — sent by the SDK during init and teardown.
    /// Command: {FF, 05, 05, 01}
    /// </summary>
    public bool ItmDisplayReset()
    {
        Console.WriteLine("  Sending ITM DisplayReset...");
        return SendItmCommand(0x05, 0x01);
    }

    /// <summary>
    /// ITM Enable/Subscribe — toggles the ITM display mode.
    /// Command: {FF, 05, 02, enable}
    /// enable=1 to subscribe (enable tuning display), enable=0 to unsubscribe.
    /// </summary>
    public bool ItmEnable(bool enable)
    {
        Console.WriteLine($"  Sending ITM Enable({enable})...");
        return SendItmCommand(0x02, enable ? (byte)0x01 : (byte)0x00);
    }

    /// <summary>
    /// ITM PageSet — selects a tuning page (0-6).
    /// Command: {FF, 05, 04, page, count}
    /// The SDK validates page 0-6. Count defaults to 1.
    /// </summary>
    public bool ItmPageSet(byte page, byte count = 1)
    {
        Console.WriteLine($"  Sending ITM PageSet(page={page}, count={count})...");
        return SendItmCommand(0x04, page, count);
    }

    // ===== COL01 EXTENDED COMMANDS =====
    // These use the same 8-byte col01 report format: [01, F8, 09, 01, sub-cmd, data...]

    /// <summary>
    /// Sends a raw col01 sub-command. Report: [01, F8, 09, 01, subCmd, d0, d1, d2].
    /// </summary>
    private bool SendCol01Command(byte subCmd, byte d0 = 0, byte d1 = 0, byte d2 = 0)
    {
        var report = new byte[OLED_INTERRUPT_LENGTH];
        report[0] = 0x01;  // Report ID
        report[1] = 0xF8;
        report[2] = 0x09;
        report[3] = 0x01;
        report[4] = subCmd;
        report[5] = d0;
        report[6] = d1;
        report[7] = d2;

        Console.WriteLine($"  col01 cmd: {BitConverter.ToString(report).Replace("-", " ")}");
        return SendDisplayReport(report);
    }

    /// <summary>
    /// SevenSegmentModeEnable — toggles 7-segment display mode on OLED wheels.
    /// The SDK sends mode+1 (i.e., enable=true sends 0x02, enable=false sends 0x01).
    /// When IsSevenSegmentOLED=false, the SDK skips this entirely.
    /// Command: [01, F8, 09, 01, 18, mode, 00, 00]
    /// </summary>
    public bool SevenSegmentModeEnable(bool enable)
    {
        byte mode = enable ? (byte)0x02 : (byte)0x01;
        Console.WriteLine($"  Sending SevenSegmentModeEnable(enable={enable}, mode=0x{mode:X2})...");
        return SendCol01Command(0x18, mode);
    }

    /// <summary>
    /// OledPixelTest — triggers a built-in OLED test pattern.
    /// Command: [01, F8, 09, 01, 50, pattern, 00, 00]
    /// </summary>
    public bool OledPixelTest(byte pattern)
    {
        Console.WriteLine($"  Sending OledPixelTest(pattern=0x{pattern:X2})...");
        return SendCol01Command(0x50, pattern);
    }

    /// <summary>
    /// HostHelloMessageSend — device handshake.
    /// Command: [01, F8, 09, 01, A0, 00, 00, 00]
    /// </summary>
    public bool HostHelloMessageSend()
    {
        Console.WriteLine("  Sending HostHelloMessageSend...");
        return SendCol01Command(0xA0);
    }

    /// <summary>
    /// UsbReportTrigger — triggers USB report generation.
    /// The SDK sends enable_flag and -(enable!=0) as the two data bytes.
    /// Command: [01, F8, 09, 01, 06, flag, enable, 00]
    /// </summary>
    public bool UsbReportTrigger(bool enable)
    {
        byte flag = enable ? (byte)0x01 : (byte)0x00;
        byte enableByte = enable ? (byte)0xFF : (byte)0x00;
        Console.WriteLine($"  Sending UsbReportTrigger(enable={enable})...");
        return SendCol01Command(0x06, flag, enableByte);
    }

    /// <summary>
    /// Interactive test menu for ITM and OLED protocol exploration.
    /// </summary>
    public void TestItmOledProtocol()
    {
        Console.WriteLine("\n=== ITM / OLED PROTOCOL TESTS ===");
        Console.WriteLine("These commands were reverse-engineered from the Fanatec SDK.\n");

        while (true)
        {
            Console.WriteLine("\n--- ITM Commands (col03, 64-byte) ---");
            Console.WriteLine("  1. ITM DisplayReset       {FF 05 05 01}");
            Console.WriteLine("  2. ITM Enable (subscribe) {FF 05 02 01}");
            Console.WriteLine("  3. ITM Disable (unsub)    {FF 05 02 00}");
            Console.WriteLine("  4. ITM PageSet            {FF 05 04 <page> <count>}");
            Console.WriteLine("--- col01 Commands (8-byte) ---");
            Console.WriteLine("  5. SevenSegmentModeEnable(true)   {F8 09 01 18 02}");
            Console.WriteLine("  6. SevenSegmentModeEnable(false)  {F8 09 01 18 01}");
            Console.WriteLine("  7. OledPixelTest(pattern)         {F8 09 01 50 xx}");
            Console.WriteLine("  8. HostHelloMessageSend           {F8 09 01 A0}");
            Console.WriteLine("  9. UsbReportTrigger(enable)       {F8 09 01 06 xx}");
            Console.WriteLine("--- Sequences ---");
            Console.WriteLine("  A. Full ITM init (Reset → Enable → PageSet 0)");
            Console.WriteLine("  B. Full ITM teardown (Reset → Disable)");
            Console.WriteLine("  C. Sweep OledPixelTest 0x00-0x0F");
            Console.WriteLine("  Q. Back to main menu");
            Console.Write("\nChoice: ");

            var input = Console.ReadLine()?.Trim().ToUpper();
            if (string.IsNullOrEmpty(input)) continue;

            switch (input)
            {
                case "1":
                    ItmDisplayReset();
                    break;

                case "2":
                    ItmEnable(true);
                    break;

                case "3":
                    ItmEnable(false);
                    break;

                case "4":
                    Console.Write("  Page (0-6): ");
                    if (byte.TryParse(Console.ReadLine()?.Trim(), out byte page) && page <= 6)
                        ItmPageSet(page);
                    else
                        Console.WriteLine("  Invalid page (must be 0-6)");
                    break;

                case "5":
                    SevenSegmentModeEnable(true);
                    break;

                case "6":
                    SevenSegmentModeEnable(false);
                    break;

                case "7":
                    Console.Write("  Pattern byte (hex, e.g. 00, 01, FF): ");
                    var patStr = Console.ReadLine()?.Trim();
                    if (patStr != null && byte.TryParse(patStr, System.Globalization.NumberStyles.HexNumber, null, out byte pat))
                        OledPixelTest(pat);
                    else
                        Console.WriteLine("  Invalid hex byte");
                    break;

                case "8":
                    HostHelloMessageSend();
                    break;

                case "9":
                    Console.Write("  Enable? (y/n): ");
                    var en = Console.ReadLine()?.Trim().ToLower() == "y";
                    UsbReportTrigger(en);
                    break;

                case "A":
                    Console.WriteLine("\n  --- Full ITM Init Sequence ---");
                    ItmDisplayReset();
                    Thread.Sleep(200);
                    ItmEnable(true);
                    Thread.Sleep(200);
                    ItmPageSet(0);
                    Console.WriteLine("  --- Init complete ---");
                    break;

                case "B":
                    Console.WriteLine("\n  --- Full ITM Teardown ---");
                    ItmDisplayReset();
                    Thread.Sleep(200);
                    ItmEnable(false);
                    Console.WriteLine("  --- Teardown complete ---");
                    break;

                case "C":
                    Console.WriteLine("\n  --- Sweeping OledPixelTest 0x00-0x0F ---");
                    for (byte p = 0; p <= 0x0F; p++)
                    {
                        OledPixelTest(p);
                        Console.Write("  Press Enter for next, Q to stop... ");
                        var resp = Console.ReadLine()?.Trim().ToUpper();
                        if (resp == "Q") break;
                    }
                    Console.WriteLine("  --- Sweep complete ---");
                    break;

                case "Q":
                    return;

                default:
                    Console.WriteLine("  Unknown option");
                    break;
            }
        }
    }

    public void Disconnect()
    {
        _col01Stream?.Close();
        _col01Stream?.Dispose();
        _ledStream?.Close();
        _ledStream?.Dispose();
        _oledCtrlHandle?.Close();
        _oledCtrlHandle?.Dispose();
    }

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
