using System;
using System.Windows.Forms;

namespace SourceGeneratorPlayground
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        private static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var mainForm = new MainForm();
            Application.Run(mainForm);

            // TODO: Use https://github.com/jaredpar/roslyn-codedom
            System.Console.BackgroundColor = ConsoleColor.White;
            System.ComponentModel.INotifyPropertyChanged? c = null;
        }
    }
}
