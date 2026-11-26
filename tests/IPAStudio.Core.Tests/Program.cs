namespace IPAStudio.Core.Tests;

using System;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("=== Running Core Unit Tests ===");
        try
        {
            Console.WriteLine("\n--- AirTrafficPhotoSyncTests ---");
            AirTrafficPhotoSyncTests.RunAll();

            Console.WriteLine("\n--- DialogNotificationUpdateTests ---");
            DialogNotificationUpdateTests.RunAll();

            Console.WriteLine("\n--- QuickTransferTests ---");
            QuickTransferTests.RunAll();

            Console.WriteLine("\n--- MediaExportAndIconTests ---");
            MediaExportAndIconTests.RunAll();

            Console.WriteLine("\n--- BugReportTests ---");
            BugReportTests.RunAll();

            Console.WriteLine("\n[ALL C# TESTS PASSED SUCCESSFULLY]");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nTEST FAILED: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
    }
}
