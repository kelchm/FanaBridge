using System;
using System.Threading;
using FanatecLedControl;

// Minimal PoC: Connect to wheel and demonstrate LED control

Console.WriteLine("=== Fanatec M4 GT3 LED Control PoC ===\n");

// First, scan for devices to find the correct VID/PID
Console.WriteLine("Step 1: Scanning for Fanatec devices...\n");
DeviceScanner.ListFanatecDevices();

Console.WriteLine("\nPress any key to see ALL HID devices, or 'q' to quit...");
var key = Console.ReadKey(true);
if (key.KeyChar == 'q' || key.KeyChar == 'Q')
{
    Console.WriteLine("Exiting.");
    return;
}

Console.WriteLine("\n");
DeviceScanner.ListAllHidDevices();

Console.WriteLine("\nPress any key to continue with LED demo (if device found), or 'q' to quit...");
key = Console.ReadKey(true);
if (key.KeyChar == 'q' || key.KeyChar == 'Q')
{
    Console.WriteLine("Exiting.");
    return;
}

Console.WriteLine("\n=== Choose Demo Mode ===");
Console.WriteLine("1. Full LED animation showcase");
Console.WriteLine("2. Display gear/speed test (safe 8-byte protocol)");
Console.WriteLine("3. Both (LED animations + display test)");
Console.WriteLine("4. ITM / OLED protocol tests (experimental)");
Console.WriteLine("5. Character chart (cycle through all 7-segment chars)");
Console.Write("\nEnter choice (1-5): ");

var choice = Console.ReadLine()?.Trim();

using (var wheel = new FanatecHidDevice())
{
    if (!wheel.Connect())
    {
        Console.WriteLine("Failed to connect to Fanatec wheel");
        return;
    }

    bool runLedDemo = choice == "1" || choice == "3";
    bool runOledTest = choice == "2" || choice == "3";
    bool runItmTest = choice == "4";
    bool runCharChart = choice == "5";

    if (runCharChart)
    {
        wheel.TestCharacterChart();
    }

    if (runItmTest)
    {
        wheel.TestItmOledProtocol();
    }

    if (runLedDemo)
    {
        Console.WriteLine("\n🎪 FANCY ANIMATION SHOWCASE - All 12 LEDs 🎪\n");
        Thread.Sleep(500);
        
        // Animation 1: Rainbow Wave
        LedAnimations.RainbowWave(wheel, cycles: 3, delayMs: 40);
        Thread.Sleep(300);
        
        // Animation 2: Knight Rider
        LedAnimations.KnightRider(wheel, ColorHelper.Colors.Red, cycles: 3, delayMs: 50);
        Thread.Sleep(300);
        
        // Animation 3: Theater Chase
        LedAnimations.TheaterChase(wheel, ColorHelper.Colors.Cyan, ColorHelper.Colors.Magenta, cycles: 6, delayMs: 80);
        Thread.Sleep(300);
        
        // Animation 4: Color Pulse (all LEDs sync)
        LedAnimations.ColorPulse(wheel, cycles: 2, delayMs: 25);
        Thread.Sleep(300);
        
        // Animation 5: Sparkle
        LedAnimations.Sparkle(wheel, durationMs: 3000, sparkleDelayMs: 50);
        Thread.Sleep(300);
        
        // Animation 6: Side vs Side
        LedAnimations.SideVsSide(wheel, ColorHelper.Colors.Blue, ColorHelper.Colors.Orange, pulses: 8, delayMs: 100);
        Thread.Sleep(300);
        
        // Animation 7: Binary Counter (first 100 numbers)
        LedAnimations.BinaryCounter(wheel, ColorHelper.Colors.Green, ColorHelper.Colors.Black, countTo: 100, delayMs: 80);
        Thread.Sleep(300);
        
        // Finale: Fast rainbow sweep
        Console.WriteLine("  🎆 Grand finale...");
        LedAnimations.RainbowWave(wheel, cycles: 5, delayMs: 20);
        
        // Turn all off
        Console.WriteLine("\n✨ Show complete! Turning off LEDs...");
        var offColors = new ushort[12];
        var offIntensities = new byte[16];
        for (int i = 12; i < 16; i++)
            offIntensities[i] = 7;
        
        wheel.SetColors(offColors, commit: true);
        wheel.SetIntensity(offIntensities, commit: true);
    }

    if (runOledTest)
    {
        Console.WriteLine("\n━━━ DISPLAY GEAR/SPEED TEST ━━━\n");
        wheel.TestDisplayControl();
    }

    Console.WriteLine("\nDisconnecting...");
    wheel.Disconnect();
}

Console.WriteLine("Done!");
