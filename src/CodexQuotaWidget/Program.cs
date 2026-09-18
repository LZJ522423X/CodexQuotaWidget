using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget;
public static class Program
{
    [DllImport("kernel32.dll")]static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")]static extern IntPtr GetStdHandle(int kind);
    [STAThread]
    public static int Main(string[] args)
    {
        if(args.Contains("--check") || args.Contains("--once"))
        {
            var handle=GetStdHandle(-11);
            if(handle==IntPtr.Zero || handle==new IntPtr(-1))AttachConsole(uint.MaxValue);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()){AutoFlush=true});
            return HeadlessCommand.Run(args).GetAwaiter().GetResult();
        }
        var app=new App();app.InitializeComponent();return app.Run();
    }
}
