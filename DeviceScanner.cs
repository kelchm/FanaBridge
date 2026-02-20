using System;
using System.Linq;
using HidSharp;

namespace FanatecLedControl;

public static class DeviceScanner
{
    public static void ListAllHidDevices()
    {
        Console.WriteLine("Scanning for all HID devices...\n");
        
        var devices = DeviceList.Local.GetHidDevices().ToList();
        
        if (!devices.Any())
        {
            Console.WriteLine("No HID devices found.");
            return;
        }
        
        Console.WriteLine($"Found {devices.Count} HID device(s):\n");
        
        foreach (var device in devices)
        {
            Console.WriteLine($"Device: {device.GetProductName()}");
            Console.WriteLine($"  Manufacturer: {device.GetManufacturer()}");
            Console.WriteLine($"  VID: 0x{device.VendorID:X4}");
            Console.WriteLine($"  PID: 0x{device.ProductID:X4}");
            try
            {
                Console.WriteLine($"  Serial: {device.GetSerialNumber()}");
            }
            catch
            {
                Console.WriteLine($"  Serial: <unavailable>");
            }
            Console.WriteLine($"  Max Output: {device.GetMaxOutputReportLength()} bytes");
            Console.WriteLine($"  Max Input: {device.GetMaxInputReportLength()} bytes");
            Console.WriteLine();
        }
    }
    
    public static void ListFanatecDevices()
    {
        Console.WriteLine("Scanning for Fanatec devices (VID: 0x0EB7)...\n");
        
        var devices = DeviceList.Local.GetHidDevices(0x0EB7).ToList();
        
        if (!devices.Any())
        {
            Console.WriteLine("No Fanatec devices found with VID 0x0EB7.");
            Console.WriteLine("Trying alternate VID 0x0D5F...\n");
            
            devices = DeviceList.Local.GetHidDevices(0x0D5F).ToList();
            
            if (!devices.Any())
            {
                Console.WriteLine("No Fanatec devices found with VID 0x0D5F either.");
                return;
            }
        }
        
        Console.WriteLine($"Found {devices.Count} Fanatec device(s):\n");
        
        foreach (var device in devices)
        {
            Console.WriteLine($"Device: {device.GetProductName()}");
            Console.WriteLine($"  Manufacturer: {device.GetManufacturer()}");
            Console.WriteLine($"  VID: 0x{device.VendorID:X4}");
            Console.WriteLine($"  PID: 0x{device.ProductID:X4}");
            try
            {
                Console.WriteLine($"  Serial: {device.GetSerialNumber()}");
            }
            catch
            {
                Console.WriteLine($"  Serial: <unavailable>");
            }
            Console.WriteLine($"  Max Output: {device.GetMaxOutputReportLength()} bytes");
            Console.WriteLine($"  Path: {device.DevicePath}");
            Console.WriteLine();
        }
    }
}
