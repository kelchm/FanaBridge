using System;
using System.Threading;

namespace FanatecLedControl;

/// <summary>
/// Collection of LED animation effects for Fanatec wheels
/// </summary>
public static class LedAnimations
{
    /// <summary>
    /// Rainbow wave effect that cycles through all colors across all LEDs
    /// </summary>
    public static void RainbowWave(FanatecHidDevice wheel, int cycles = 3, int delayMs = 50)
    {
        Console.WriteLine("  🌈 Rainbow wave...");
        
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            for (int offset = 0; offset < 360; offset += 10)
            {
                var colors = new ushort[12];
                var intensities = new byte[16];
                
                for (int i = 0; i < 12; i++)
                {
                    int hue = (offset + (i * 30)) % 360;
                    colors[i] = HueToRgb565(hue);
                    intensities[i] = 7;
                }
                
                // Global channels
                for (int i = 12; i < 16; i++)
                    intensities[i] = 7;
                
                wheel.SetColors(colors, commit: true);
                wheel.SetIntensity(intensities, commit: true);
                Thread.Sleep(delayMs);
            }
        }
    }
    
    /// <summary>
    /// Knight Rider / Cylon scanning effect
    /// </summary>
    public static void KnightRider(FanatecHidDevice wheel, ushort color, int cycles = 3, int delayMs = 60)
    {
        Console.WriteLine("  🚗 Knight Rider scanner...");
        
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            // Scan left to right
            for (int pos = 0; pos < 12; pos++)
            {
                var colors = new ushort[12];
                var intensities = new byte[16];
                
                for (int i = 0; i < 12; i++)
                {
                    colors[i] = color;
                    
                    // Create trailing fade effect
                    int distance = Math.Abs(i - pos);
                    if (distance == 0)
                        intensities[i] = 7;
                    else if (distance == 1)
                        intensities[i] = 4;
                    else if (distance == 2)
                        intensities[i] = 2;
                    else
                        intensities[i] = 0;
                }
                
                for (int i = 12; i < 16; i++)
                    intensities[i] = 7;
                
                wheel.SetColors(colors, commit: true);
                wheel.SetIntensity(intensities, commit: true);
                Thread.Sleep(delayMs);
            }
            
            // Scan right to left
            for (int pos = 10; pos >= 1; pos--)
            {
                var colors = new ushort[12];
                var intensities = new byte[16];
                
                for (int i = 0; i < 12; i++)
                {
                    colors[i] = color;
                    
                    int distance = Math.Abs(i - pos);
                    if (distance == 0)
                        intensities[i] = 7;
                    else if (distance == 1)
                        intensities[i] = 4;
                    else if (distance == 2)
                        intensities[i] = 2;
                    else
                        intensities[i] = 0;
                }
                
                for (int i = 12; i < 16; i++)
                    intensities[i] = 7;
                
                wheel.SetColors(colors, commit: true);
                wheel.SetIntensity(intensities, commit: true);
                Thread.Sleep(delayMs);
            }
        }
    }
    
    /// <summary>
    /// Theater marquee chase effect
    /// </summary>
    public static void TheaterChase(FanatecHidDevice wheel, ushort color1, ushort color2, int cycles = 5, int delayMs = 100)
    {
        Console.WriteLine("  🎭 Theater chase...");
        
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            for (int offset = 0; offset < 3; offset++)
            {
                var colors = new ushort[12];
                var intensities = new byte[16];
                
                for (int i = 0; i < 12; i++)
                {
                    colors[i] = (i % 3 == offset) ? color1 : color2;
                    intensities[i] = 7;
                }
                
                for (int i = 12; i < 16; i++)
                    intensities[i] = 7;
                
                wheel.SetColors(colors, commit: true);
                wheel.SetIntensity(intensities, commit: true);
                Thread.Sleep(delayMs);
            }
        }
    }
    
    /// <summary>
    /// Sparkle/twinkle random effect
    /// </summary>
    public static void Sparkle(FanatecHidDevice wheel, int durationMs = 3000, int sparkleDelayMs = 50)
    {
        Console.WriteLine("  ✨ Sparkle...");
        
        var random = new Random();
        var startTime = DateTime.Now;
        
        while ((DateTime.Now - startTime).TotalMilliseconds < durationMs)
        {
            var colors = new ushort[12];
            var intensities = new byte[16];
            
            for (int i = 0; i < 12; i++)
            {
                if (random.Next(100) < 30) // 30% chance to light up
                {
                    colors[i] = HueToRgb565(random.Next(360));
                    intensities[i] = (byte)random.Next(4, 8);
                }
                else
                {
                    colors[i] = ColorHelper.Colors.Black;
                    intensities[i] = 0;
                }
            }
            
            for (int i = 12; i < 16; i++)
                intensities[i] = 7;
            
            wheel.SetColors(colors, commit: true);
            wheel.SetIntensity(intensities, commit: true);
            Thread.Sleep(sparkleDelayMs);
        }
    }
    
    /// <summary>
    /// Color wave that pulses all LEDs together through color spectrum
    /// </summary>
    public static void ColorPulse(FanatecHidDevice wheel, int cycles = 2, int delayMs = 30)
    {
        Console.WriteLine("  💓 Color pulse...");
        
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            for (int hue = 0; hue < 360; hue += 5)
            {
                var colors = new ushort[12];
                var intensities = new byte[16];
                
                ushort color = HueToRgb565(hue);
                
                // All LEDs same color
                for (int i = 0; i < 12; i++)
                {
                    colors[i] = color;
                    intensities[i] = 7;
                }
                
                for (int i = 12; i < 16; i++)
                    intensities[i] = 7;
                
                wheel.SetColors(colors, commit: true);
                wheel.SetIntensity(intensities, commit: true);
                Thread.Sleep(delayMs);
            }
        }
    }
    
    /// <summary>
    /// Binary counter effect
    /// </summary>
    public static void BinaryCounter(FanatecHidDevice wheel, ushort onColor, ushort offColor, int countTo = 4095, int delayMs = 100)
    {
        Console.WriteLine("  🔢 Binary counter...");
        
        for (int value = 0; value <= countTo && value < 4096; value++)
        {
            var colors = new ushort[12];
            var intensities = new byte[16];
            
            for (int i = 0; i < 12; i++)
            {
                bool isOn = (value & (1 << i)) != 0;
                colors[i] = isOn ? onColor : offColor;
                intensities[i] = isOn ? (byte)7 : (byte)1;
            }
            
            for (int i = 12; i < 16; i++)
                intensities[i] = 7;
            
            wheel.SetColors(colors, commit: true);
            wheel.SetIntensity(intensities, commit: true);
            Thread.Sleep(delayMs);
        }
    }
    
    /// <summary>
    /// Side vs side color battle effect
    /// </summary>
    public static void SideVsSide(FanatecHidDevice wheel, ushort leftColor, ushort rightColor, int pulses = 5, int delayMs = 80)
    {
        Console.WriteLine("  ⚔️ Side vs side battle...");
        
        for (int pulse = 0; pulse < pulses; pulse++)
        {
            var colors = new ushort[12];
            var intensities = new byte[16];
            
            // Left side (0-5)
            for (int i = 0; i < 6; i++)
            {
                colors[i] = leftColor;
                intensities[i] = 7;
            }
            
            // Right side (6-11)
            for (int i = 6; i < 12; i++)
            {
                colors[i] = rightColor;
                intensities[i] = 0;
            }
            
            for (int i = 12; i < 16; i++)
                intensities[i] = 7;
            
            wheel.SetColors(colors, commit: true);
            wheel.SetIntensity(intensities, commit: true);
            Thread.Sleep(delayMs);
            
            // Swap
            for (int i = 0; i < 6; i++)
            {
                intensities[i] = 0;
            }
            
            for (int i = 6; i < 12; i++)
            {
                intensities[i] = 7;
            }
            
            wheel.SetIntensity(intensities, commit: true);
            Thread.Sleep(delayMs);
        }
    }
    
    /// <summary>
    /// Converts HSV hue (0-360) to RGB565
    /// </summary>
    private static ushort HueToRgb565(int hue)
    {
        hue = hue % 360;
        double h = hue / 60.0;
        int i = (int)Math.Floor(h);
        double f = h - i;
        
        byte v = 255;
        byte p = 0;
        byte q = (byte)(v * (1 - f));
        byte t = (byte)(v * f);
        
        return (i % 6) switch
        {
            0 => ColorHelper.RgbToRgb565(v, t, p),
            1 => ColorHelper.RgbToRgb565(q, v, p),
            2 => ColorHelper.RgbToRgb565(p, v, t),
            3 => ColorHelper.RgbToRgb565(p, q, v),
            4 => ColorHelper.RgbToRgb565(t, p, v),
            5 => ColorHelper.RgbToRgb565(v, p, q),
            _ => ColorHelper.RgbToRgb565(v, p, p)
        };
    }
}

