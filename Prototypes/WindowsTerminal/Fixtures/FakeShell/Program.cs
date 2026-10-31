// This deliberately inert client must never satisfy the PowerShell output test.
// It only allows the console to echo input, then exits with the expected code.
System.Console.WriteLine("@@POWERSHELL-READY@@");
System.Console.ReadLine();
return 13;
